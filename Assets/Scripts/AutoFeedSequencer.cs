using UnityEngine;
using System;
using realvirtual; // MU, Sensor
using VME.IO;      // BoolIn, BoolOut, FloatIn, FloatOut

/// Auto feed + cut sequencer (UNITS: mm, mm/s) with:
/// - Boundary-gated Stop (no mid-step aborts)
/// - Latched E-Stop (hard kill) requiring Reset
/// - Safe re-feed after debounced exit clear (optional)
/// - Outfeed to pick; pick sensor only stops OUTFEED (auto resumes when low)
/// - Spawn level: LS_Blade_Down↑ .. (debounced) Exit LOW
public class AutoFeedSequencer : MonoBehaviour
{
    // ===== Lifecycle (Inputs)
    [Header("Lifecycle (Inputs)")]
    public BoolIn Cmd_Start, Cmd_Stop, Cmd_Reset;
    public BoolIn EStop_OK;

    // ===== Conveyor & eyes (Inputs)
    [Header("Conveyor & Eyes (Inputs)  [mm / mm/s]")]
    public FloatIn Conv_Infeed_Position_mm;
    public BoolIn Conv_Infeed_IsDriving; // optional
    public BoolIn PE_Cutter_Entry;       // long sheet at entry
    public BoolIn PE_Cutter_Exit;        // cut piece in throat
    public BoolIn PE_Pick_Pos;           // piece arrived at pick

    // ===== Entry Sensor (runtime MU capture)
    [Header("Entry Sensor (runtime MU capture)")]
    public Sensor EntrySensor;

    // ===== Blade safety & limits (Inputs)
    [Header("Blade Safety & Limits (Inputs)")]
    public BoolIn LC_Cutter_Guard_OK;
    public BoolIn LS_Blade_Up;
    public BoolIn LS_Blade_Down;

    // ===== Infeed (Outputs)
    [Header("Infeed (Outputs)  [mm / mm/s]")]
    public FloatOut Conv_Infeed_TargetSpeed_mmps;
    public BoolOut Conv_Infeed_Fwd;

    // ===== Outfeed (Outputs)
    [Header("Outfeed (Outputs)  [mm / mm/s]")]
    public FloatOut Conv_Outfeed_TargetSpeed_mmps;
    public BoolOut Conv_Outfeed_Fwd;

    // ===== Blade jog (Outputs)
    [Header("Blade Jog (Outputs)")]
    public BoolOut Blade_JogDown; // down
    public BoolOut Blade_JogUp;   // up

    // ===== Cut piece spawn (PLC)
    [Header("Cut Piece Spawning (PLC Output)")]
    [Tooltip("Boolean to the Source that generates the cut piece. TRUE on BladeDown↑, FALSE when Exit is debounced LOW.")]
    public BoolOut Source_CutPiece_Generate;

    [Tooltip("Gate spawn/visuals on a permitted stroke only.")]
    public bool RequireCutPermission = true;

    [Header("Options")]
    [Tooltip("If true, automatically meter the next cut after the throat clears. If false, remain idle after delivering to pick.")]
    public bool AutoRefeed = true;

    // ===== Setpoints & watchdogs
    [Header("Setpoints (Units: mm & mm/s)")]
    public float InfeedSpeed_mmps = 800f;
    public float OutfeedSpeed_mmps = 800f;
    [Tooltip("Length to feed after entry eye trips (mm)")]
    public float CutLength_mm = 100f;

    [Header("Timings & Watchdogs (seconds)")]
    public float SettleTime_s = 0.20f;
    public float WD_ToEntry_s = 5.0f;
    public float WD_Feed_Scale = 1.5f;   // feed WD ~ (CutLength/Speed) * scale
    public float WD_CutDown_s = 5.0f;
    public float WD_CutUp_s = 5.0f;
    public float WD_ExitClear_s = 3.0f;  // time to clear cutter throat (safety net; still use debounce)
    public float WD_ToPick_s = 5.0f;     // time from exit clear to reach pick
    public float WD_Refeed_s = 5.0f;     // time from NextFeedArmed to next BladeDown (paused while Stop=1)

    // --- Sensor filtering
    [Header("Sensor filtering")]
    [Tooltip("How long Exit must be LOW to count as 'cleared'.")]
    public float ExitClearDebounce_s = 0.10f;
    [Tooltip("After entering S4, wait this long before evaluating Exit LOW.")]
    public float ExitClearArmingDelay_s = 0.05f;

