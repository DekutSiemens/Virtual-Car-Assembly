using UnityEngine;
using VME.IO; // BoolIn/BoolOut

/// CutterController — exit-edge aware, one stroke per properly positioned piece.
/// - AUTO: requires Cmd_Start=1, GuardOK, BladeUp, and PE_Exit HIGH *for PreCutDelaySec*
///         (triggered by PE_Exit.Rising; waits the delay before CUT_DOWN).
/// - MANUAL: Cmd_Start.Rising arms a pending stroke, which will only start once
///           PE_Exit is HIGH *for PreCutDelaySec* (still requires GuardOK & BladeUp).
/// - Re-arm only on PE_Exit.Falling (or EXIT LOW at reset).
/// - No spawning, no infeed control.
/// Shared signals policy:
///   - Mode_Auto: single bit (true=AUTO, false=MANUAL)
///   - Cmd_Start / Cmd_Stop / Cmd_Reset / EStop_OK: same BoolIn tags used across all controllers
public class CutterController : MonoBehaviour
{
    // ===== Shared lifecycle (inputs)
    [Header("Lifecycle (Inputs)")]
    public BoolIn Cmd_Start;   // shared
    public BoolIn Cmd_Stop;    // shared
    public BoolIn Cmd_Reset;   // shared
    public BoolIn EStop_OK;    // shared
    public BoolIn Mode_Auto;   // true=AUTO, false=MANUAL

    // ===== Safety & limits (inputs)
    [Header("Safety & Limits (Inputs)")]
    public BoolIn GuardOK;     // guard interlock
    public BoolIn BladeUp;     // top limit
    public BoolIn BladeDown;   // bottom limit

    // ===== Positioning (inputs)
    [Header("Cutter Exit Sensor (Input)")]
    public BoolIn PE_Exit;     // ONLY sensor used for sequencing (edges)

    // ===== Actuators (outputs)
    [Header("Blade Actuation (Outputs)")]
    public BoolOut Blade_JogDown;
    public BoolOut Blade_JogUp;

    // ===== Handshake / HMI (outputs)
    [Header("Handshake / HMI (Outputs)")]
    [Tooltip("Pulses HIGH once when a full cut cycle completes (BladeDown->BladeUp).")]
    public BoolOut CutComplete;   // wire to PLCOutputBool on a SignalBus
    public BoolOut CutterBusy;    // on during CUT_DOWN / CUT_UP
    public BoolOut CutterFault;   // latched in FAULT

    [Header("Pulse Settings")]
    [Tooltip("Duration of the CutComplete pulse (seconds).")]
    public float CutCompletePulse_s = 0.05f;

    // ===== Timing
    [Header("Pre-Cut Timing")]
    [Tooltip("How long PE_Exit must remain HIGH before the cut begins.")]
    public float PreCutDelaySec = 0.20f;

    [Tooltip("Small debounce to accept PE_Exit as 'HIGH'. Set to 0 to disable.")]
    public float ExitHighDebounceSec = 0.02f;

    // ===== Watchdogs
    [Header("Watchdogs (seconds)")]
    [Tooltip("Max time allowed for the blade to go DOWN to BladeDown.")]
    public float WD_CutDown_s = 5.0f;
    [Tooltip("Max time allowed for the blade to go UP to BladeUp.")]
    public float WD_CutUp_s = 5.0f;

    // ===== Diagnostics
    [Header("Diagnostics")]
    public bool VerboseLogs = false;

    // ===== FSM
    private enum S { RESET, ARMED, PENDING_START, CUT_DOWN, CUT_UP, HOLD, FAULT }
    [SerializeField] private S _s = S.RESET;
    private S _prev;

    // ===== Internals
    private bool _eStopLatched;
    private bool _armed;              // ready to accept next cycle (AUTO re-armed on EXIT falling)
    private float _stateTimer;

    // Exit-high timing
    private bool _exitHighValid;     // becomes true once debounce satisfied
    private float _exitHighSince;     // when PE_Exit went HIGH
    private float _exitHighValidAt;   // when it becomes valid (Rising + debounce)

