using UnityEngine;
using System;
using System.Reflection;
using VME.IO;   // wrappers (BoolIn/BoolOut/FloatIn/FloatOut)

public class PickPlaceSequencer : MonoBehaviour
{
    // ---------------- Lifecycle ----------------
    [Header("Lifecycle (Inputs)")]
    public BoolIn Cmd_Start;       // may stay HIGH continuously
    public BoolIn Cmd_Stop;
    public BoolIn Cmd_Reset;
    public BoolIn EStop_OK;

    [Header("Controller Status (Outputs, optional)")]
    public BoolOut PnP_Busy;
    public BoolOut PnP_Done; // Pulses high for one frame when a cycle completes

    // ---------------- Start gating ----------------
    [Header("Cycle Trigger Inputs")]
    [Tooltip("Presence sensor: TRUE when a NEW sheet is at the pick position (from Outfeed)")]
    public BoolIn SheetAtPick;

    [Header("Press Handshake Inputs")]
    [Tooltip("Presence sensor: TRUE when a STAMPED part is ready for pickup (from Press)")]
    public BoolIn StampedPartAtPress;
    [Tooltip("Presence sensor: TRUE when ANY sheet is on the press die (wire to S_SheetAtPress)")]
    public BoolIn SheetOnPress;
    [Tooltip("Logic signal: TRUE when press is stamping (Bridge from StampingPressController.Press_Busy)")] // <-- NEW
    public BoolIn PressIsBusy; // <-- NEW

    [Tooltip("If true, a new cycle only starts when Cmd_Start is HIGH AND a sheet sensor is HIGH.")]
    public bool GateStartBySheetSensor = true;

    // ---------------- Grip (optional single-bit pick/place) ----------------
    [Header("Grip (optional)")]
    public BoolOut Grip_Pick;   // single bit; we just set/reset (no pulse timing)
    public BoolOut Grip_Place;

    // ---------------- Link IO Bundles ------------------
    public enum Link { X = 0, Y = 1, Z = 2, R = 3 }

    [Serializable]
    public class LinkIO
    {
        [Header("Outputs")]
        public FloatOut DestinationIndex;
        public FloatOut TargetSpeed_mmps;
        public BoolOut StartDrive;

        [Header("Inputs")]
        public BoolIn IsAtPosition;
        public BoolIn IsDriving;
        public FloatIn PositionIndex;
    }

    [Header("Links (assign ALL 4)")]
    public LinkIO LinkX, LinkY, LinkZ, LinkR;

    // ---------------- Program --------------------------
    public enum WaitPolicy { WaitAtPosition, DwellOnly }
    public enum GripAction { Off, PickBeforeDwell, PlaceAfterDwell }

    [Serializable]
    public class Step
    {
        [Header("Move")]
        public Link Link;
        public float DestinationIndex = 0;
        public float TargetSpeed_mmps = 400f;

        [Header("Completion & Timing")]
        public WaitPolicy Wait = WaitPolicy.WaitAtPosition;
        public float Dwell_s = 0f;
        public float WD_Move_s = 3.0f;

        [Header("Grip action")]
        public GripAction Grip = GripAction.Off;
    }

    [Header("Program A: Pick from Conveyor -> Place in Press")]
    public Step[] Steps_PickFromConveyor;

    [Header("Program B: Pick from Press -> Place at Exit")]
    public Step[] Steps_PickFromPress;

    // ---------------- Behaviour Options ----------------
    [Header("Controller Options")]
    public float MinStartPulse_s = 0.10f;
    public float SettleAfterAtPos_s = 0.05f;
    public bool MirrorWritesToOverride = true;
    public bool DryRun = false;

    // ---------------- Debug ----------------------------
    [Header("Debug")]
    public bool VerboseLogs = true;
    public float LogInterval_s = 0.25f;
    float _nextLogAt;

    // ---------------- FSM ------------------------------
    // High-level "Job" controller
    enum CycleState { RESET, IDLE, RUNNING_CONVEYOR_CYCLE, RUNNING_PRESS_CYCLE, FAULT }
    // Low-level "Step" executor
    enum ExecState { PREP, RUN, DWELL, DONE }

    [SerializeField] CycleState CState = CycleState.RESET;
    [SerializeField] ExecState EState = ExecState.DONE;
    CycleState _prevCState;
    ExecState _prevEState;


    // ---------------- Internals ------------------------
    bool eStopLatched;
    bool runEnable;
    int stepIdx;
    float stateTimer_C; // Cycle state timer
    float stateTimer_E; // Executor state timer
    float settleTimer;
    float pulseHoldUntil;

    // cached for current step
    Step cur;
    LinkIO cio;
    Step[] activeProgram; // Points to the program we are currently running

