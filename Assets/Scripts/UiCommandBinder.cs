using UnityEngine;
using VME.IO;                 // BoolOut
using Michsky.MUIP;          // ButtonManager

/// Binds MUIP ButtonManager buttons to VME BoolOut command lines.
/// Wire the BoolOuts to your controllers' BoolIn (Cmd_Start/Stop/Reset) using SignalBridgeBoolMulti.
public class UiCommandBinder : MonoBehaviour
{
    [Header("UI Buttons (Modern UI Pack)")]
    public ButtonManager StartButton;
    public ButtonManager StopButton;
    public ButtonManager ResetButton;

    [Header("VME Outputs (bridge these to controllers' BoolIn)")]
    public BoolOut UI_Cmd_Start;    // Bridge to Infeed/Outfeed/Press Cmd_Start (BoolIn)
    public BoolOut UI_Cmd_Stop;     // Bridge to ... Cmd_Stop (BoolIn)
    public BoolOut UI_Cmd_Reset;    // Bridge to ... Cmd_Reset (BoolIn)

    [Header("Behavior")]
    [Tooltip("Start behaves as a maintained level toggle (On/Off).")]
    public bool StartAsToggle = true;

    [Tooltip("Stop behaves as a momentary pulse when false (Off) -> true (On) for the duration below.")]
    public bool StopAsPulse = true;

    [Tooltip("Reset behaves as a momentary pulse for the duration below.")]
    public bool ResetAsPulse = true;

    [Tooltip("Pulse duration in seconds for Stop/Reset when in pulse mode.")]
    [Min(0.02f)] public float PulseSeconds = 0.08f;

    // internal state for toggle
    bool _startLatched;

    void Awake()
    {
        // Defensive: clear outputs at boot
        SafeSet(UI_Cmd_Start, false);
        SafeSet(UI_Cmd_Stop, false);
        SafeSet(UI_Cmd_Reset, false);

        // Hook UI events if present
        if (StartButton != null) StartButton.onClick.AddListener(OnStartClicked);
        if (StopButton != null) StopButton.onClick.AddListener(OnStopClicked);
        if (ResetButton != null) ResetButton.onClick.AddListener(OnResetClicked);
    }

    void OnDestroy()
    {
        if (StartButton != null) StartButton.onClick.RemoveListener(OnStartClicked);
        if (StopButton != null) StopButton.onClick.RemoveListener(OnStopClicked);
        if (ResetButton != null) ResetButton.onClick.RemoveListener(OnResetClicked);
    }

    // -------- UI Handlers --------
    void OnStartClicked()
    {
        if (StartAsToggle)
        {
            _startLatched = !_startLatched;
            SafeSet(UI_Cmd_Start, _startLatched);
        }
        else
        {
            // Momentary start pulse (rare, but supported)
            StopAllCoroutines();
            StartCoroutine(Pulse(UI_Cmd_Start, PulseSeconds));
        }
    }

    void OnStopClicked()
    {
        if (StopAsPulse)
        {
            StopAllCoroutines();
            StartCoroutine(Pulse(UI_Cmd_Stop, PulseSeconds));
        }
        else
        {
            // Maintained stop (level)
            // Tapping the button flips the level
            bool next = !ReadBoolOut(UI_Cmd_Stop);
            SafeSet(UI_Cmd_Stop, next);
        }
    }

    void OnResetClicked()
    {
        if (ResetAsPulse)
        {
            StopAllCoroutines();
            StartCoroutine(Pulse(UI_Cmd_Reset, PulseSeconds));
        }
        else
        {
            // Maintained reset (uncommon)
            bool next = !ReadBoolOut(UI_Cmd_Reset);
            SafeSet(UI_Cmd_Reset, next);
        }
    }

    // -------- Helpers --------
    System.Collections.IEnumerator Pulse(BoolOut line, float seconds)
    {
        SafeSet(line, true);
        var t = Mathf.Max(0.02f, seconds);
        var end = Time.unscaledTime + t;
        while (Time.unscaledTime < end) yield return null;
        SafeSet(line, false);
    }

    void SafeSet(BoolOut line, bool value)
    {
        if (line == null) return;
        line.Set(value);
    }

    bool ReadBoolOut(BoolOut line)
    {
        if (line == null) return false;
        // BoolOut doesn't always expose a getter; if not available, track your own shadow.
        // Here we conservatively return false—only used for maintained mode toggling.
        // Prefer using StartAsToggle + _startLatched for Start, and pulse for Stop/Reset.
        return false;
    }
}