    [Header("Policy")]
    public bool AllowRetractionOnGuardOpen = true;

    [Header("Debugging")]
    public bool VerboseLogs = true;
    public float LogInterval = 0.10f;

    float _nextLogAt = 0f;
    string lastFault = "";

    // ===== FSM
    public enum STATE { S_RESET, S0_APPROACH, S1_METER_FEED, S2_CUT_DOWN, S3_CUT_UP, S4_RELEASE_TO_PICK, S_HOLD, S_FAULT }
    [SerializeField] public STATE State = STATE.S_RESET;
    STATE _prevState;

    // ===== Internals
    float stateTimer, settleTimer;
    bool runEnable;
    bool EStopLatched;                 // latched hard kill
    bool feedInterlockLatched;
    bool cutPermissionLatched;
    bool NextFeedArmed;                // set after debounced Exit clear (if Entry present)
    float posAtEntry_mm, measuredSpeed_mmps, feedWD_s;

    // Refeed and pick watchdog helpers
    float refeedTimer_s = 0f;
    bool pickTimerActive = false;
    float pickTimer_s = 0f;

    // Exit clear debounce helpers
    bool exitClearedThisCycle = false;
    float s4EnterTime = 0f;
    float exitLowHold_s = 0f;

    // Runtime MU for visuals
    [SerializeField] MU _currentMU;
    int _appearanceIdx = 0;

    void Start()
    {
        Enter(STATE.S_RESET);
        if (EntrySensor != null)
            EntrySensor.EventMUSensor.AddListener(OnEntrySensorEvent);
        else
            Debug.LogWarning("[SEQ] EntrySensor not assigned; cannot capture runtime MU instance.");
    }

    void OnDestroy()
    {
        if (EntrySensor != null)
            EntrySensor.EventMUSensor.RemoveListener(OnEntrySensorEvent);
    }

    void FixedUpdate()
    {
        // Inputs & speed
        SampleInputs();
        DeriveMeasuredSpeed();

        // E-Stop latch
        if (!EStop_OK.v)
        {
            if (!EStopLatched) Debug.LogWarning("[SEQ] E-STOP triggered -> latched");
            EStopLatched = true;
            Enter(STATE.S_RESET);
        }
        runEnable = !EStopLatched && EStop_OK.v;

        // Reset clears E-Stop latch and faults
        if (Cmd_Reset.Rising)
        {
            EStopLatched = false;
            Enter(STATE.S_RESET);
        }

        // Edge logs
        SensorEdgeLogs();

        // FSM (Stop is boundary-gated inside ticks; NOT an immediate motion kill)
        switch (State)
        {
            case STATE.S_RESET: Tick_RESET(); break;
            case STATE.S0_APPROACH: Tick_S0_APPROACH(); break;
            case STATE.S1_METER_FEED: Tick_S1_METER_FEED(); break;
            case STATE.S2_CUT_DOWN: Tick_S2_CUT_DOWN(); break;
            case STATE.S3_CUT_UP: Tick_S3_CUT_UP(); break;
            case STATE.S4_RELEASE_TO_PICK: Tick_S4_RELEASE_TO_PICK(); break;
            case STATE.S_HOLD: Tick_S_HOLD(); break;
            case STATE.S_FAULT: Tick_S_FAULT(); break;
        }

        // Spawn + visuals
        HandleSpawnAndAppearance();

        // Cross-state watchdog timers
        if (NextFeedArmed && !Cmd_Stop.v)
        {
            refeedTimer_s += Time.fixedDeltaTime;
            if (refeedTimer_s > WD_Refeed_s) Fault("Refeed timeout");
        }
        if (pickTimerActive)
        {
            pickTimer_s += Time.fixedDeltaTime;
            if (pickTimer_s > WD_ToPick_s) Fault("ToPick timeout");
        }

        // Logs
        if (VerboseLogs && Time.time >= _nextLogAt)
        {
            _nextLogAt = Time.time + Mathf.Max(0.01f, LogInterval);
            LogTick();
        }
    }

