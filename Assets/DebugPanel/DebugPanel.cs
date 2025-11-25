#if UNITY_EDITOR
using UnityEngine;
using TMPro;
using UnityEditor;
using System.Collections.Generic;

public class DebugPanelPro : MonoBehaviour
{
    [Header("UI Components")]
    [SerializeField] private Canvas canvas;
    [SerializeField] private TextMeshProUGUI debugText;
    [SerializeField] private TextMeshProUGUI fpsText;
    [SerializeField] private TextMeshProUGUI statsText;

    [Header("Log Filtering")]
    public bool showDebug = true;
    public bool showWarnings = true;
    public bool showErrors = true;

    [Header("Debug Settings")]
    [SerializeField] private int maxLines = 30;
    [SerializeField] private float fpsUpdateInterval = 0.5f;
    [SerializeField] private float statsUpdateInterval = 0.5f;

    private Queue<string> logQueue = new Queue<string>();

    private float fpsTimer = 0;
    private float fpsSum = 0;
    private int fpsSamples = 0;

    private float statsTimer = 0;

    private void Awake()
    {
        if (canvas == null) canvas = GetComponent<Canvas>();
        Application.logMessageReceived += HandleUnityLog;
    }

    private void OnDestroy()
    {
        Application.logMessageReceived -= HandleUnityLog;
    }

    private void Update()
    {
        UpdateFPS();
        UpdateStats();
        FlushLogs();
    }

    // ------------------------------
    // 1. UNITY LOG FILTERING
    // ------------------------------
    private void HandleUnityLog(string condition, string stackTrace, LogType type)
    {
        if (type == LogType.Log && !showDebug) return;
        if (type == LogType.Warning && !showWarnings) return;
        if (type == LogType.Error && !showErrors) return;
        if (type == LogType.Exception && !showErrors) return;

        string color = "#FFFFFF";
        if (type == LogType.Warning) color = "#FFFF66";
        if (type == LogType.Error || type == LogType.Exception) color = "#FF4444";

        logQueue.Enqueue($"<color={color}>{condition}</color>\n");
    }

    private void FlushLogs()
    {
        if (debugText == null || logQueue.Count == 0) return;

        while (logQueue.Count > 0)
            debugText.text += logQueue.Dequeue();

        TrimLogLines();
    }

    private void TrimLogLines()
    {
        string[] lines = debugText.text.Split('\n');
        if (lines.Length > maxLines)
        {
            int start = lines.Length - maxLines;
            debugText.text = string.Join("\n", lines, start, maxLines);
        }
    }

    // ------------------------------
    // 2. FPS CALCULATION
    // ------------------------------
    private void UpdateFPS()
    {
        fpsTimer += Time.deltaTime;
        fpsSamples++;
        fpsSum += (1.0f / Time.deltaTime);

        if (fpsTimer >= fpsUpdateInterval)
        {
            int avgFPS = Mathf.RoundToInt(fpsSum / fpsSamples);

            if (avgFPS >= 60) fpsText.color = Color.green;
            else if (avgFPS >= 30) fpsText.color = Color.yellow;
            else fpsText.color = Color.red;

            fpsText.text = $"{avgFPS} FPS";

            fpsTimer = 0;
            fpsSum = 0;
            fpsSamples = 0;
        }
    }

    // ------------------------------
    // 3. RENDER STATS (UnityStats)
    // ------------------------------
    private void UpdateStats()
    {
        statsTimer += Time.deltaTime;
        if (statsTimer < statsUpdateInterval) return;
        statsTimer = 0;

        int tris = UnityStats.triangles;
        int verts = UnityStats.vertices;
        int batches = UnityStats.batches;
        int draws = UnityStats.drawCalls;
        int setPass = UnityStats.setPassCalls;

        string color = "green";
        if (batches > 800) color = "red";
        else if (batches > 400) color = "yellow";

        statsText.text =
            $"<b><color={color}>Render Stats</color></b>\n" +
            $"Tris: {tris:N0}\n" +
            $"Verts: {verts:N0}\n" +
            $"Batches: {batches:N0}\n" +
            $"Draw Calls: {draws:N0}\n" +
            $"SetPass: {setPass:N0}";
    }

    // ------------------------------
    // 4. PUBLIC API
    // ------------------------------
    public static void Log(string msg)
    {
        Debug.Log(msg);
    }
}
#endif
