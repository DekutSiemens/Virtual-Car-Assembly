using UnityEngine;
using System;
using System.Reflection;   // for optional Override mirroring
using realvirtual;

/// Industrial pick & place controller for 4 links (X/Y/Z/R) that drives
/// Drive_DestinationMotor-style IO (DestinationIndex, TargetSpeed, StartDrive,
/// IsAtPosition, IsDriving, PositionIndex).
///
/// Fixes vs previous version:
/// 1) One Start pulse is enough: RESET → EXEC immediately (no second pulse).
/// 2) Output writes are reliable:
///    - Writes to normal Value (tag.Value)
///    - Optional: also forces tag.Override=true and sets tag.ValueOverride
///      so you can SEE changes in the inspector and satisfy behaviours that
///      use the Override path.
///
/// Extras:
/// - Min start-pulse width
/// - Boundary-gated Stop between steps
/// - Optional vacuum on/off steps
/// - Robust logs with the *actual* values we write
public class PickPlaceSequencer : MonoBehaviour
{
    // ---------------- Lifecycle ----------------
    [Header("Lifecycle (Inputs)")]
    public BoolIn Cmd_Start;       // Rising edge starts a cycle
    public BoolIn Cmd_Stop;        // Boundary-gated hold between steps
    public BoolIn Cmd_Reset;       // Rising clears faults / E-Stop latch
    public BoolIn EStop_OK;        // Level; any drop latches E-Stop

    [Header("Controller Status (Outputs, optional)")]
    public BoolOut PnP_Busy;       // level TRUE during an active cycle
    public BoolOut PnP_Done;       // one-frame pulse at the end of a cycle

    // ---------------- Vacuum (optional) ----------------
    [Header("Gripper (optional)")]
    public BoolOut Vacuum_On;
    public BoolIn Vacuum_OK;

    // ---------------- Link IO Bundles ------------------
    public enum Link { X = 0, Y = 1, Z = 2, R = 3 }

    [Serializable]
    public class LinkIO
    {
        [Header("Outputs")]
        public FloatOut DestinationIndex;   // integer index for destination motor
        public FloatOut TargetSpeed_mmps;   // linear mm/s or deg/s for rotary
        public BoolOut StartDrive;         // pulse TRUE for >= MinStartPulse_s

        [Header("Inputs")]
        public BoolIn IsAtPosition;
        public BoolIn IsDriving;
        public FloatIn PositionIndex;       // for logs / diagnostics
    }

    [Header("Links (assign ALL 4)")]
    public LinkIO LinkX, LinkY, LinkZ, LinkR;

    // ---------------- Program --------------------------
    public enum WaitPolicy { WaitAtPosition, DwellOnly }
    public enum VacAction { None, OnBeforeMove, OffAfterDwell }

    [Serializable]
    public class Step
    {
        [Header("Move")]
        public Link Link;
        [Tooltip("Destination index expected by Drive_DestinationMotor")]
        public float DestinationIndex = 0;
        [Tooltip("Target speed (mm/s; use deg/s for rotary R)")]
        public float TargetSpeed_mmps = 400f;

        [Header("Completion & Timing")]
        public WaitPolicy Wait = WaitPolicy.WaitAtPosition;
        [Tooltip("Post-move dwell (sec)")]
        public float Dwell_s = 0f;
        [Tooltip("Move watchdog (sec)")]
        public float WD_Move_s = 3.0f;

        [Header("Vacuum (optional)")]
        public VacAction Vacuum = VacAction.None;
        [Tooltip("If Vacuum action used, confirm window (sec) for Vacuum_OK")]
        public float WD_Vacuum_s = 0.5f;
    }

    [Header("Program (runs top → bottom)")]
    public Step[] Steps;

    // ---------------- Behaviour Options ----------------
    [Header("Controller Options")]
    [Tooltip("One Start pulse also starts the program (no second pulse in IDLE).")]
    public bool StartImmediately = true;

    [Tooltip("Minimum StartDrive pulse width (sec).")]
    public float MinStartPulse_s = 0.10f;

    [Tooltip("Extra settle after IsAtPosition before entering dwell (sec).")]
    public float SettleAfterAtPos_s = 0.05f;

