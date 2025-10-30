using UnityEngine;
using VME.IO; // BoolIn/BoolOut

/// CutterController — exit-edge triggered, one stroke per EXIT rising.
/// - AUTO: fires once per PE_Exit.Rising when armed, GuardOK, BladeUp, Cmd_Start are valid.
/// - MANUAL: fires once per Cmd_Start.Rising (ignores exit), still respects GuardOK & BladeUp.
/// - Re-arm only on PE_Exit.Falling (or if EXIT is LOW at reset).
/// - No spawning, no infeed control, no entry sensor dependency.
///
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

    // ===== Optional HMI (outputs)
    [Header("Optional HMI (Outputs)")]
    public BoolOut CutterBusy;   // on during CUT_DOWN / CUT_UP
    public BoolOut CutterFault;  // latched in FAULT

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
    private enum S { RESET, ARMED, CUT_DOWN, CUT_UP, HOLD, FAULT }
    [SerializeField] private S _s = S.RESET;
    private S _prev;

    // ===== Internals
    private bool _eStopLatched;
    private bool _armed;           // true -> ready to accept next EXIT rising (AUTO)
    private float _stateTimer;

    // ===== Unity
    void Start() => Enter(S.RESET);

    void FixedUpdate()
    {
        // Sample all inputs (updates v/prev/Rising/Falling)
        Cmd_Start.Sample(); Cmd_Stop.Sample(); Cmd_Reset.Sample(); EStop_OK.Sample();
        Mode_Auto.Sample();
        GuardOK.Sample(); BladeUp.Sample(); BladeDown.Sample();
        PE_Exit.Sample();

        // Hard E-Stop latch
        if (!EStop_OK.v)
        {
            _eStopLatched = true;
            Enter(S.RESET);
        }
        if (Cmd_Reset.Rising)
        {
            _eStopLatched = false;
            Enter(S.RESET);
        }

        // Global re-arm: armed becomes true only after a FALLING edge (exit cleared)
        if (PE_Exit.Falling) _armed = true;

        // Boundary STOP behavior:
        // - If in ARMED/HOLD: go/keep HOLD while STOP is high.
        // - If mid-stroke (CUT_*): finish sub-stroke safely; HOLD will be enforced when we next reach ARMED if STOP is still high.
        if (Cmd_Stop.v && _s != S.FAULT)
        {
            if (_s == S.ARMED || _s == S.HOLD)
            {
                Enter(S.HOLD);
                return;
            }
        }

        // FSM
        switch (_s)
        {
            case S.RESET:
                {
                    OutputsOff();
                    _armed = !PE_Exit.v; // armed immediately if EXIT is LOW at reset
                    ClearFaultLamp();

                    if (!_eStopLatched && EStop_OK.v)
                        Enter(S.ARMED);
                    break;
                }

            case S.ARMED:
                {
                    // If STOP asserted, remain HOLD.
                    if (Cmd_Stop.v) { Enter(S.HOLD); break; }

                    OutputsOff();
                    SetBusyLamp(false);

                    // AUTO: require Cmd_Start level, GuardOK, BladeUp, armed, and EXIT rising
                    if (Mode_Auto.v)
                    {
                        if (Cmd_Start.v && _armed && GuardOK.v && BladeUp.v && PE_Exit.Rising)
                        {
                            _armed = false; // consume this rising edge
                            Enter(S.CUT_DOWN);
                            break;
                        }
                    }
                    else
                    {
                        // MANUAL: one stroke per Start rising, guard & blade-up required
                        if (Cmd_Start.Rising && GuardOK.v && BladeUp.v)
                        {
                            Enter(S.CUT_DOWN);
                            break;
                        }
                    }

                    // If STOP is not asserted but EXIT stays HIGH from previous cycle, we wait
                    // for FALLING to re-arm via the global edge handler above.
                    break;
                }

            case S.CUT_DOWN:
                {
                    // Safety while moving down
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
                        // Stroke complete: return to ARMED.
                        SetBusyLamp(false);
                        Blade_JogUp.Set(false);
                        Enter(S.ARMED);

                        // If STOP still held, immediately transition to HOLD from ARMED.
                        if (Cmd_Stop.v) Enter(S.HOLD);
                        break;
                    }

                    if (Watchdog(WD_CutUp_s, "CUT_UP timeout")) break;
                    break;
                }

            case S.HOLD:
                {
                    OutputsOff();
                    SetBusyLamp(false);
                    // Resume only when STOP released; then go to ARMED.
                    if (!Cmd_Stop.v) Enter(S.ARMED);
                    break;
                }

            case S.FAULT:
                {
                    OutputsOff();
                    SetBusyLamp(false);
                    SetFaultLamp(true);
                    // Leave only via Reset (handled above)
                    break;
                }
        }
    }

    // ===== Helpers =====
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
}
