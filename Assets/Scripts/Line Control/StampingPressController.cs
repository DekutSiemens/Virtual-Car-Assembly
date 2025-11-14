using UnityEngine;
using VME.IO;
using realvirtual;

/// <summary>
/// Stamping Press Controller - Revised Logic
/// 
/// OPERATIONAL FLOW:
/// 1. IDLE: Press waits for sheet placement
/// 2. SHEET_PLACED: Sheet detected, waiting for robot to clear
/// 3. READY: Sheet present + Robot clear + All safety OK → can stamp
/// 4. STAMPING_DOWN: Press moving down
/// 5. STAMPING_UP: Press moving up after bottoming
/// 6. DONE: Stamped part ready for pickup
/// 7. Back to IDLE when sheet removed
/// 
/// KEY RULES:
/// - Only ONE stamp per sheet placement
/// - Must wait for sheet removal before accepting new sheet
/// - Robot must be clear before and during stamping
/// </summary>
[DefaultExecutionOrder(-10)]
public class StampingPressController : MonoBehaviour
{
    // ---------- Lifecycle ----------
    [Header("Lifecycle (Inputs)")]
    public BoolIn Cmd_Start;
    public BoolIn Cmd_Stop;
    public BoolIn Cmd_Reset;
    public BoolIn EStop_OK;

    [Header("Mode (true=AUTO, false=MANUAL)")]
    public BoolIn Mode_Auto;

    // ---------- Sensors ----------
    [Header("Sensors from cell (Inputs)")]
    [Tooltip("TRUE when a sheet/part is on the die")]
    public BoolIn S_SheetAtPress;
    [Tooltip("TRUE when gripper is clear of press area")]
    public BoolIn S_RobotClear;

    // ---------- Optional PnP pulse ----------
    [Header("PnP → Press (Inputs) - Optional")]
    [Tooltip("Optional: Pulse when PnP finishes placing")]
    public BoolIn PlaceDone;

    // ---------- Cylinder ----------
    [Header("Cylinder (PLC signals → Drive_Cylinder)")]
    public PLCOutputBool Cyl_Out;      // true=DOWN, false=UP
    public PLCInputBool Cyl_IsOut;     // bottom sensor
    public PLCInputBool Cyl_IsIn;      // top sensor

    // ---------- Outputs ----------
    [Header("Signals to HMI (Outputs)")]
    public BoolOut Press_Busy;
    public BoolOut Press_Fault;
    public BoolOut Press_ReadyForPick;

    // ---------- Timers ----------
    [Header("Watchdogs & Delays (seconds)")]
    public float WD_Down_s = 5.0f;
    public float WD_Up_s = 5.0f;
    [Tooltip("Time to wait after sheet+clear both true before allowing stamp (debounce)")]
    public float ReadyDebounce_s = 0.2f;

    [Header("Debug")]
    public bool VerboseLogs = true;

    // ---------- FSM ----------
    private enum State
    {
        FAULT,              // Error state, needs reset
        IDLE,               // Waiting for sheet
        SHEET_PLACED,       // Sheet detected, waiting for robot to clear
        READY,              // Ready to stamp (sheet present, robot clear, debounced)
        STAMPING_DOWN,      // Press moving down
        STAMPING_UP,        // Press returning up
        DONE,               // Stamped, waiting for pickup
        HOLD                // Stopped by operator
    }

    [SerializeField] private State _state = State.IDLE;
    private State _prevState;
    private float _stateTime;

    // ---------- Internal flags ----------
    private bool _eStopLatched;
    private bool _sheetPrev;            // For edge detection
    private bool _robotClearPrev;       // For edge detection
    private bool _hasStampedThisSheet;  // Prevents re-stamping same sheet
    private float _readyDebounceTimer;  // Timer for stable ready conditions

    private float Now => Time.time;

    void Start()
    {
        Log("=== Stamping Press Controller Started ===");
        EnterState(State.IDLE);
    }