    // ===== Runtime MU capture (reset visuals ONLY when a NEW MU arrives)
    void OnEntrySensorEvent(MU mu, bool occupied)
    {
        if (!occupied || mu == null) return;

        // Only react if this is a different MU than the currently tracked one
        if (mu != _currentMU)
        {
            _currentMU = mu;
            _appearanceIdx = 0;
            ApplyAppearanceByIndex(0); // set 'before cut' variant
            if (VerboseLogs) Debug.Log($"[ENTRY] Captured NEW MU '{_currentMU.name}', stage=0 (visual reset).");
        }
    }

    // ===== States
    void Tick_RESET()
    {
        StopAllMotion();
        Source_CutPiece_Generate.Set(false);

        _appearanceIdx = 0;
        _currentMU = null;

        feedInterlockLatched = false;
        cutPermissionLatched = false;
        NextFeedArmed = false;
        refeedTimer_s = 0f;
        pickTimerActive = false;
        pickTimer_s = 0f;

        exitClearedThisCycle = false;
        s4EnterTime = 0f;
        exitLowHold_s = 0f;

        // Allow Start only if blade up
        if (runEnable && LS_Blade_Up.v && Cmd_Start.Rising)
            Enter(STATE.S0_APPROACH);
    }

    void Tick_S0_APPROACH()
    {
        // Approach until entry eye
        Conv_Infeed_TargetSpeed_mmps.Set(InfeedSpeed_mmps);
        Conv_Infeed_Fwd.Set(runEnable);

        if (PE_Cutter_Entry.v)
        {
            Conv_Infeed_Fwd.Set(false); // stop, settle
            settleTimer += Time.fixedDeltaTime;

            if (settleTimer >= SettleTime_s)
            {
                if (Cmd_Stop.v) return; // boundary gate

                posAtEntry_mm = Conv_Infeed_Position_mm.v;
                float v = Mathf.Max(1f, measuredSpeed_mmps);
                feedWD_s = Mathf.Max(0.2f, (CutLength_mm / v) * WD_Feed_Scale);
                Enter(STATE.S1_METER_FEED);
            }
        }
        else
        {
            Watchdog(WD_ToEntry_s, "Approach watchdog");
        }
    }

    void Tick_S1_METER_FEED()
    {
        if (!feedInterlockLatched) { Fault("Feed latch not set"); return; }
        if (!runEnable) { Fault("EStop/Run lost during metering"); return; }
        if (!LC_Cutter_Guard_OK.v) { Fault("Guard opened during metering"); return; }
        if (!LS_Blade_Up.v) { Fault("Blade not up during metering"); return; }

        // If material lost during metering, end gracefully
        if (!PE_Cutter_Entry.v)
        {
            Conv_Infeed_Fwd.Set(false);
            Debug.LogWarning("[SEQ] Material lost during metering -> ending infeed cycle");
            Enter(STATE.S_HOLD);
            return;
        }

        Conv_Infeed_TargetSpeed_mmps.Set(InfeedSpeed_mmps);
        Conv_Infeed_Fwd.Set(true);

        float delta_mm = Conv_Infeed_Position_mm.v - posAtEntry_mm;
        if (delta_mm < -0.5f) { Fault($"Negative Δ {delta_mm:F1} mm"); return; }
        if (delta_mm > CutLength_mm * 10f) { Fault($"Δ spike {delta_mm:F1} mm"); return; }

        if (delta_mm >= CutLength_mm)
        {
            Conv_Infeed_Fwd.Set(false);
            if (Cmd_Stop.v) return; // boundary gate

            cutPermissionLatched = runEnable && LC_Cutter_Guard_OK.v && LS_Blade_Up.v;
            Debug.Log($"[SEQ] Stroke permission latched: {cutPermissionLatched}");
            Enter(STATE.S2_CUT_DOWN);
            return;
        }

        Watchdog(feedWD_s, "Metering watchdog");
    }

    void Tick_S2_CUT_DOWN()
    {
        if (!runEnable) { Fault("EStop/Run lost on down"); return; }
        if (!LC_Cutter_Guard_OK.v) { Fault("Guard opened on down"); return; }
        if (!cutPermissionLatched) { Fault("Stroke not permitted"); return; }

        Blade_JogUp.Set(false);
        Blade_JogDown.Set(true);

        if (LS_Blade_Down.v)
        {
            Blade_JogDown.Set(false);
            Enter(STATE.S3_CUT_UP);
            return;
        }

        Watchdog(WD_CutDown_s, "Cut-down watchdog");
    }

