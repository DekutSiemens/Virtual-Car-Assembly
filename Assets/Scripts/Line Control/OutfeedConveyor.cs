using UnityEngine;
using VME.IO;

[DefaultExecutionOrder(-10)]
public class OutfeedConveyor : MonoBehaviour
{
    [Header("Shared Control Signals")]
    public BoolIn Cmd_Start, Cmd_Stop, Cmd_Reset, EStop_OK;

    [Header("Mode Bit (true=AUTO, false=MANUAL)")]
    public BoolIn Mode_Auto;

    [Header("Sensors & Cutter Handshake (Inputs)")]
    public BoolIn PE_Exit;          // part present at cutter throat
    public BoolIn PE_Pick;          // part present at pick station
    public BoolIn Cut_Complete;     // 1-shot pulse from Cutter (>= 40–60 ms)
    [Tooltip("BladeUp / CutterSafe = 1 when outfeed path is mechanically clear.")]
    public BoolIn Cutter_Safe;      // optional (can be bypassed via RequireCutterSafe)

    [Header("Drive Outputs")]
    public BoolOut Conv_Outfeed_Fwd;
    public FloatOut Conv_Outfeed_TargetSpeed_mmps;

    [Header("Settings")]
    public float SpeedMMps = 300f;
    [Tooltip("Max seconds allowed to reach pick from throat before faulting.")]
    public float WD_ToPick_s = 8.0f;
    [Tooltip("Delay after pick clears (sensor LOW) before staging the next part.")]
    public float PickClearDelay_s = 0.20f;

    [Header("Safety")]
    [Tooltip("If false, treats the cutter path as safe even if Cutter_Safe is not wired yet.")]
    public bool RequireCutterSafe = true;

    [Header("Diagnostics")]
    public bool VerboseLogs = false;

    private enum S { RESET, IDLE, RUN, PARKED, CLEAR_DELAY, HOLD, FAULT }
    [SerializeField] private S _s = S.RESET, _prev;

    private bool _eStopLatched;
    private float _stateEnterAt, _watchdogDeadline, _delayStartT;
    private int _cutPermitCount = 0; // token queue (one per Cut_Complete)

    private float Now => Time.time;

    void Start() => Enter(S.RESET);