    // ===================================================
    // Unity
    // ===================================================
    void Start()
    {
        EnterCState(CycleState.RESET);
    }

    void FixedUpdate()
    {
        SampleInputs();

        // --- E-Stop & Reset ---
        if (!EStop_OK.v)
        {
            if (!eStopLatched && VerboseLogs) Debug.LogWarning("[PnP] E-STOP drop -> latched");
            eStopLatched = true;
            SafeOutputs();
            EnterCState(CycleState.RESET);
        }

        if (Cmd_Reset.Rising)
        {
            eStopLatched = false;
            EnterCState(CycleState.RESET);
        }

        runEnable = (!eStopLatched) && EStop_OK.v;

        // --- Top-Level State Machine (Cycles) ---
        stateTimer_C += Time.fixedDeltaTime;

        // Clear Done pulse after one frame
        if (PnP_Done?.tag == true && PnP_Done.Get())
        {
            PnP_Done.Set(false);
        }

        switch (CState)
        {
            case CycleState.RESET:
                Tick_RESET();
                break;
            case CycleState.IDLE:
                Tick_IDLE();
                break;
            case CycleState.RUNNING_CONVEYOR_CYCLE:
            case CycleState.RUNNING_PRESS_CYCLE:
                Tick_RUNNING(); // This function runs the sub-state machine
                break;
            case CycleState.FAULT:
                Tick_FAULT();
                break;
        }

        MaintainStartPulse();

        if (VerboseLogs && Time.time >= _nextLogAt)
        {
            _nextLogAt = Time.time + Mathf.Max(0.05f, LogInterval_s);
            LogTick();
        }
    }

    // ===================================================
    // Top-Level State logic
    // ===================================================

    void Tick_RESET()
    {
        SafeOutputs();
        if (PnP_Busy?.tag) PnP_Busy.Set(false);
        if (PnP_Done?.tag) PnP_Done.Set(false);

        stepIdx = 0;
        activeProgram = null;
        EState = ExecState.DONE; // Ensure executor is idle

        if (runEnable)
        {
            EnterCState(CycleState.IDLE);
        }
    }

    void Tick_IDLE()
    {
        if (PnP_Busy?.tag) PnP_Busy.Set(false);
        if (!runEnable) { EnterCState(CycleState.RESET); return; }
        if (Cmd_Stop.v) return; // Stay idle if Stop is held

        // Check for start command
        bool startActive = GateStartBySheetSensor ? Cmd_Start.v : (Cmd_Start.Rising || Cmd_Start.v);

        // --- PRIORITY LOGIC ---
        // 1. Always clear the press first.
        if (StampedPartAtPress.v && startActive && (Steps_PickFromPress?.Length > 0))
        {
            Log("Stamped part detected. Starting PRESS_CYCLE.");
            StartExecutor(Steps_PickFromPress, CycleState.RUNNING_PRESS_CYCLE);
        }
        // 2. Only if press is clear AND EMPTY AND NOT BUSY, check for a new sheet.
        else if (SheetAtPick.v && !SheetOnPress.v && !PressIsBusy.v && startActive && (Steps_PickFromConveyor?.Length > 0)) // <-- MODIFIED
        {
            Log("New sheet detected AND press is empty/idle. Starting CONVEYOR_CYCLE.");
            StartExecutor(Steps_PickFromConveyor, CycleState.RUNNING_CONVEYOR_CYCLE);
        }
    }

    void StartExecutor(Step[] program, CycleState runningState)
    {
        if (program == null || program.Length == 0) { Fault("No steps configured for this cycle"); return; }

        activeProgram = program;
        stepIdx = 0;
        if (PnP_Busy?.tag) PnP_Busy.Set(true);

        EnterCState(runningState);
        EnterEState(ExecState.PREP); // Start the executor
    }

    // This function manages the execution of the active program
    void Tick_RUNNING()
    {
        if (!runEnable) { Fault("Run lost during cycle"); return; }
        if (Cmd_Stop.v) { EnterCState(CycleState.IDLE); return; } // Stop command aborts cycle, returns to IDLE

        if (PnP_Busy?.tag) PnP_Busy.Set(true);

        // --- Sub-State Machine (Executor) ---
        stateTimer_E += Time.fixedDeltaTime;

        switch (EState)
        {
            case ExecState.PREP:
                Tick_EXEC_PREP();
                break;
            case ExecState.RUN:
                Tick_EXEC_RUN();
                break;
            case ExecState.DWELL:
                Tick_EXEC_DWELL();
                break;
            case ExecState.DONE:
                Tick_EXEC_DONE();
                break;
        }
    }

