using UnityEngine;
using realvirtual;
using VME.IO;

/// InfeedConveyor — EXIT-level stop + continuous-LOW rearm + distance-based sourcing via signal + post-cut push assist
[DefaultExecutionOrder(-10)]
public class InfeedConveyor : MonoBehaviour
{
    [Header("Shared Control Signals")]
    public BoolIn Cmd_Start, Cmd_Stop, Cmd_Reset, EStop_OK;

    [Header("Mode Bit (true=AUTO, false=MANUAL)")]
    public BoolIn Mode_Auto;

    [Header("Inputs")]
    public BoolIn PE_Entry;     // visual only
    public BoolIn PE_Exit;      // primary index (level used for stop)

    [Header("Outputs to Drive")]
    public BoolOut Conv_Infeed_Fwd;
    public FloatOut Conv_Infeed_TargetSpeed_mmps;

    [Header("Source Control (wire to Source inputs)")]
    [Tooltip("Wire this to Source.SourceGenerateOnDistance (BoolIn on Source).")]
    public BoolOut SourceGenerateOnDistance_Out;
    [Tooltip("Optional single seed (unused in AUTO). Wire to Source.SourceGenerate.")]
    public BoolOut SourceGenerateOnce_Out;

    [Header("Motion Settings")]
    public float SpeedMMps = 300f;

    [Header("Watchdogs (seconds)")]
    public float WD_ToExit_s = 5.0f;

    [Header("Re-arm (Exit LOW must be continuous)")]
    [Tooltip("EXIT must be LOW continuously this long before next feed is allowed.")]
    public float ReArmDelaySec = 0.30f;

    [Header("Push Assist (post-cut nudge)")]
    [Tooltip("Enable a short nudge after cut complete to hand off to outfeed.")]
    public bool EnablePushAssist = true;
    [Tooltip("Cut complete pulse from Cutter (>=40–60 ms).")]
    public BoolIn Cut_Complete;            // optional; if null, push will never arm
    [Tooltip("Blade up / path clear (safety).")]
    public BoolIn Cutter_Safe;             // optional safety gate
    [Tooltip("Pick station presence (require empty before push).")]
    public BoolIn PE_Pick;                 // optional; used if PushRequirePickEmpty = true
    [Tooltip("How fast to nudge during push (mm/s).")]
    public float PushAssistSpeedMMps = 150f;
    [Tooltip("How long to nudge (s). Keep tiny (0.10–0.25s).")]
    public float PushAssistDuration_s = 0.15f;
    [Tooltip("How long after Cut_Complete the push may occur (s).")]
    public float PushAssistWindow_s = 1.00f;
    [Tooltip("Require pick to be empty before push.")]
    public bool PushRequirePickEmpty = true;
    [Tooltip("Require Cutter_Safe before push.")]
    public bool PushRequireCutterSafe = true;

    [Header("Diagnostics")]
    public bool VerboseLogs = false;

    private enum S { RESET, IDLE, FEED_TO_EXIT, PUSH_ASSIST, HOLD, FAULT }
    [SerializeField] private S _s = S.RESET, _prev;

    private bool _eStopLatched;
    private bool _armed;               // allowed to accept next feed
    private float _exitLowAccum;        // continuous-low timer for EXIT
    private float _enteredAt;
    private bool _cmdFwd;
    private int _minRunGuardFrames;   // ignore EXIT first physics tick in FEED

    // Push assist internals
    private bool _pushArmed;
    private float _pushArmUntil;
    private float _pushEndAt;

    float Now => Time.time;

    void Start() { SetDistanceGen(false); Enter(S.RESET); }
    void OnEnable() { SetDistanceGen(false); }
    void OnDisable() { SetDistanceGen(false); }