    void Tick_S3_CUT_UP()
    {
        if (!runEnable) { Fault("EStop/Run lost on up"); return; }
        if (!AllowRetractionOnGuardOpen && !LC_Cutter_Guard_OK.v) { Fault("Guard opened on up (blocked)"); return; }

        Blade_JogDown.Set(false);
        Blade_JogUp.Set(true);

        if (LS_Blade_Up.v && !LS_Blade_Down.v)
        {
            Blade_JogUp.Set(false);
            if (Cmd_Stop.v) return; // boundary gate
            Enter(STATE.S4_RELEASE_TO_PICK);
            return;
        }

        Watchdog(WD_CutUp_s, "Cut-up watchdog");
    }

    void Tick_S4_RELEASE_TO_PICK()
    {
        // Outfeed: only gated by pick sensor (stop when HIGH, resume when LOW)
        Conv_Outfeed_TargetSpeed_mmps.Set(OutfeedSpeed_mmps);
        bool outfeedOK = runEnable && !Cmd_Stop.v && !PE_Pick_Pos.v;
        Conv_Outfeed_Fwd.Set(outfeedOK);

        // --- Debounced Exit Clear (LOW level + arming delay)
        if (!exitClearedThisCycle)
        {
            // Start arming after a short delay to avoid sampling during transition
            bool armingWindowOpen = (Time.time - s4EnterTime) >= ExitClearArmingDelay_s;

            if (armingWindowOpen)
            {
                if (!PE_Cutter_Exit.v)
                {
                    exitLowHold_s += Time.fixedDeltaTime;
                    if (exitLowHold_s >= ExitClearDebounce_s)
                    {
                        exitClearedThisCycle = true;

                        // Reset Source level at the handoff point
                        if (Source_CutPiece_Generate.Get())
                            Source_CutPiece_Generate.Set(false);

                        // Arm next feed only if AutoRefeed is enabled and material present at entry
                        if (AutoRefeed && PE_Cutter_Entry.v)
                        {
                            NextFeedArmed = true;
                            refeedTimer_s = 0f;
                            if (VerboseLogs) Debug.Log("[SEQ] Exit CLEAR (debounced) -> NextFeedArmed=TRUE");
                        }
                        else
                        {
                            NextFeedArmed = false;
                            if (VerboseLogs) Debug.Log("[SEQ] Exit CLEAR -> AutoRefeed disabled or no entry material");
                        }

                        // Start pick watchdog after clear
                        pickTimerActive = true;
                        pickTimer_s = 0f;
                    }
                }
                else
                {
                    // Exit went HIGH again -> reset hold
                    exitLowHold_s = 0f;
                }
            }
        }

        // If AutoRefeed is ON and STOP=0 and interlocks OK, start next feed automatically
        if (AutoRefeed && NextFeedArmed && runEnable && LC_Cutter_Guard_OK.v && LS_Blade_Up.v && !Cmd_Stop.v)
        {
            if (!PE_Cutter_Entry.v)
            {
                NextFeedArmed = false; // sheet ran out after arming
            }
            else
            {
                posAtEntry_mm = Conv_Infeed_Position_mm.v;
                float v = Mathf.Max(1f, measuredSpeed_mmps);
                feedWD_s = Mathf.Max(0.2f, (CutLength_mm / v) * WD_Feed_Scale);

                NextFeedArmed = false;
                refeedTimer_s = 0f;

                Enter(STATE.S1_METER_FEED);
                return;
            }
        }

        // Safety net: if exit never clears at all, still watch it
        if (!exitClearedThisCycle)
            Watchdog(WD_ExitClear_s, "Exit clear watchdog");
    }

    void Tick_S_HOLD()
    {
        StopAllMotion();

        // Idle until Start again
        if (runEnable && LS_Blade_Up.v && Cmd_Start.Rising)
        {
            if (PE_Cutter_Entry.v)
            {
                posAtEntry_mm = Conv_Infeed_Position_mm.v;
                float v = Mathf.Max(1f, measuredSpeed_mmps);
                feedWD_s = Mathf.Max(0.2f, (CutLength_mm / v) * WD_Feed_Scale);
                Enter(STATE.S1_METER_FEED);
            }
            else
            {
                Enter(STATE.S0_APPROACH);
            }
        }
    }