    // Pending start (after conditions met, we still wait PreCutDelaySec)
    private bool _pendingStart;
    private float _startAt;           // Now + PreCutDelaySec

    // pulse timer
    private float _cutCompleteT = 0f;

    float Now => Time.time;

    // ===== Unity
    void Start() => Enter(S.RESET);

    void FixedUpdate()
    {
        // Sample all inputs
        Cmd_Start.Sample(); Cmd_Stop.Sample(); Cmd_Reset.Sample(); EStop_OK.Sample();
        Mode_Auto.Sample();
        GuardOK.Sample(); BladeUp.Sample(); BladeDown.Sample();
        PE_Exit.Sample();

        // Hard E-Stop latch & reset
        if (!EStop_OK.v) { _eStopLatched = true; Enter(S.RESET); }
        if (Cmd_Reset.Rising) { _eStopLatched = false; Enter(S.RESET); }

        // Track EXIT HIGH window & debounce
        if (PE_Exit.Rising)
        {
            _exitHighSince = Now;
            _exitHighValid = false;
            _exitHighValidAt = Now + Mathf.Max(0f, ExitHighDebounceSec);
        }
        if (PE_Exit.v && !_exitHighValid && Now >= _exitHighValidAt)
        {
            _exitHighValid = true; // EXIT has been HIGH long enough to be trusted
        }

        // Re-arm only when EXIT falls (piece left)
        if (PE_Exit.Falling)
        {
            _armed = true;
            _exitHighValid = false;
            _pendingStart = false; // cancel any pending start if piece left
        }

        // Boundary STOP behavior
        if (Cmd_Stop.v && _s != S.FAULT)
        {
            if (_s == S.ARMED || _s == S.PENDING_START || _s == S.HOLD)
            {
                Enter(S.HOLD);
                // don't return; we still service the completion pulse timer at end
            }
        }

        // FSM
        switch (_s)
        {
            case S.RESET:
                {
                    OutputsOff(); PulseOff();
                    ClearFaultLamp();

                    // armed at reset iff EXIT is LOW
                    _armed = !PE_Exit.v;
                    _pendingStart = false;
                    _exitHighValid = PE_Exit.v && (ExitHighDebounceSec <= 0f); // if already high & no debounce, treat as valid

                    if (!_eStopLatched && EStop_OK.v)
                        Enter(S.ARMED);
                    break;
                }

            case S.ARMED:
                {
                    if (Cmd_Stop.v) { Enter(S.HOLD); break; }

                    OutputsOff(); SetBusyLamp(false);

                    // Decide whether to schedule a start
                    bool safetyOK = GuardOK.v && BladeUp.v && !_eStopLatched && EStop_OK.v;

                    if (Mode_Auto.v)
                    {
                        // AUTO: require Start level, armed, and EXIT HIGH (valid) — schedule delayed start
                        if (Cmd_Start.v && _armed && safetyOK && _exitHighValid)
                        {
                            ScheduleStart();
                            Enter(S.PENDING_START);
                            break;
                        }
                    }
                    else
                    {
                        // MANUAL: on Start Rising, arm a pending start; will begin when EXIT goes HIGH & valid
                        if (Cmd_Start.Rising)
                        {
                            _armed = true; // manual doesn’t depend on rearm, but keep consistent
                            _pendingStart = true;
                        }

                        if (_pendingStart && safetyOK && _exitHighValid)
                        {
                            ScheduleStart();
                            Enter(S.PENDING_START);
                            break;
                        }
                    }
                    break;
                }

            case S.PENDING_START:
                {
                    // Wait out the pre-cut delay; ensure conditions still acceptable
                    OutputsOff(); SetBusyLamp(false);

                    bool safetyOK = GuardOK.v && BladeUp.v && !_eStopLatched && EStop_OK.v;

                    // If EXIT dropped, cancel and go back to ARMED
                    if (!PE_Exit.v)
                    {
                        _pendingStart = false;
                        Enter(S.ARMED);
                        break;
                    }

                    // If STOP asserted, go HOLD; we’ll come back to ARMED
                    if (Cmd_Stop.v)
                    {
                        Enter(S.HOLD);
                        break;
                    }

                    if (safetyOK && Now >= _startAt)
                    {
                        _pendingStart = false;
                        _armed = false;         // consume this cycle
                        Enter(S.CUT_DOWN);
                    }
                    break;
                }

            case S.CUT_DOWN:
                {
                    if (!GuardOK.v) { Fault("Guard opened during CUT_DOWN"); break; }

                    Blade_JogDown.Set(true);
                    Blade_JogUp.Set(false);
                    SetBusyLamp(true);

                    if (BladeDown.v)
                    {
                        Enter(S.CUT_UP);
                        break;
                    }

                    if (Watchdog(WD_CutDown_s, "CUT_DOWN timeout")) break;
                    break;
                }

            case S.CUT_UP:
                {
                    Blade_JogDown.Set(false);
                    Blade_JogUp.Set(true);
                    SetBusyLamp(true);

                    if (BladeUp.v && !BladeDown.v)
                    {
                        // Stroke complete
                        SetBusyLamp(false);
                        Blade_JogUp.Set(false);

                        // completion pulse
                        FireCutCompletePulse();

                        Enter(S.ARMED);
                        if (Cmd_Stop.v) Enter(S.HOLD);
                        break;
                    }

                    if (Watchdog(WD_CutUp_s, "CUT_UP timeout")) break;
                    break;
                }

            case S.HOLD:
                {
                    OutputsOff(); SetBusyLamp(false);
                    _pendingStart = false; // cancel any pending sequence while on HOLD
                    if (!Cmd_Stop.v) Enter(S.ARMED);
                    break;
                }

            case S.FAULT:
                {
                    OutputsOff(); SetBusyLamp(false); SetFaultLamp(true);
                    // leave via Reset
                    break;
                }
        }

        // Service CutComplete pulse timing every tick
        ServicePulseTimer();
    }