    void FixedUpdate()
    {
        // Sample inputs
        Cmd_Start.Sample(); Cmd_Stop.Sample(); Cmd_Reset.Sample(); EStop_OK.Sample();
        Mode_Auto.Sample();
        PE_Entry.Sample(); PE_Exit.Sample();
        if (Cut_Complete != null) Cut_Complete.Sample();
        if (Cutter_Safe != null) Cutter_Safe.Sample();
        if (PE_Pick != null) PE_Pick.Sample();

        // E-Stop latch & reset
        if (!EStop_OK.v) { _eStopLatched = true; Enter(S.RESET); }
        if (Cmd_Reset.Rising) { _eStopLatched = false; Enter(S.RESET); }
        bool runEnable = !_eStopLatched && EStop_OK.v;

        // === Arm push on cut-complete (bounded window) ===
        if (EnablePushAssist && Cut_Complete != null && Cut_Complete.Rising)
        {
            _pushArmed = true;
            _pushArmUntil = Now + Mathf.Max(0.01f, PushAssistWindow_s);
        }
        // Auto-expire the arm window
        if (_pushArmed && Now > _pushArmUntil) _pushArmed = false;

        // === EXIT continuous-LOW blanking (rearm discipline) ===
        if (!PE_Exit.v) { _exitLowAccum += Time.fixedDeltaTime; }
        else { _exitLowAccum = 0f; _armed = false; }

        if (_exitLowAccum >= Mathf.Max(0f, ReArmDelaySec))
            _armed = true;

        // STOP boundary: drop sourcing first, then stop belt (also interrupts PUSH)
        if (Cmd_Stop.v && _s != S.FAULT)
        {
            if (_s == S.IDLE || _s == S.FEED_TO_EXIT || _s == S.PUSH_ASSIST || _s == S.HOLD)
            {
                SetDistanceGen(false);
                CommandStop();
                Enter(S.HOLD);
            }
        }

        switch (_s)
        {
            case S.RESET:
                {
                    SetDistanceGen(false);
                    CommandStop();
                    _exitLowAccum = 0f;
                    _armed = false;
                    _pushArmed = false;
                    if (runEnable) Enter(S.IDLE);
                    break;
                }

            case S.IDLE:
                {
                    CommandStop();

                    // ---- Optional post-cut push assist (no sourcing, no feed) ----
                    if (EnablePushAssist && _pushArmed && Now <= _pushArmUntil && runEnable && !Cmd_Stop.v)
                    {
                        bool pickOK = !PushRequirePickEmpty || (PE_Pick != null && !PE_Pick.v);
                        bool safeOK = !PushRequireCutterSafe || (Cutter_Safe != null && Cutter_Safe.v);
                        if (pickOK && safeOK)
                        {
                            SetDistanceGen(false);     // never spawn during push
                            Enter(S.PUSH_ASSIST);
                            break;
                        }
                    }

                    // MANUAL: one feed per Start press, only when EXIT low and armed
                    if (!Mode_Auto.v)
                    {
                        if (Cmd_Start.Rising && !PE_Exit.v && _armed)
                            Enter(S.FEED_TO_EXIT);
                        break;
                    }

                    // AUTO: Start level + armed + Exit low
                    if (Mode_Auto.v && Cmd_Start.v && _armed && !PE_Exit.v)
                        Enter(S.FEED_TO_EXIT);

                    break;
                }

            case S.FEED_TO_EXIT:
                {
                    if (!runEnable) { Fault("Run lost during FEED_TO_EXIT"); break; }

                    // consume armed at start; enable distance-based sourcing (first time in)
                    if (_minRunGuardFrames == 1) { _armed = false; SetDistanceGen(true); }

                    CommandRun(SpeedMMps);

                    // PRIMARY STOP: EXIT LEVEL HIGH (robust against missed Rising)
                    if (_minRunGuardFrames > 0)
                    {
                        _minRunGuardFrames--; // wait one physics tick before honoring EXIT
                    }
                    else if (PE_Exit.v)
                    {
                        // terminate sourcing first, then stop belt
                        SetDistanceGen(false);
                        CommandStop();
                        Enter(S.IDLE);
                        if (Cmd_Stop.v) Enter(S.HOLD);
                        break;
                    }

                    // Watchdog
                    if (Now - _enteredAt > WD_ToExit_s)
                    {
                        SetDistanceGen(false);
                        CommandStop();
                        Fault("ToExit watchdog");
                    }
                    break;
                }

            case S.PUSH_ASSIST:
                {
                    // short, controlled nudge; never enable sourcing here
                    CommandRun(Mathf.Max(0f, PushAssistSpeedMMps));
                    if (Now >= _pushEndAt)
                    {
                        CommandStop();
                        _pushArmed = false;     // consume the arm
                        Enter(S.IDLE);
                    }
                    break;
                }

            case S.HOLD:
                {
                    CommandStop();
                    if (!Cmd_Stop.v)
                    {
                        if (Mode_Auto.v) { if (Cmd_Start.v) Enter(S.IDLE); }
                        else { if (Cmd_Start.Rising) Enter(S.IDLE); }
                    }
                    break;
                }

            case S.FAULT:
                {
                    SetDistanceGen(false);
                    CommandStop();
                    break;
                }
        }
    }

    // ===== Drive Command Helpers =====
    void CommandRun(float speed)
    {
        Conv_Infeed_TargetSpeed_mmps.Set(speed);
        Conv_Infeed_Fwd.Set(true);
        _cmdFwd = true;
    }

    void CommandStop()
    {
        Conv_Infeed_TargetSpeed_mmps.Set(0f);
        Conv_Infeed_Fwd.Set(false);
        _cmdFwd = false;
    }

    // ===== Source Distance Gen Helper (signal-driven) =====
    void SetDistanceGen(bool on)
    {
        if (SourceGenerateOnDistance_Out != null)
            SourceGenerateOnDistance_Out.Set(on);
    }

    // ===== State Helpers =====
    void Enter(S s)
    {
        _prev = _s;
        _s = s;
        _enteredAt = Now;

        if (s == S.FEED_TO_EXIT)
            _minRunGuardFrames = 1;   // ignore EXIT for the first physics tick to avoid immediate stop

        if (s == S.PUSH_ASSIST)
            _pushEndAt = Now + Mathf.Max(0.01f, PushAssistDuration_s);

        if (VerboseLogs) Debug.Log($"[Infeed] {_prev} → {_s} @ {Now:0.000}s (Exit={PE_Exit.v})");
    }

    void Fault(string why)
    {
        if (VerboseLogs) Debug.LogWarning($"[Infeed] FAULT: {why}");
        Enter(S.FAULT);
    }
}