    void Tick_S_FAULT()
    {
        StopAllMotion();
        Source_CutPiece_Generate.Set(false);
    }

    // ===== Helpers
    void Enter(STATE s)
    {
        _prevState = State;
        State = s;
        stateTimer = 0f; settleTimer = 0f;

        if (s == STATE.S1_METER_FEED)
        {
            feedInterlockLatched = (!EStopLatched) && EStop_OK.v && LC_Cutter_Guard_OK.v && LS_Blade_Up.v;
            Debug.Log($"[SEQ] S1 start: pos={Conv_Infeed_Position_mm.v:F1}mm; Cut={CutLength_mm:F1}mm; v={measuredSpeed_mmps:F1}mm/s; WD_feed={feedWD_s:0.00}s");
        }

        if (s == STATE.S_RESET || s == STATE.S_FAULT)
        {
            feedInterlockLatched = false;
            cutPermissionLatched = false;
            NextFeedArmed = false;
            refeedTimer_s = 0f;
            pickTimerActive = false;
            pickTimer_s = 0f;

            exitClearedThisCycle = false;
            s4EnterTime = 0f;
            exitLowHold_s = 0f;
        }

        if (s == STATE.S4_RELEASE_TO_PICK)
        {
            // Initialize exit-clear debounce
            s4EnterTime = Time.time;
            exitLowHold_s = 0f;
            exitClearedThisCycle = false;

            // If exit already LOW, we'll still require ExitClearDebounce_s after the arming delay.
            pickTimerActive = false; // will start when exit clear debounced
            pickTimer_s = 0f;
        }

        if (VerboseLogs)
            Debug.Log($"[SEQ] STATE: {_prevState} -> {State} @ {Time.time:0.000}s (Stop={Cmd_Stop.v}, EStopLatched={EStopLatched}, FeedLatched={feedInterlockLatched}, StrokeLatched={cutPermissionLatched}, NextFeedArmed={NextFeedArmed})");
    }

    void StopAllMotion()
    {
        Conv_Infeed_Fwd.Set(false);
        Conv_Outfeed_Fwd.Set(false);
        Blade_JogDown.Set(false);
        Blade_JogUp.Set(false);
    }

    void Watchdog(float maxSec, string label)
    {
        stateTimer += Time.fixedDeltaTime;
        if (stateTimer > maxSec) Fault($"{label} exceeded {maxSec:0.00}s in {State}");
    }

    void Fault(string why)
    {
        lastFault = why;
        Debug.LogWarning($"[SEQ] FAULT: {why} @ {Time.time:0.000}s (State={State})");
        Enter(STATE.S_FAULT);
    }

    void SampleInputs()
    {
        Cmd_Start.Sample(); Cmd_Stop.Sample(); Cmd_Reset.Sample();
        EStop_OK.Sample();
        PE_Cutter_Entry.Sample();
        PE_Cutter_Exit.Sample();
        PE_Pick_Pos.Sample();
        Conv_Infeed_Position_mm.Sample();
        Conv_Infeed_IsDriving.Sample();
        LC_Cutter_Guard_OK.Sample();
        LS_Blade_Up.Sample();
        LS_Blade_Down.Sample();
    }

    void SensorEdgeLogs()
    {
        if (PE_Cutter_Entry.Rising) Debug.Log($"[EDGE] PE_Cutter_Entry ↑ @ {Time.time:0.000}s");
        if (PE_Cutter_Entry.Falling) Debug.Log($"[EDGE] PE_Cutter_Entry ↓ @ {Time.time:0.000}s");
        if (PE_Cutter_Exit.Rising) Debug.Log($"[EDGE] PE_Cutter_Exit ↑ @ {Time.time:0.000}s");
        if (PE_Cutter_Exit.Falling) Debug.Log($"[EDGE] PE_Cutter_Exit ↓ @ {Time.time:0.000}s");
        if (PE_Pick_Pos.Rising) Debug.Log($"[EDGE] PE_Pick_Pos ↑ @ {Time.time:0.000}s");
        if (PE_Pick_Pos.Falling) Debug.Log($"[EDGE] PE_Pick_Pos ↓ @ {Time.time:0.000}s");
        if (LS_Blade_Up.Rising) Debug.Log($"[EDGE] LS_Blade_Up ↑ @ {Time.time:0.000}s");
        if (LS_Blade_Up.Falling) Debug.Log($"[EDGE] LS_Blade_Up ↓ @ {Time.time:0.000}s");
        if (LS_Blade_Down.Rising) Debug.Log($"[EDGE] LS_Blade_Down ↑ @ {Time.time:0.000}s");
        if (LS_Blade_Down.Falling) Debug.Log($"[EDGE] LS_Blade_Down ↓ @ {Time.time:0.000}s");
        if (LC_Cutter_Guard_OK.Rising) Debug.Log($"[EDGE] Guard OK ↑ @ {Time.time:0.000}s");
        if (LC_Cutter_Guard_OK.Falling) Debug.Log($"[EDGE] Guard OK ↓ @ {Time.time:0.000}s");
    }

