using UnityEngine;
using realvirtual;
using VME.IO;

/// InfeedConveyor — EXIT-sensor–driven indexing + gated timed spawning
/// AUTO: While Cmd_Start=1, feed until PE_Exit=HIGH (via Rising edge), stop; re-arm on PE_Exit=LOW.
/// MANUAL: Each Cmd_Start press feeds once to PE_Exit Rising.
/// Entry sensor is only for initial approach/settle; not used to stop or index length.
[DefaultExecutionOrder(-10)] // run early to stabilize outputs before other late systems
public class InfeedConveyor : MonoBehaviour
{
    [Header("Shared Control Signals")]
    public BoolIn Cmd_Start;   // shared global signals
    public BoolIn Cmd_Stop;
    public BoolIn Cmd_Reset;
    public BoolIn EStop_OK;

    [Header("Mode Bit (true=AUTO, false=MANUAL)")]
    public BoolIn Mode_Auto;

    [Header("Inputs")]
    public BoolIn PE_Entry;  // optional: for initial approach only
    public BoolIn PE_Exit;   // primary index sensor (edges drive logic)

    [Header("Outputs to Drive")]
    public BoolOut Conv_Infeed_Fwd;
    public FloatOut Conv_Infeed_TargetSpeed_mmps;

    [Header("Motion Settings")]
    [Tooltip("Conveyor command speed in mm/s.")]
    public float SpeedMMps = 300f;
    [Tooltip("Delay after reaching entry sensor before feeding to exit (s).")]
    public float StepDelaySec = 0.20f;

    [Header("Watchdogs (seconds)")]
    [Tooltip("Max time allowed to find the entry sensor in APPROACH.")]
    public float WD_ToEntry_s = 5.0f;
    [Tooltip("Max time allowed to reach EXIT=HIGH during FEED_TO_EXIT.")]
    public float WD_ToExit_s = 5.0f;

    [Header("Timed Spawning (while belt commanded forward)")]
    [Tooltip("Source to spawn from. Ensure its AutomaticGeneration=false and Interval=0.")]
    public Source SpawnSource;
    [Min(0.05f)] public float SpawnPeriod_s = 1.50f;
    [Tooltip("Optional delay after forward command before first spawn (e.g., belt ramp).")]
    public float SpawnStartDelay_s = 0.00f;
    [Tooltip("If true, also require EStop_OK for spawns (recommended).")]
    public bool SpawnRequireEStopOK = true;

    [Header("Diagnostics")]
    public bool VerboseLogs = false;

    // ===== FSM =====
    private enum S { RESET, IDLE, APPROACH, WAIT_AT_ENTRY, FEED_TO_EXIT, HOLD, FAULT }
    [SerializeField] private S _s = S.RESET;
    private S _prev;

    // ===== Internals =====
    private bool _eStopLatched;
    private bool _armed;       // true => allowed to accept next feed (requires EXIT Falling first)
    private float _enteredAt;

    // Commanded forward latch (what THIS controller is asking the drive to do)
    private bool _cmdFwd;

    // Timed spawning internals
    private bool _spawnTicking;
    private float _spawnDelayT;

    float Now => Time.time;

    void Start()
    {
        if (SpawnSource != null) SpawnSource.CancelInvoke(); // ensure single cadence
        Enter(S.RESET);
    }

    void OnEnable()
    {
        if (SpawnSource != null) SpawnSource.CancelInvoke();
        StopSpawning();
    }

    void OnDisable() => StopSpawning();

