using UnityEngine;
using VME.IO;

[DefaultExecutionOrder(-10)]
public class OutfeedConveyor : MonoBehaviour
{
    [Header("Shared Control Signals")]
    public BoolIn Cmd_Start;     // shared across line
    public BoolIn Cmd_Stop;
    public BoolIn Cmd_Reset;
    public BoolIn EStop_OK;

    [Header("Mode Bit (true=AUTO, false=MANUAL)")]
    public BoolIn Mode_Auto;

    [Header("Sensors & Cutter Handshake (Inputs)")]
    public BoolIn PE_Exit;         // part present at cutter throat
    public BoolIn PE_Pick;         // part present at pick station
    public BoolIn Cut_Complete;    // 1-shot pulse from Cutter when a cut finished

    [Header("Drive Outputs")]
    public BoolOut Conv_Outfeed_Fwd;
    public FloatOut Conv_Outfeed_TargetSpeed_mmps;

    [Header("Settings")]
    public float SpeedMMps = 300f;
    [Tooltip("Max seconds allowed to reach pick from throat before faulting.")]
    public float WD_ToPick_s = 8.0f;
    [Tooltip("Delay after pick clears (sensor LOW) before staging the next part.")]
    public float PickClearDelay_s = 0.20f;

    [Header("Diagnostics")]
    public bool VerboseLogs = false;

    // ===== FSM =====
    private enum S { RESET, IDLE, RUN, PARKED, CLEAR_DELAY, HOLD, FAULT }
    [SerializeField] private S _s = S.RESET;
    private S _prev;

    // ===== Internals =====
    private bool _eStopLatched;
    private float _stateEnterAt;
    private float _watchdogDeadline;
    private float _delayStartT;

    // Convenience
    private float Now => Time.time;

    void Start() => Enter(S.RESET);

    void FixedUpdate()
    {
        // Sample inputs
        Cmd_Start.Sample(); Cmd_Stop.Sample(); Cmd_Reset.Sample(); EStop_OK.Sample();
        Mode_Auto.Sample();
        PE_Exit.Sample(); PE_Pick.Sample(); Cut_Complete.Sample();

        // E-Stop handling
        if (!EStop_OK.v) { _eStopLatched = true; Enter(S.RESET); }
        if (Cmd_Reset.Rising) { _eStopLatched = false; Enter(S.RESET); }

        bool runEnable = !_eStopLatched && EStop_OK.v;

        // Global Stop → HOLD (outputs off)
        if (Cmd_Stop.v && _s != S.FAULT)
        {
            if (_s != S.HOLD) { CommandStop(); Enter(S.HOLD); }
        }

        switch (_s)
        {
            case S.RESET:
                {
                    CommandStop();
                    if (runEnable) Enter(S.IDLE);
                    break;
                }

            case S.IDLE:
                {
                    CommandStop();

                    // AUTO start: fresh cut pulse + throat has part + pick is empty + Start level
                    if (Mode_Auto.v && Cmd_Start.v)
                    {
                        if (Cut_Complete.Rising && PE_Exit.v && !PE_Pick.v)
                            BeginRun();
                    }

                    // MANUAL start: per-press, when conditions are good
                    if (!Mode_Auto.v && Cmd_Start.Rising)
                    {
                        if (PE_Exit.v && !PE_Pick.v)
                            BeginRun();
                    }

                    // Also allow staging right after pick clears (handled by CLEAR_DELAY state),
                    // but if it cleared while we were IDLE (edge case), we can start directly:
                    if (Mode_Auto.v && Cmd_Start.v && PE_Pick.Falling && PE_Exit.v)
                    {
                        _delayStartT = Now; Enter(S.CLEAR_DELAY);
                    }
                    break;
                }

            case S.RUN:
                {
                    if (!runEnable) { Fault("Run lost"); break; }

                    CommandRun(SpeedMMps);

                    // Primary stop: as soon as the part reaches pick
                    if (PE_Pick.v)
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

                    // When robot picks the part (sensor LOW), start delay then stage next if throat has part
                    if (PE_Pick.Falling)
                    {
                        _delayStartT = Now;
                        Enter(S.CLEAR_DELAY);
                    }
                    break;
                }

            case S.CLEAR_DELAY:
                {
                    CommandStop();

                    // Wait the configured delay after pick cleared
                    if (Now - _delayStartT >= PickClearDelay_s)
                    {
                        // Only stage if throat really has a part and Start permission is present
                        if (PE_Exit.v)
                        {
                            if (Mode_Auto.v && Cmd_Start.v) BeginRun();
                            else if (!Mode_Auto.v && Cmd_Start.Rising) BeginRun();
                            else Enter(S.IDLE); // no permission yet
                        }
                        else
                        {
                            Enter(S.IDLE); // nothing to bring
                        }
                    }
                    break;
                }

            case S.HOLD:
                {
                    CommandStop();
                    // Release HOLD → back to IDLE; normal conditions will re-arm start
                    if (!Cmd_Stop.v) Enter(S.IDLE);
                    break;
                }

            case S.FAULT:
                {
                    CommandStop();
                    // Exit only via Reset (handled at top)
                    break;
                }
        }
    }

    // ===== Helpers =====
    void BeginRun()
    {
        _watchdogDeadline = Now + Mathf.Max(0.5f, WD_ToPick_s);
        Enter(S.RUN);
    }

    void CommandRun(float speed)
    {
        Conv_Outfeed_TargetSpeed_mmps.Set(speed);
        Conv_Outfeed_Fwd.Set(true);
    }

    void CommandStop()
    {
        Conv_Outfeed_TargetSpeed_mmps.Set(0f);
        Conv_Outfeed_Fwd.Set(false);
    }

    void Enter(S s)
    {
        _prev = _s;
        _s = s;
        _stateEnterAt = Now;
        if (VerboseLogs) Debug.Log($"[Outfeed] {_prev} → {_s} @ {Now:0.000}s (Exit={PE_Exit.v}, Pick={PE_Pick.v})");
    }

    void Fault(string why)
    {
        if (VerboseLogs) Debug.LogWarning($"[Outfeed] FAULT: {why}");
        Enter(S.FAULT);
    }
}