    // ===== Spawn + MUAppearences switching =====
    void HandleSpawnAndAppearance()
    {
        bool inCutPhase = (State == STATE.S2_CUT_DOWN) || (State == STATE.S3_CUT_UP);
        bool gateOK = !RequireCutPermission || cutPermissionLatched;

        // On BladeDown↑: generate & advance visual
        if (LS_Blade_Down.Rising && inCutPhase && gateOK)
        {
            if (!Source_CutPiece_Generate.Get())
            {
                Source_CutPiece_Generate.Set(true);
                if (VerboseLogs) Debug.Log("[CUT] Generate=TRUE (BladeDown↑)");
            }

            if (_currentMU != null && _currentMU.MUAppearences != null && _currentMU.MUAppearences.Count > 0)
            {
                int last = _currentMU.MUAppearences.Count - 1;
                _appearanceIdx = Mathf.Min(_appearanceIdx + 1, last);
                ApplyAppearanceByIndex(_appearanceIdx);
                if (VerboseLogs) Debug.Log($"[VIS] MU '{_currentMU.name}' -> MUAppearences idx={_appearanceIdx}/{last}");
            }
            else if (VerboseLogs)
            {
                Debug.LogWarning("[VIS] No runtime MU or MUAppearences not configured; skipping visual swap.");
            }
        }
        // Source level resets when Exit debounced LOW in S4.
    }

    // Enable selected appearance, disable all others
    void ApplyAppearanceByIndex(int idx)
    {
        if (_currentMU == null || _currentMU.MUAppearences == null || _currentMU.MUAppearences.Count == 0) return;
        idx = Mathf.Clamp(idx, 0, _currentMU.MUAppearences.Count - 1);
        for (int i = 0; i < _currentMU.MUAppearences.Count; i++)
        {
            var go = _currentMU.MUAppearences[i];
            if (!go) continue;
            go.SetActive(i == idx);
        }
    }

    // Measured speed from encoder (mm/s)
    void DeriveMeasuredSpeed()
    {
        float du_mm = Conv_Infeed_Position_mm.v - Conv_Infeed_Position_mm.prev;
        float dt = Mathf.Max(1e-4f, Time.fixedDeltaTime);
        measuredSpeed_mmps = du_mm / dt;
    }

    void LogTick()
    {
        float delta_mm = Conv_Infeed_Position_mm.v - posAtEntry_mm;
        string line =
            $"[TICK {Time.time:0.000}] State={State} | " +
            $"Stop={Cmd_Stop.v} EStopLatched={EStopLatched} FeedLatched={feedInterlockLatched} StrokeLatched={cutPermissionLatched} NextFeedArmed={NextFeedArmed} | " +
            $"PEin={PE_Cutter_Entry.v} PEx={PE_Cutter_Exit.v} Pick={PE_Pick_Pos.v} Up={LS_Blade_Up.v} Down={LS_Blade_Down.v} Guard={LC_Cutter_Guard_OK.v} | " +
            $"pos={Conv_Infeed_Position_mm.v:F1}mm Δ={delta_mm:F1}mm (L={CutLength_mm:F1}mm) v={measuredSpeed_mmps:F1}mm/s WD_feed={feedWD_s:0.00}s | " +
            $"Gen={Source_CutPiece_Generate.Get()} | " +
            $"refeedT={refeedTimer_s:0.00}s pickT={(pickTimerActive ? pickTimer_s : 0f):0.00}s";
        Debug.Log(line);
    }
}