    void Tick_FAULT()
    {
        SafeOutputs();
        if (PnP_Busy?.tag) PnP_Busy.Set(false);
        // wait for Reset
    }

    // ===================================================
    // Sub-State (Executor) logic
    // ===================================================

    void Tick_EXEC_PREP()
    {
        if (stepIdx >= (activeProgram?.Length ?? 0))
        {
            EnterEState(ExecState.DONE); // Program finished
            return;
        }

        cur = activeProgram[stepIdx];
        cio = GetLinkIO(cur.Link);
        if (cio == null) { Fault($"Link IO missing for {cur.Link} in step {stepIdx}"); return; }

        // Command move
        TrySetFloatOutput(cio.TargetSpeed_mmps, cur.TargetSpeed_mmps);
        TrySetFloatOutput(cio.DestinationIndex, cur.DestinationIndex);
        PulseStartOnly(cio.StartDrive);

        // reset per-move timers
        settleTimer = 0f;

        if (VerboseLogs)
            Debug.Log($"[PnP] Step {stepIdx + 1}/{activeProgram.Length}: {cur.Link} -> Dest={cur.DestinationIndex}, v={cur.TargetSpeed_mmps} (Wait={cur.Wait}, Dwell={cur.Dwell_s}, Grip={cur.Grip})");

        EnterEState(ExecState.RUN);
    }

    void EnterDwellAndPick()
    {
        // Assert PICK exactly when we enter DWELL (post-position/settle)
        if (cur.Grip == GripAction.PickBeforeDwell)
        {
            TrySetBoolOutput(Grip_Place, false);
            TrySetBoolOutput(Grip_Pick, true);
            if (VerboseLogs) Debug.Log("[PnP] Grip_Pick=TRUE at DWELL entry.");
        }

        EnterEState(ExecState.DWELL);
    }

    void Tick_EXEC_RUN()
    {
        if (cur.Wait == WaitPolicy.WaitAtPosition)
        {
            if (cio.IsAtPosition.v)
            {
                settleTimer += Time.fixedDeltaTime;
                if (settleTimer >= Mathf.Max(0, SettleAfterAtPos_s))
                {
                    EnterDwellAndPick(); // <-- pick at dwell entry
                    return;
                }
            }
        }
        else
        {
            // DwellOnly policy: move immediately to DWELL
            EnterDwellAndPick();
            return;
        }

        // Watchdog
        if (stateTimer_E > Mathf.Max(0.05f, cur.WD_Move_s))
            Fault($"Move WD: {cur.Link} Dest={cur.DestinationIndex} atPos={cio.IsAtPosition.v} drv={cio.IsDriving.v}");
    }

    void Tick_EXEC_DWELL()
    {
        if (stateTimer_E >= Mathf.Max(0, cur.Dwell_s))
        {
            // Grip action AFTER dwell (place)
            if (cur.Grip == GripAction.PlaceAfterDwell)
            {
                TrySetBoolOutput(Grip_Pick, false);
                TrySetBoolOutput(Grip_Place, true);
                if (VerboseLogs) Debug.Log("[PnP] Grip_Place=TRUE after dwell.");
            }

            stepIdx++; // Move to next step
            EnterEState(ExecState.PREP); // Go prepare next step
        }
    }

    // This state is reached when the program (step list) is finished
    void Tick_EXEC_DONE()
    {
        Log("Program execution finished.");

        // One-frame done pulse
        if (PnP_Done?.tag)
        {
            PnP_Done.Set(true);
        }

        // Cycle complete, return to IDLE to look for the next job
        EnterCState(CycleState.IDLE);
    }

    // ===================================================
    // Helpers
    // ===================================================
    LinkIO GetLinkIO(Link link)
    {
        switch (link)
        {
            case Link.X: return LinkX;
            case Link.Y: return LinkY;
            case Link.Z: return LinkZ;
            case Link.R: return LinkR;
        }
        return null;
    }

    void PulseStartOnly(BoolOut startOut)
    {
        ForceAllStartFalse();
        if (startOut?.tag == null) return;

        if (!DryRun)
        {
            TrySetBoolOutput(startOut, true);
            pulseHoldUntil = Time.time + Mathf.Max(0.02f, MinStartPulse_s);
        }
        if (VerboseLogs)
            Debug.Log($"[PnP]  StartDrive ↑ (hold {MinStartPulse_s:0.000}s)");
    }

    void MaintainStartPulse()
    {
        if (Time.time >= pulseHoldUntil)
            ForceAllStartFalse();
    }