    void FixedUpdate()
    {
        // Sample inputs
        Cmd_Start.Sample(); Cmd_Stop.Sample(); Cmd_Reset.Sample(); EStop_OK.Sample();
        Mode_Auto.Sample();
        PE_Entry.Sample(); PE_Exit.Sample();

        // Hard E-Stop latch & reset
        if (!EStop_OK.v) { _eStopLatched = true; Enter(S.RESET); }
        if (Cmd_Reset.Rising) { _eStopLatched = false; Enter(S.RESET); }

        bool runEnable = !_eStopLatched && EStop_OK.v;

        // Global re-arming: whenever EXIT falls (piece cleared), allow next feed
        if (PE_Exit.Falling) _armed = true;

        // STOP boundary behavior
        if (Cmd_Stop.v && _s != S.FAULT)
        {
            if (_s == S.IDLE || _s == S.WAIT_AT_ENTRY || _s == S.APPROACH || _s == S.HOLD)
            {
                CommandStop();
                Enter(S.HOLD);
                // Do not 'return'; spawning gate still evaluates at the end
            }
        }

        switch (_s)
        {
            case S.RESET:
                {
                    CommandStop();
                    _armed = !PE_Exit.v; // armed at boot if exit is LOW
                    if (runEnable) Enter(S.IDLE);
                    break;
                }

            case S.IDLE:
                {
                    CommandStop();

                    // MANUAL: one feed per Start press
                    if (!Mode_Auto.v)
                    {
                        if (Cmd_Start.Rising && !PE_Exit.v)
                        {
                            if (PE_Entry.v) Enter(S.WAIT_AT_ENTRY);
                            else Enter(S.FEED_TO_EXIT);
                        }
                        break;
                    }

                    // AUTO: require Start level + armed (EXIT must have gone LOW)
                    if (Mode_Auto.v && Cmd_Start.v && _armed)
                    {
                        if (PE_Entry.v) Enter(S.WAIT_AT_ENTRY);
                        else Enter(S.APPROACH);
                    }
                    break;
                }

            case S.APPROACH:
                {
                    if (!runEnable) { Fault("Run lost during APPROACH"); break; }

                    // Move until entry sensor seen (initial alignment only)
                    CommandRun(SpeedMMps);

                    if (PE_Entry.v)
                    {
                        CommandStop();
                        Enter(S.WAIT_AT_ENTRY);
                    }
                    else if (Now - _enteredAt > WD_ToEntry_s)
                    {
                        Fault("Approach watchdog");
                    }
                    break;
                }

            case S.WAIT_AT_ENTRY:
                {
                    // Small dwell at entry before feeding to exit
                    CommandStop();
                    if (Now - _enteredAt >= StepDelaySec)
                        Enter(S.FEED_TO_EXIT);
                    break;
                }

            case S.FEED_TO_EXIT:
                {
                    if (!runEnable) { Fault("Run lost during FEED_TO_EXIT"); break; }

                    // Feed until EXIT *rising*; armed was cleared on state entry
                    CommandRun(SpeedMMps);

                    // Primary stop: EXIT rising => sheet positioned
                    if (PE_Exit.Rising)
                    {
                        CommandStop();
                        Enter(S.IDLE);

                        // If STOP is still high, fall into HOLD immediately
                        if (Cmd_Stop.v) Enter(S.HOLD);
                        break;
                    }

                    // Watchdog: took too long to find exit
                    if (Now - _enteredAt > WD_ToExit_s)
                    {
                        CommandStop();
                        Fault("ToExit watchdog");
                    }
                    break;
                }

            case S.HOLD:
                {
                    CommandStop();
                    // Resume only when STOP released and Start asserted (AUTO) or Start pressed (MANUAL)
                    if (!Cmd_Stop.v)
                    {
                        if (Mode_Auto.v)
                        {
                            if (Cmd_Start.v) Enter(S.IDLE); // armed is checked in IDLE
                        }
                        else
                        {
                            if (Cmd_Start.Rising) Enter(S.IDLE);
                        }
                    }
                    break;
                }

            case S.FAULT:
                {
                    CommandStop();
                    // Exit via Reset (handled above)
                    break;
                }
        }

        // ---- Timed Spawning Gate (runs regardless of state machine) ----
        // Rule: spawn only while THIS controller is commanding forward (AUTO or MANUAL).
        bool forwardCommanded = _cmdFwd;
        bool spawnPermitted = forwardCommanded && (!SpawnRequireEStopOK || (EStop_OK.v && !_eStopLatched)) && !Cmd_Stop.v;

        if (!spawnPermitted)
        {
            StopSpawning();    // halts immediately; no backlog
            _spawnDelayT = 0f;
        }
        else
        {
            if (!_spawnTicking)
            {
                // optional start delay (belt ramp, mechanical settle)
                _spawnDelayT += Time.fixedDeltaTime;
                if (_spawnDelayT >= SpawnStartDelay_s) StartSpawning();
            }
        }
    }

    // ===== Drive Command Helpers =====
    void CommandRun(float speed)
    {
        Conv_Infeed_TargetSpeed_mmps.Set(speed);
        Conv_Infeed_Fwd.Set(true);
        _cmdFwd = true;
        if (!_spawnTicking) _spawnDelayT = 0f; // restart spawn delay when motion starts
    }

    void CommandStop()
    {
        Conv_Infeed_TargetSpeed_mmps.Set(0f);
        Conv_Infeed_Fwd.Set(false);
        _cmdFwd = false;
    }

    // ===== Timed Spawning Helpers =====
    void StartSpawning()
    {
        if (SpawnSource == null || _spawnTicking) return;

        // Make sure internal Source timers are dead so we are the single cadence
        SpawnSource.CancelInvoke();
        CancelInvoke(nameof(SpawnTick));
        InvokeRepeating(nameof(SpawnTick), 0f, Mathf.Max(0.01f, SpawnPeriod_s));

        _spawnTicking = true;
        if (VerboseLogs) Debug.Log("[Infeed] Spawning START");
    }

    void StopSpawning()
    {
        CancelInvoke(nameof(SpawnTick));
        _spawnTicking = false;
        if (VerboseLogs) Debug.Log("[Infeed] Spawning STOP");
    }

    void SpawnTick()
    {
        if (SpawnSource != null)
            SpawnSource.Generate(); // direct call: lowest-latency path
    }

    // ===== State Helpers =====
    void Enter(S s)
    {
        _prev = _s;
        _s = s;
        _enteredAt = Now;

        // consume armed only when we *start* feeding toward EXIT
        if (s == S.FEED_TO_EXIT) _armed = false;

        if (VerboseLogs) Debug.Log($"[Infeed] {_prev} → {_s} @ {Now:0.000}s (Exit={PE_Exit.v})");
    }

    void Fault(string why)
    {
        if (VerboseLogs) Debug.LogWarning($"[Infeed] FAULT: {why}");
        Enter(S.FAULT);
    }
}