    [Tooltip("Also write to Override/ValueOverride so inspector updates and behaviours that read Override see your commands.")]
    public bool MirrorWritesToOverride = true;

    [Tooltip("If true, controller ignores IO writes (useful for dry-run debugging).")]
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

    // cached for the current step
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

        // Hard E-Stop latch
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

        // Fix #1: a single Start pulse is enough to begin immediately
        if (Cmd_Start.Rising)
        {
            if (Steps == null || Steps.Length == 0) { Fault("No steps configured"); return; }
            if (PnP_Busy?.tag) PnP_Busy.Set(true);
            Enter(STATE.EXEC_PREP);
        }
    }

    void Tick_EXEC_PREP()
    {
        if (!runEnable) { Fault("Run lost"); return; }

        // Boundary-gated Stop: don't launch a new move while Stop=1
        if (Cmd_Stop.v) return;

        if (stepIdx >= (Steps?.Length ?? 0))
        {
            Enter(STATE.DONE);
            return;
        }

        cur = Steps[stepIdx];
        cio = GetLinkIO(cur.Link);
        if (cio == null) { Fault($"Link IO missing for {cur.Link}"); return; }

        // Optional vacuum on before the move
        if (cur.Vacuum == VacAction.OnBeforeMove)
        {
            TrySetBoolOutput(Vacuum_On, true);
            if (Vacuum_OK.tag && cur.WD_Vacuum_s > 0f)
            {
                stateTimer += Time.fixedDeltaTime;
                if (!Vacuum_OK.v && stateTimer < cur.WD_Vacuum_s) return;
                if (!Vacuum_OK.v && stateTimer >= cur.WD_Vacuum_s) { Fault("Vacuum OK timeout before move"); return; }
                stateTimer = 0f;
            }
        }

        // Command the selected link: speed → destination → start pulse
        TrySetFloatOutput(cio.TargetSpeed_mmps, cur.TargetSpeed_mmps);
        TrySetFloatOutput(cio.DestinationIndex, cur.DestinationIndex);
        PulseStartOnly(cio.StartDrive);

        // reset per-move timers
        stateTimer = 0f;
        settleTimer = 0f;

        if (VerboseLogs)
        {
            Debug.Log($"[PnP] Step {stepIdx + 1}/{Steps.Length}: {cur.Link} -> Dest={cur.DestinationIndex}, v={cur.TargetSpeed_mmps} (Wait={cur.Wait}, Dwell={cur.Dwell_s})");
        }

        Enter(STATE.EXEC_RUN);
    }

    void Tick_EXEC_RUN()
    {
        if (!runEnable) { Fault("Run lost"); return; }

        stateTimer += Time.fixedDeltaTime;

        bool atPos = cio.IsAtPosition.v;

        if (cur.Wait == WaitPolicy.WaitAtPosition)
        {
            if (atPos)
            {
                settleTimer += Time.fixedDeltaTime;
                if (settleTimer >= Mathf.Max(0, SettleAfterAtPos_s))
                {
                    Enter(STATE.DWELL);
                    return;
                }
            }
        }
        else
        {
            // DwellOnly policy: go straight to dwell
            Enter(STATE.DWELL);
            return;
        }

        // Watchdog
        if (stateTimer > Mathf.Max(0.05f, cur.WD_Move_s))
        {
            Fault($"Move WD: {cur.Link} Dest={cur.DestinationIndex}  atPos={cio.IsAtPosition.v} drv={cio.IsDriving.v}");
        }
    }

    void Tick_DWELL()
    {
        if (!runEnable) { Fault("Run lost"); return; }

        stateTimer += Time.fixedDeltaTime;

        if (stateTimer >= Mathf.Max(0, cur.Dwell_s))
        {
            // Optional: vacuum off AFTER dwell, with confirm
            if (cur.Vacuum == VacAction.OffAfterDwell)
            {
                TrySetBoolOutput(Vacuum_On, false);
                if (Vacuum_OK.tag && cur.WD_Vacuum_s > 0f)
                {
                    float rel = stateTimer - cur.Dwell_s;
                    if (Vacuum_OK.v && rel < cur.WD_Vacuum_s) return;
                    if (Vacuum_OK.v && rel >= cur.WD_Vacuum_s) { Fault("Vacuum release timeout"); return; }
                }
            }

            // next step
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

        // Go back to RESET and wait for the next Start
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
        // force all LOW, then raise the selected one for MinStartPulse_s
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
        TrySetBoolOutput(Vacuum_On, false);
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

        Vacuum_OK.Sample();

        LinkX.IsAtPosition.Sample(); LinkX.IsDriving.Sample(); LinkX.PositionIndex.Sample();
        LinkY.IsAtPosition.Sample(); LinkY.IsDriving.Sample(); LinkY.PositionIndex.Sample();
        LinkZ.IsAtPosition.Sample(); LinkZ.IsDriving.Sample(); LinkZ.PositionIndex.Sample();
        LinkR.IsAtPosition.Sample(); LinkR.IsDriving.Sample(); LinkR.PositionIndex.Sample();
    }

    void LogTick()
    {
        string curDesc = (Steps != null && stepIdx < Steps.Length)
            ? $"{Steps[stepIdx].Link}@{Steps[stepIdx].DestinationIndex} v={Steps[stepIdx].TargetSpeed_mmps}"
            : "-";

        Debug.Log(
            $"[PnP {Time.time:0.000}] State={State} Busy={(PnP_Busy?.Get() ?? false)} Stop={Cmd_Stop.v} " +
            $"EStopLatched={eStopLatched} step={stepIdx}/{(Steps != null ? Steps.Length : 0)} cur={curDesc} | " +
            $"X(p={LinkX.PositionIndex.v:0} at={LinkX.IsAtPosition.v} drv={LinkX.IsDriving.v}) " +
            $"Y(p={LinkY.PositionIndex.v:0} at={LinkY.IsAtPosition.v} drv={LinkY.IsDriving.v}) " +
            $"Z(p={LinkZ.PositionIndex.v:0} at={LinkZ.IsAtPosition.v} drv={LinkZ.IsDriving.v}) " +
            $"R(p={LinkR.PositionIndex.v:0} at={LinkR.IsAtPosition.v} drv={LinkR.IsDriving.v}) " +
            $"VacOn={(Vacuum_On?.Get() ?? false)} VacOK={Vacuum_OK.v}"
        );
    }

    // ===================================================
    // Output writing (with optional Override mirroring)
    // ===================================================

    void TrySetFloatOutput(FloatOut outTag, float value)
    {
        if (outTag?.tag == null) return;
        if (DryRun) return;

        // normal path
        outTag.Set(value);

        // mirror to Override + ValueOverride if present and enabled
        if (MirrorWritesToOverride)
        {
            TrySetOverride(outTag.tag, value);
        }
    }

    void TrySetBoolOutput(BoolOut outTag, bool value)
    {
        if (outTag?.tag == null) return;
        if (DryRun) return;

        outTag.Set(value);

        if (MirrorWritesToOverride)
        {
            TrySetOverride(outTag.tag, value ? 1f : 0f);
        }
    }

    // reflection-based, so it works with the free package fields without us
    // taking a dependency on their exact member names
    static void TrySetOverride(object plcOutput, float numeric)
    {
        if (plcOutput == null) return;

        var t = plcOutput.GetType();

        // bool Override
        var fOverride = t.GetField("Override", BindingFlags.Public | BindingFlags.Instance);
        var pOverride = t.GetProperty("Override", BindingFlags.Public | BindingFlags.Instance);
        if (fOverride != null) fOverride.SetValue(plcOutput, true);
        if (pOverride != null && pOverride.CanWrite) pOverride.SetValue(plcOutput, true);

        // float ValueOverride
        var fVO = t.GetField("ValueOverride", BindingFlags.Public | BindingFlags.Instance);
        var pVO = t.GetProperty("ValueOverride", BindingFlags.Public | BindingFlags.Instance);
        if (fVO != null) fVO.SetValue(plcOutput, numeric);
        if (pVO != null && pVO.CanWrite) pVO.SetValue(plcOutput, numeric);
    }
}