    void FixedUpdate()
    {
        // Sample all inputs
        SampleInputs();

        // Read current sensor states
        bool sheetPresent = S_SheetAtPress != null && S_SheetAtPress.v;
        bool robotClear = S_RobotClear != null && S_RobotClear.v;
        bool eStopOK = EStop_OK != null && EStop_OK.v;
        bool isAuto = Mode_Auto != null && Mode_Auto.v;

        // Edge detection
        bool sheetRising = !_sheetPrev && sheetPresent;
        bool sheetFalling = _sheetPrev && !sheetPresent;
        bool robotClearRising = !_robotClearPrev && robotClear;

        _sheetPrev = sheetPresent;
        _robotClearPrev = robotClear;

        // ===== E-STOP HANDLING =====
        if (!eStopOK)
        {
            if (!_eStopLatched)
            {
                LogWarn("E-Stop triggered! Latching fault.");
                _eStopLatched = true;
                EnterState(State.FAULT);
            }
        }

        // ===== RESET COMMAND =====
        if (Cmd_Reset != null && Cmd_Reset.Rising)
        {
            Log("Reset command received - clearing faults");
            _eStopLatched = false;
            _hasStampedThisSheet = false;
            ClearFault();
            CommandUp();
            EnterState(State.IDLE);
            return;
        }

        // ===== STOP COMMAND =====
        if (Cmd_Stop != null && Cmd_Stop.v && _state != State.FAULT)
        {
            if (_state != State.HOLD)
            {
                Log("Stop command - entering HOLD");
                CommandUp();
                SetBusy(false);
                EnterState(State.HOLD);
            }
        }

        // ===== SHEET REMOVAL DETECTION =====
        // When sheet is removed, clear the "stamped" flag to allow new cycle
        if (sheetFalling)
        {
            Log("Sheet removed - ready for new sheet");
            _hasStampedThisSheet = false;
            SetReadyForPick(false);
        }

        // ===== STATE MACHINE =====
        _stateTime += Time.fixedDeltaTime;

        switch (_state)
        {
            case State.FAULT:
                HandleFaultState();
                break;

            case State.IDLE:
                HandleIdleState(sheetPresent, robotClear, sheetRising);
                break;

            case State.SHEET_PLACED:
                HandleSheetPlacedState(sheetPresent, robotClear, robotClearRising);
                break;

            case State.READY:
                HandleReadyState(sheetPresent, robotClear, isAuto);
                break;

            case State.STAMPING_DOWN:
                HandleStampingDownState(robotClear);
                break;

            case State.STAMPING_UP:
                HandleStampingUpState(sheetPresent);
                break;

            case State.DONE:
                HandleDoneState(sheetPresent);
                break;

            case State.HOLD:
                HandleHoldState();
                break;
        }
    }

    // ==================== STATE HANDLERS ====================

    void HandleFaultState()
    {
        CommandUp();
        SetBusy(false);
        SetReadyForPick(false);
        // Exit via Reset only
    }

    void HandleIdleState(bool sheetPresent, bool robotClear, bool sheetRising)
    {
        CommandUp();
        SetBusy(false);
        SetReadyForPick(false);

        // Wait for sheet arrival
        if (sheetRising)
        {
            Log("Sheet detected → SHEET_PLACED");
            EnterState(State.SHEET_PLACED);
        }
        // Handle case where sheet is already present at startup
        else if (sheetPresent && robotClear && _stateTime > ReadyDebounce_s && !_hasStampedThisSheet)
        {
            Log("Sheet already present at startup → READY");
            EnterState(State.READY);
        }
    }

    void HandleSheetPlacedState(bool sheetPresent, bool robotClear, bool robotClearRising)
    {
        CommandUp();
        SetBusy(false);

        // Sheet removed before robot cleared - back to idle
        if (!sheetPresent)
        {
            Log("Sheet removed before stamping → IDLE");
            EnterState(State.IDLE);
            return;
        }

        // Wait for robot to clear
        if (robotClear)
        {
            // Robot is clear, start debounce timer
            if (_stateTime >= ReadyDebounce_s)
            {
                // Stable for debounce period
                if (!_hasStampedThisSheet && SafetyOK() && IsAtTop())
                {
                    Log("Robot cleared, conditions stable → READY");
                    EnterState(State.READY);
                }
            }
        }
        else
        {
            // Robot not clear yet, reset timer
            _stateTime = 0f;
        }
    }

    void HandleReadyState(bool sheetPresent, bool robotClear, bool isAuto)
    {
        CommandUp();
        SetBusy(false);

        // Safety check - if conditions degrade, go back
        if (!sheetPresent)
        {
            Log("Sheet removed while ready → IDLE");
            EnterState(State.IDLE);
            return;
        }

        if (!robotClear)
        {
            LogWarn("Robot entered area while ready → SHEET_PLACED");
            EnterState(State.SHEET_PLACED);
            return;
        }

        if (!SafetyOK() || !IsAtTop())
        {
            LogWarn("Safety or position issue → IDLE");
            EnterState(State.IDLE);
            return;
        }

        // Check for start command
        bool startCommand = false;
        if (isAuto)
        {
            // AUTO mode: level-triggered
            startCommand = Cmd_Start != null && Cmd_Start.v;
        }
        else
        {
            // MANUAL mode: edge-triggered
            startCommand = Cmd_Start != null && Cmd_Start.Rising;
        }

        if (startCommand)
        {
            Log("Start command received → BEGIN STAMPING");
            _hasStampedThisSheet = true;  // Mark this sheet as being stamped
            CommandDown();
            SetBusy(true);
            EnterState(State.STAMPING_DOWN);
        }
    }