    // ===== Helpers =====
    void ScheduleStart()
    {
        _pendingStart = true;
        _startAt = Now + Mathf.Max(0f, PreCutDelaySec);
        if (VerboseLogs) Debug.Log($"[Cutter] Pre-cut delay scheduled: starts at t={_startAt:0.000}");
    }

    void Enter(S next)
    {
        _prev = _s;
        _s = next;
        _stateTimer = 0f;
        if (VerboseLogs) Debug.Log($"[Cutter] {_prev} → {_s}");
    }

    void OutputsOff()
    {
        Blade_JogDown.Set(false);
        Blade_JogUp.Set(false);
    }

    bool Watchdog(float maxSec, string label)
    {
        _stateTimer += Time.fixedDeltaTime;
        if (_stateTimer > maxSec)
        {
            Fault(label);
            return true;
        }
        return false;
    }

    void Fault(string why)
    {
        if (VerboseLogs) Debug.LogWarning($"[Cutter] FAULT: {why}");
        PulseOff();
        Enter(S.FAULT);
    }

    void SetBusyLamp(bool on)
    {
        if (CutterBusy.tag != null) CutterBusy.Set(on);
    }

    void SetFaultLamp(bool on)
    {
        if (CutterFault.tag != null) CutterFault.Set(on);
    }

    void ClearFaultLamp() => SetFaultLamp(false);

    // ---- CutComplete pulse helpers ----
    void FireCutCompletePulse()
    {
        _cutCompleteT = Mathf.Max(0.01f, CutCompletePulse_s);
        if (CutComplete.tag != null) CutComplete.Set(true);
        if (VerboseLogs) Debug.Log("[Cutter] CutComplete ↑ pulse");
    }

    void ServicePulseTimer()
    {
        if (_cutCompleteT > 0f)
        {
            _cutCompleteT -= Time.fixedDeltaTime;
            if (_cutCompleteT <= 0f) PulseOff();
        }
    }

    void PulseOff()
    {
        _cutCompleteT = 0f;
        if (CutComplete.tag != null) CutComplete.Set(false);
    }
}