    void ForceAllStartFalse()
    {
        TrySetBoolOutput(LinkX?.StartDrive, false);
        TrySetBoolOutput(LinkY?.StartDrive, false);
        TrySetBoolOutput(LinkZ?.StartDrive, false);
        TrySetBoolOutput(LinkR?.StartDrive, false);
    }

    void SafeOutputs()
    {
        ForceAllStartFalse();
        TrySetBoolOutput(Grip_Pick, false);
        TrySetBoolOutput(Grip_Place, false);
    }

    void EnterCState(CycleState s)
    {
        if (CState == s) return;
        _prevCState = CState; CState = s;
        stateTimer_C = 0f;
        if (VerboseLogs)
            Debug.Log($"[PnP] CYCLE STATE: {_prevCState} -> {CState} @ {Time.time:0.000}s");
    }

    void EnterEState(ExecState s)
    {
        if (EState == s) return;
        _prevEState = EState; EState = s;
        stateTimer_E = 0f;
        if (VerboseLogs)
            Debug.Log($"[PnP] > Exec State: {_prevEState} -> {EState}");
    }

    void Fault(string why)
    {
        Debug.LogWarning($"[PnP] FAULT: {why} @ {Time.time:0.000}s (CState={CState}, EState={EState}, step={stepIdx})");
        EnterCState(CycleState.FAULT);
    }

    void Log(string msg)
    {
        if (VerboseLogs) Debug.Log($"[PnP] {msg}");
    }

    void SampleInputs()
    {
        Cmd_Start.Sample(); Cmd_Stop.Sample(); Cmd_Reset.Sample(); EStop_OK.Sample();
        SheetAtPick.Sample();
        StampedPartAtPress.Sample();
        SheetOnPress.Sample();
        PressIsBusy.Sample(); // <-- NEW

        LinkX.IsAtPosition.Sample(); LinkX.IsDriving.Sample(); LinkX.PositionIndex.Sample();
        LinkY.IsAtPosition.Sample(); LinkY.IsDriving.Sample(); LinkY.PositionIndex.Sample();
        LinkZ.IsAtPosition.Sample(); LinkZ.IsDriving.Sample(); LinkZ.PositionIndex.Sample();
        LinkR.IsAtPosition.Sample(); LinkR.IsDriving.Sample(); LinkR.PositionIndex.Sample();
    }

    void LogTick()
    {
        string curDesc = (activeProgram != null && stepIdx < activeProgram.Length)
            ? $"{activeProgram[stepIdx].Link}@{activeProgram[stepIdx].DestinationIndex}"
            : "-";

        Debug.Log(
            $"[PnP {Time.time:0.000}] CState={CState} | EState={EState} | Busy={(PnP_Busy?.Get() ?? false)} | " +
            $"step={stepIdx}/{(activeProgram != null ? activeProgram.Length : 0)} ({curDesc}) | " +
            $"TRIGGERS: SheetAtPick={SheetAtPick.v} | " + // <-- MODIFIED
            $"PRESS: StampedAtPress={StampedPartAtPress.v} SheetOnPress={SheetOnPress.v} IsBusy={PressIsBusy.v} | " + // <-- MODIFIED
            $"Grip(Pick={(Grip_Pick?.Get() ?? false)}, Place={(Grip_Place?.Get() ?? false)})"
        );
    }

    // ===================================================
    // Output writing (with optional Override mirroring)
    // ===================================================
    void TrySetFloatOutput(FloatOut outTag, float value)
    {
        if (outTag?.tag == null || DryRun) return;
        outTag.Set(value);
        if (MirrorWritesToOverride) TrySetOverride(outTag.tag, value);
    }

    void TrySetBoolOutput(BoolOut outTag, bool value)
    {
        if (outTag?.tag == null || DryRun) return;
        outTag.Set(value);
        if (MirrorWritesToOverride) TrySetOverride(outTag.tag, value ? 1f : 0f);
    }

    static void TrySetOverride(object plcOutput, float numeric)
    {
        if (plcOutput == null) return;
        var t = plcOutput.GetType();

        var fOverride = t.GetField("Override", BindingFlags.Public | BindingFlags.Instance);
        var pOverride = t.GetProperty("Override", BindingFlags.Public | BindingFlags.Instance);
        if (fOverride != null) fOverride.SetValue(plcOutput, true);
        if (pOverride != null && pOverride.CanWrite) pOverride.SetValue(plcOutput, true);

        var fVO = t.GetField("ValueOverride", BindingFlags.Public | BindingFlags.Instance);
        var pVO = t.GetProperty("ValueOverride", BindingFlags.Public | BindingFlags.Instance);
        if (fVO != null) fVO.SetValue(plcOutput, numeric);
        if (pVO != null && pVO.CanWrite) pVO.SetValue(plcOutput, numeric);
    }
}