    void HandleStampingDownState(bool robotClear)
    {
        SetBusy(true);

        // Keep commanding DOWN until we reach bottom
        CommandDown();

        // Safety: robot intrusion during stamping
        if (!robotClear)
        {
            Fault("Robot entered press area during stamping!");
            return;
        }

        // Check if reached bottom
        if (IsAtBottom())
        {
            Log("Reached bottom → reversing");
            EnterState(State.STAMPING_UP);
            return;
        }

        // Watchdog timeout
        if (_stateTime > WD_Down_s)
        {
            Fault($"Down stroke timeout ({WD_Down_s}s exceeded)");
        }
    }

    void HandleStampingUpState(bool sheetPresent)
    {
        SetBusy(true);

        // Keep commanding UP until we reach top
        CommandUp();

        // Check if reached top
        if (IsAtTop())
        {
            Log("Reached top → DONE");
            SetBusy(false);

            // Only set ready if sheet is still present
            if (sheetPresent)
            {
                SetReadyForPick(true);
                EnterState(State.DONE);
            }
            else
            {
                Log("Sheet removed during return → IDLE");
                EnterState(State.IDLE);
            }
            return;
        }

        // Watchdog timeout
        if (_stateTime > WD_Up_s)
        {
            Fault($"Up stroke timeout ({WD_Up_s}s exceeded)");
        }
    }

    void HandleDoneState(bool sheetPresent)
    {
        CommandUp();
        SetBusy(false);

        // Update ready signal based on sheet presence
        SetReadyForPick(sheetPresent);

        // Wait for sheet to be removed
        if (!sheetPresent)
        {
            Log("Stamped part picked → IDLE");
            SetReadyForPick(false);
            EnterState(State.IDLE);
        }
    }

    void HandleHoldState()
    {
        CommandUp();
        SetBusy(false);

        // Exit hold when stop released
        if (Cmd_Stop == null || !Cmd_Stop.v)
        {
            Log("Stop released → IDLE");
            EnterState(State.IDLE);
        }
    }

    // ==================== HELPERS ====================

    void SampleInputs()
    {
        if (Cmd_Start != null) Cmd_Start.Sample();
        if (Cmd_Stop != null) Cmd_Stop.Sample();
        if (Cmd_Reset != null) Cmd_Reset.Sample();
        if (EStop_OK != null) EStop_OK.Sample();
        if (Mode_Auto != null) Mode_Auto.Sample();
        if (S_SheetAtPress != null) S_SheetAtPress.Sample();
        if (S_RobotClear != null) S_RobotClear.Sample();
        if (PlaceDone != null) PlaceDone.Sample();
    }

    bool SafetyOK()
    {
        bool eStopOK = EStop_OK != null && EStop_OK.v;
        return eStopOK && !_eStopLatched;
    }

    bool IsAtTop()
    {
        return Cyl_IsIn != null && Cyl_IsIn.Value;
    }

    bool IsAtBottom()
    {
        return Cyl_IsOut != null && Cyl_IsOut.Value;
    }

    void CommandDown()
    {
        if (Cyl_Out != null)
        {
            Cyl_Out.Value = true;
            Log("→ Cylinder DOWN");
        }
    }

    void CommandUp()
    {
        if (Cyl_Out != null)
        {
            Cyl_Out.Value = false;
            Log("→ Cylinder UP");
        }
    }

    void SetBusy(bool busy)
    {
        if (Press_Busy != null && Press_Busy.tag != null)
            Press_Busy.Set(busy);
    }

    void SetReadyForPick(bool ready)
    {
        if (Press_ReadyForPick != null && Press_ReadyForPick.tag != null)
            Press_ReadyForPick.Set(ready);
    }

    void SetFault(bool fault)
    {
        if (Press_Fault != null && Press_Fault.tag != null)
            Press_Fault.Set(fault);
    }

    void ClearFault()
    {
        SetFault(false);
    }

    void EnterState(State newState)
    {
        _prevState = _state;
        _state = newState;
        _stateTime = 0f;

        Log($">>> STATE: {_prevState} → {_state}");
    }

    void Fault(string reason)
    {
        LogWarn($"FAULT: {reason}");
        SetFault(true);
        CommandUp();
        SetBusy(false);
        EnterState(State.FAULT);
    }

    void Log(string msg)
    {
        if (VerboseLogs)
            Debug.Log($"[Press] {msg}");
    }

    void LogWarn(string msg)
    {
        Debug.LogWarning($"[Press] {msg}");
    }
}