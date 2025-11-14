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
    public BoolOut PnP_Done;

    // ---------------- Start gating ----------------
    [Header("Start gating (sheet present)")]
    [Tooltip("Presence sensor that is TRUE when the cut sheet is parked at the pick position.")]
    public BoolIn SheetAtPick;
    [Tooltip("If true, a new cycle only starts when Cmd_Start is HIGH AND SheetAtPick is HIGH.")]
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

    [Header("Program (runs top → bottom)")]
    public Step[] Steps;

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
    enum STATE { RESET, EXEC_PREP, EXEC_RUN, DWELL, DONE, FAULT }
    [SerializeField] STATE State = STATE.RESET;
    STATE _prev;

    // ---------------- Internals ------------------------
    bool eStopLatched;
    bool runEnable;
    int stepIdx;
    float stateTimer;
    float settleTimer;
    float pulseHoldUntil;


    // cached for current step
    Step cur;
    LinkIO cio;

    // ===================================================
    // Unity
    // ===================================================
    void Start()
    {
        Enter(STATE.RESET);
    }

    void FixedUpdate()
    {
        SampleInputs();

        // E-Stop latch
        if (!EStop_OK.v)
        {
            if (!eStopLatched && VerboseLogs) Debug.LogWarning("[PnP] E-STOP drop -> latched");
            eStopLatched = true;
            SafeOutputs();
            Enter(STATE.RESET);
        }

        if (Cmd_Reset.Rising)
        {
            eStopLatched = false;
            Enter(STATE.RESET);
        }

        runEnable = (!eStopLatched) && EStop_OK.v;

        // FSM
        switch (State)
        {
            case STATE.RESET: Tick_RESET(); break;
            case STATE.EXEC_PREP: Tick_EXEC_PREP(); break;
            case STATE.EXEC_RUN: Tick_EXEC_RUN(); break;
            case STATE.DWELL: Tick_DWELL(); break;
            case STATE.DONE: Tick_DONE(); break;
            case STATE.FAULT: Tick_FAULT(); break;
        }

        MaintainStartPulse();

        if (VerboseLogs && Time.time >= _nextLogAt)
        {
            _nextLogAt = Time.time + Mathf.Max(0.05f, LogInterval_s);
            LogTick();
        }
    }

    // ===================================================
    // State logic
    // ===================================================
    void Tick_RESET()
    {
        SafeOutputs();
        if (PnP_Busy?.tag) PnP_Busy.Set(false);
        if (PnP_Done?.tag) PnP_Done.Set(false);

        stepIdx = 0;
        stateTimer = 0f;
        settleTimer = 0f;

        if (!runEnable) return;

        // Level-gated start:
        bool canStart = GateStartBySheetSensor ? (Cmd_Start.v && SheetAtPick.v) : Cmd_Start.Rising;
        if (canStart)
        {
            BeginProgram();
        }
    }

    void BeginProgram()
    {
        if (Steps == null || Steps.Length == 0) { Fault("No steps configured"); return; }
        if (PnP_Busy?.tag) PnP_Busy.Set(true);
        Enter(STATE.EXEC_PREP);
    }

    void Tick_EXEC_PREP()
    {
        if (!runEnable) { Fault("Run lost"); return; }
        if (Cmd_Stop.v) return; // boundary-gated stop

        if (stepIdx >= (Steps?.Length ?? 0)) { Enter(STATE.DONE); return; }

        cur = Steps[stepIdx];
        cio = GetLinkIO(cur.Link);
        if (cio == null) { Fault($"Link IO missing for {cur.Link}"); return; }

        // NOTE: Do NOT set Grip here. We only set Pick at DWELL ENTRY.

        // Command move
        TrySetFloatOutput(cio.TargetSpeed_mmps, cur.TargetSpeed_mmps);
        TrySetFloatOutput(cio.DestinationIndex, cur.DestinationIndex);
        PulseStartOnly(cio.StartDrive);

        // reset per-move timers
        stateTimer = 0f;
        settleTimer = 0f;

        if (VerboseLogs)
            Debug.Log($"[PnP] Step {stepIdx + 1}/{Steps.Length}: {cur.Link} -> Dest={cur.DestinationIndex}, v={cur.TargetSpeed_mmps} (Wait={cur.Wait}, Dwell={cur.Dwell_s}, Grip={cur.Grip})");

        Enter(STATE.EXEC_RUN);
    }

    // Helper to enter dwell and perform "PickBeforeDwell" exactly on dwell entry
    void EnterDwell()
    {
        // Assert PICK exactly when we enter DWELL (post-position/settle)
        if (cur.Grip == GripAction.PickBeforeDwell)
        {
            TrySetBoolOutput(Grip_Place, false);
            TrySetBoolOutput(Grip_Pick, true);
            if (VerboseLogs) Debug.Log("[PnP] Grip_Pick=TRUE at DWELL entry.");
        }

        Enter(STATE.DWELL);
    }

    void Tick_EXEC_RUN()
    {
        if (!runEnable) { Fault("Run lost"); return; }

        stateTimer += Time.fixedDeltaTime;

        if (cur.Wait == WaitPolicy.WaitAtPosition)
        {
            if (cio.IsAtPosition.v)
            {
                settleTimer += Time.fixedDeltaTime;
                if (settleTimer >= Mathf.Max(0, SettleAfterAtPos_s))
                {
                    EnterDwell(); // <-- pick at dwell entry
                    return;
                }
            }
        }
        else
        {
            EnterDwell(); // DwellOnly still asserts pick at dwell entry
            return;
        }

        // Watchdog
        if (stateTimer > Mathf.Max(0.05f, cur.WD_Move_s))
            Fault($"Move WD: {cur.Link} Dest={cur.DestinationIndex} atPos={cio.IsAtPosition.v} drv={cio.IsDriving.v}");
    }

    void Tick_DWELL()
    {
        if (!runEnable) { Fault("Run lost"); return; }

        stateTimer += Time.fixedDeltaTime;

        if (stateTimer >= Mathf.Max(0, cur.Dwell_s))
        {
            // Grip action AFTER dwell (place)
            if (cur.Grip == GripAction.PlaceAfterDwell)
            {
                TrySetBoolOutput(Grip_Pick, false);
                TrySetBoolOutput(Grip_Place, true);
                if (VerboseLogs) Debug.Log("[PnP] Grip_Place=TRUE after dwell.");
            }

            stepIdx++;
            Enter(STATE.EXEC_PREP);
        }
    }

    void Tick_DONE()
    {
        if (PnP_Busy?.tag) PnP_Busy.Set(false);

        // One-frame done pulse (if wired)
        if (PnP_Done?.tag)
        {
            if (!PnP_Done.Get()) PnP_Done.Set(true);
            else PnP_Done.Set(false);
        }

        // Back to RESET; RESET will auto-restart if Cmd_Start && SheetAtPick are HIGH.
        Enter(STATE.RESET);
    }

    void Tick_FAULT()
    {
        SafeOutputs();
        // wait for Reset
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

    void Enter(STATE s)
    {
        _prev = State; State = s;
        stateTimer = 0f; settleTimer = 0f;
        if (VerboseLogs)
            Debug.Log($"[PnP] STATE: {_prev} -> {State} @ {Time.time:0.000}s (step={stepIdx}/{(Steps != null ? Steps.Length : 0)})");
    }

    void Fault(string why)
    {
        Debug.LogWarning($"[PnP] FAULT: {why} @ {Time.time:0.000}s (state={State}, step={stepIdx})");
        Enter(STATE.FAULT);
    }

    void SampleInputs()
    {
        Cmd_Start.Sample(); Cmd_Stop.Sample(); Cmd_Reset.Sample(); EStop_OK.Sample();
        SheetAtPick.Sample();

        LinkX.IsAtPosition.Sample(); LinkX.IsDriving.Sample(); LinkX.PositionIndex.Sample();
        LinkY.IsAtPosition.Sample(); LinkY.IsDriving.Sample(); LinkY.PositionIndex.Sample();
        LinkZ.IsAtPosition.Sample(); LinkZ.IsDriving.Sample(); LinkZ.PositionIndex.Sample();
        LinkR.IsAtPosition.Sample(); LinkR.IsDriving.Sample(); LinkR.PositionIndex.Sample();
    }

    void LogTick()
    {
        string curDesc = (Steps != null && stepIdx < Steps.Length)
            ? $"{Steps[stepIdx].Link}@{Steps[stepIdx].DestinationIndex} v={Steps[stepIdx].TargetSpeed_mmps} (Grip={Steps[stepIdx].Grip})"
            : "-";

        Debug.Log(
            $"[PnP {Time.time:0.000}] State={State} Busy={(PnP_Busy?.Get() ?? false)} Stop={Cmd_Stop.v} " +
            $"EStopLatched={eStopLatched} step={stepIdx}/{(Steps != null ? Steps.Length : 0)} cur={curDesc} | " +
            $"X(pos={LinkX.PositionIndex.v:0} at={LinkX.IsAtPosition.v} drv={LinkX.IsDriving.v}) " +
            $"Y(pos={LinkY.PositionIndex.v:0} at={LinkY.IsAtPosition.v} drv={LinkY.IsDriving.v}) " +
            $"Z(pos={LinkZ.PositionIndex.v:0} at={LinkZ.IsAtPosition.v} drv={LinkZ.IsDriving.v}) " +
            $"R(pos={LinkR.PositionIndex.v:0} at={LinkR.IsAtPosition.v} drv={LinkR.IsDriving.v}) " +
            $"SheetAtPick={SheetAtPick.v} " +
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