    void FixedUpdate()
    {
        // Sample inputs (null-safe)
        Cmd_Start?.Sample(); Cmd_Stop?.Sample(); Cmd_Reset?.Sample(); EStop_OK?.Sample();
        Mode_Auto?.Sample();
        PE_Exit?.Sample(); PE_Pick?.Sample(); Cut_Complete?.Sample(); Cutter_Safe?.Sample();

        // Latch/queue a token on every fresh cut
        if (Cut_Complete != null && Cut_Complete.Rising) _cutPermitCount++;

        // E-Stop handling
        if (EStop_OK == null || !EStop_OK.v) { _eStopLatched = true; Enter(S.RESET); }
        if (Cmd_Reset != null && Cmd_Reset.Rising) { _eStopLatched = false; Enter(S.RESET); }
        bool runEnable = !_eStopLatched && (EStop_OK == null || EStop_OK.v);

        // Global Stop → HOLD (outputs off)
        if (Cmd_Stop != null && Cmd_Stop.v && _s != S.FAULT)
        {
            if (_s != S.HOLD) { CommandStop(); Enter(S.HOLD); }
        }

        switch (_s)
        {
            case S.RESET:
                {
                    CommandStop();
                    _cutPermitCount = 0; // require fresh tokens after reset
                    if (runEnable) Enter(S.IDLE);
                    break;
                }

            case S.IDLE:
                {
                    CommandStop();

                    // AUTO start: require token + throat has part + pick empty + cutter safe + Start level
                    if (IsAuto() && CanStartLevel()) BeginRun();

                    // MANUAL start: same, but on Start rising edge
                    if (!IsAuto() && StartPressedOnce() && CanStartLevel()) BeginRun();

                    // When pick clears, arm the delay (but do NOT move without full CanStartLevel)
                    if (PE_Pick != null && PE_Pick.Falling)
                    {
                        _delayStartT = Now;
                        Enter(S.CLEAR_DELAY);
                    }
                    break;
                }

            case S.RUN:
                {
                    if (!runEnable) { Fault("Run lost"); break; }

                    // Safety: if cutter becomes unsafe during transfer, stop & fault
                    if (RequireCutterSafe && !IsSafe())
                    {
                        CommandStop();
                        Fault("CutterNotSafeDuringRun");
                        break;
                    }

                    CommandRun(SpeedMMps);

                    // Primary stop: as soon as the part reaches pick
                    if (PE_Pick != null && PE_Pick.v)
                    {
                        CommandStop();
                        Enter(S.PARKED);
                        break;
                    }

                    // Timeout safety
                    if (Now > _watchdogDeadline)
                    {
                        CommandStop();
                        Fault("ToPick timeout");
                    }
                    break;
                }

            case S.PARKED:
                {
                    CommandStop();
                    if (PE_Pick != null && PE_Pick.Falling)
                    {
                        _delayStartT = Now;
                        Enter(S.CLEAR_DELAY);
                    }
                    break;
                }

            case S.CLEAR_DELAY:
                {
                    CommandStop();

                    if (Now - _delayStartT >= PickClearDelay_s)
                    {
                        if (IsAuto() && CanStartLevel()) BeginRun();
                        else if (!IsAuto() && StartPressedOnce() && CanStartLevel()) BeginRun();
                        else Enter(S.IDLE);
                    }
                    break;
                }

            case S.HOLD:
                {
                    CommandStop();
                    if (Cmd_Stop == null || !Cmd_Stop.v) Enter(S.IDLE);
                    break;
                }

            case S.FAULT:
                {
                    CommandStop();
                    break;
                }
        }
    }

    // ===== Helpers =====
    bool IsAuto() => Mode_Auto == null || Mode_Auto.v;
    bool StartLevel() => Cmd_Start == null || Cmd_Start.v;
    bool StartPressedOnce() => Cmd_Start != null && Cmd_Start.Rising;

    bool ThroatHasPart() => PE_Exit != null && PE_Exit.v;
    bool PickEmpty() => PE_Pick != null && !PE_Pick.v;

    bool IsSafe()
    {
        if (!RequireCutterSafe) return true;
        return Cutter_Safe != null && Cutter_Safe.v;
    }

    bool CanStartLevel()
    {
        return StartLevel()
            && _cutPermitCount > 0
            && ThroatHasPart()
            && PickEmpty()
            && IsSafe();
    }

    void BeginRun()
    {
        _cutPermitCount = Mathf.Max(0, _cutPermitCount - 1); // consume exactly one token
        _watchdogDeadline = Now + Mathf.Max(0.5f, WD_ToPick_s);
        Enter(S.RUN);
    }

    void CommandRun(float speed)
    {
        Conv_Outfeed_TargetSpeed_mmps?.Set(speed);
        Conv_Outfeed_Fwd?.Set(true);
    }

    void CommandStop()
    {
        Conv_Outfeed_TargetSpeed_mmps?.Set(0f);
        Conv_Outfeed_Fwd?.Set(false);
    }

    void Enter(S s)
    {
        _prev = _s;
        _s = s;
        _stateEnterAt = Now;
        if (VerboseLogs)
        {
            var exit = PE_Exit != null && PE_Exit.v;
            var pick = PE_Pick != null && PE_Pick.v;
            var safe = !RequireCutterSafe || (Cutter_Safe != null && Cutter_Safe.v);
            Debug.Log($"[Outfeed] {_prev} → {_s} @ {Now:0.000}s (Exit={exit}, Pick={pick}, Safe={safe}, Tokens={_cutPermitCount})");
        }
    }

    void Fault(string why)
    {
        if (VerboseLogs) Debug.LogWarning($"[Outfeed] FAULT: {why}");
        Enter(S.FAULT);
    }
}
