#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif


/// <summary>
/// Advanced VR Performance Profiler for Quest 3 Optimization
/// - Collects comprehensive performance data during play mode
/// - Analyzes on exit with min/max/avg/percentiles
/// - Provides Quest 3-specific optimization recommendations
/// - Optional manual export to JSON
/// </summary>
public class VRPerformanceProfiler : MonoBehaviour
{
    [Header("Profiling Settings")]
    [Tooltip("Time to wait before recording (skips startup lag)")]
    public float warmupTime = 2.0f;

    [Tooltip("How often to sample stats (seconds)")]
    public float sampleInterval = 0.5f;

    [Tooltip("Filter startup/shutdown spikes")]
    public bool filterStartupShutdownSpikes = true;

    [Tooltip("Enable detailed console logging during play")]
    public bool verboseLogging = false;

    [Tooltip("Manual export - press 'E' during play to save JSON report")]
    public bool enableManualExport = true;

    [Header("Quest 3 Target Thresholds")]
    [Tooltip("Target FPS for Quest 3")]
    public float targetFPS = 72f;

    [Tooltip("Maximum acceptable draw calls")]
    public int maxDrawCalls = 300;

    [Tooltip("Maximum acceptable SetPass calls")]
    public int maxSetPassCalls = 150;

    [Tooltip("Maximum acceptable batches")]
    public int maxBatches = 300;

    // Performance data collection
    private List<PerformanceSample> samples = new List<PerformanceSample>();
    private float sessionStartTime;
    private float lastSampleTime;
    private bool isRecording = false;
    private float warmupTimer = 0f;

    // Track last session for comparison
    private SessionReport currentSessionReport;

    private void OnEnable()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        sessionStartTime = Time.realtimeSinceStartup;
        lastSampleTime = sessionStartTime;
        warmupTimer = 0f;
        isRecording = false;
        samples.Clear();

        Debug.Log("<color=cyan>═══════════════════════════════════════════════════════</color>");
        Debug.Log("<color=cyan>🎮 VR Performance Profiler Started</color>");
        Debug.Log($"<color=yellow>⏱️ Warmup period: {warmupTime}s (skipping startup spikes)</color>");
        if (enableManualExport)
        {
            Debug.Log("<color=yellow>📁 Manual Export: Press 'E' during play to save JSON report</color>");
        }
        Debug.Log("<color=cyan>═══════════════════════════════════════════════════════</color>");
    }

    private void OnDisable()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
    }

    private void OnDestroy()
    {
        // Ensure we unsubscribe from static event when destroyed
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
    }

    private void Update()
    {
        // Handle warmup period
        if (!isRecording)
        {
            warmupTimer += Time.unscaledDeltaTime;
            if (warmupTimer >= warmupTime)
            {
                isRecording = true;
                sessionStartTime = Time.realtimeSinceStartup;
                lastSampleTime = sessionStartTime;
                Debug.Log("<color=green>🔴 Recording Started - Profiling in progress...</color>");
            }
            return;
        }

        // Sample at interval
        if (Time.realtimeSinceStartup - lastSampleTime >= sampleInterval)
        {
            CollectSample();
            lastSampleTime = Time.realtimeSinceStartup;
        }

        // Manual export on 'E' key press (supports old + new input backends)
        if (enableManualExport && IsExportKeyPressed())
        {
            ExportSessionData();
        }

    }

    private void CollectSample()
    {
        var sample = new PerformanceSample
        {
            timestamp = Time.realtimeSinceStartup - sessionStartTime,

            // Rendering stats
            fps = 1f / Time.unscaledDeltaTime,
            triangles = UnityStats.triangles,
            vertices = UnityStats.vertices,
            batches = UnityStats.batches,
            drawCalls = UnityStats.drawCalls,
            setPassCalls = UnityStats.setPassCalls,

            // Memory stats
            totalMemoryMB = UnityStats.usedTextureMemorySize / (1024f * 1024f),
            renderTextureMemoryMB = UnityStats.renderTextureBytes / (1024f * 1024f),

            // Additional rendering info
            dynamicBatchedDrawCalls = UnityStats.dynamicBatchedDrawCalls,
            staticBatchedDrawCalls = UnityStats.staticBatchedDrawCalls,
            instancedBatchedDrawCalls = UnityStats.instancedBatchedDrawCalls,

            // Shadow stats
            shadowCasters = UnityStats.shadowCasters,

            // Timing
            frameTimeMS = Time.unscaledDeltaTime * 1000f
        };

        samples.Add(sample);

        if (verboseLogging && samples.Count % 10 == 0)
        {
            Debug.Log(
                $"[Frame {samples.Count}] FPS={sample.fps:F1} | " +
                $"DrawCalls={sample.drawCalls} | " +
                $"SetPass={sample.setPassCalls} | " +
                $"Batches={sample.batches} | " +
                $"Tris={sample.triangles:N0}"
            );
        }
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode)
        {
            AnalyzeAndReport();
        }
    }

    private void AnalyzeAndReport()
    {
        if (samples.Count == 0)
        {
            Debug.LogWarning("No performance samples collected.");
            return;
        }

        Debug.Log($"<color=yellow>📊 Analyzing {samples.Count} samples...</color>");

        var workingSamples = new List<PerformanceSample>(samples);
        int originalCount = workingSamples.Count;

        // Only filter startup/shutdown spikes if enabled
        if (filterStartupShutdownSpikes && workingSamples.Count > 4)
        {
            // Remove first 2 samples (startup stabilization)
            workingSamples.RemoveRange(0, 2);

            // Remove last 2 samples (shutdown artifacts)
            workingSamples.RemoveRange(workingSamples.Count - 2, 2);

            Debug.Log($"<color=yellow>🗑️ Filtered {originalCount - workingSamples.Count} startup/shutdown samples</color>");
        }

        if (workingSamples.Count == 0)
        {
            Debug.LogWarning("No samples remaining after filtering.");
            return;
        }

        float sessionDuration = workingSamples[workingSamples.Count - 1].timestamp - workingSamples[0].timestamp;

        // Create session report
        var report = new SessionReport
        {
            timestamp = System.DateTime.Now,
            duration = sessionDuration,
            sampleCount = workingSamples.Count,
            targetFPS = targetFPS,

            // Calculate statistics for each metric
            fpsStats = CalculateStats(workingSamples.Select(s => s.fps)),
            drawCallStats = CalculateStats(workingSamples.Select(s => (float)s.drawCalls)),
            setPassStats = CalculateStats(workingSamples.Select(s => (float)s.setPassCalls)),
            batchStats = CalculateStats(workingSamples.Select(s => (float)s.batches)),
            triangleStats = CalculateStats(workingSamples.Select(s => (float)s.triangles)),
            vertexStats = CalculateStats(workingSamples.Select(s => (float)s.vertices)),
            frameTimeStats = CalculateStats(workingSamples.Select(s => s.frameTimeMS)),
            memoryStats = CalculateStats(workingSamples.Select(s => s.totalMemoryMB)),

            // Batching breakdown
            avgDynamicBatched = (float)workingSamples.Average(s => s.dynamicBatchedDrawCalls),
            avgStaticBatched = (float)workingSamples.Average(s => s.staticBatchedDrawCalls),
            avgInstancedBatched = (float)workingSamples.Average(s => s.instancedBatchedDrawCalls),

            // Quest 3 compliance
            meetsTargetFPS = CalculateStats(workingSamples.Select(s => s.fps)).avg >= targetFPS,
            meetsDrawCallTarget = CalculateStats(workingSamples.Select(s => (float)s.drawCalls)).avg <= maxDrawCalls,
            meetsSetPassTarget = CalculateStats(workingSamples.Select(s => (float)s.setPassCalls)).avg <= maxSetPassCalls,
            meetsBatchTarget = CalculateStats(workingSamples.Select(s => (float)s.batches)).avg <= maxBatches
        };

        currentSessionReport = report;

        // Generate and display report
        string reportText = GenerateReport(report);
        Debug.Log(reportText);
    }

    private MetricStats CalculateStats(IEnumerable<float> values)
    {
        var list = values.ToList();
        list.Sort();

        return new MetricStats
        {
            min = list.Min(),
            max = list.Max(),
            avg = (float)list.Average(),
            median = list[list.Count / 2],
            p95 = list[(int)(list.Count * 0.95f)],
            p99 = list[(int)(list.Count * 0.99f)]
        };
    }

    private string GenerateReport(SessionReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine("\n<color=cyan>═══════════════════════════════════════════════════════════════════</color>");
        sb.AppendLine("<color=cyan>🎮 VR PERFORMANCE ANALYSIS REPORT - Quest 3 Optimization</color>");
        sb.AppendLine("<color=cyan>═══════════════════════════════════════════════════════════════════</color>");

        sb.AppendLine($"\n<color=white><b>Session Info</b></color>");
        sb.AppendLine($"  Duration: {report.duration:F1}s (after {warmupTime}s warmup)");
        sb.AppendLine($"  Samples: {report.sampleCount} ({report.sampleCount / report.duration:F1} samples/sec)");
        sb.AppendLine($"  Time: {report.timestamp:yyyy-MM-dd HH:mm:ss}");

        // Performance Score
        float score = CalculatePerformanceScore(report);
        string scoreColor = score >= 80 ? "green" : score >= 60 ? "yellow" : "red";
        string scoreRating = score >= 80 ? "Excellent" : score >= 60 ? "Good" : score >= 40 ? "Fair" : "Poor";

        sb.AppendLine($"\n<color={scoreColor}><b>━━━ PERFORMANCE SCORE: {score:F1}/100 ({scoreRating}) ━━━</b></color>");

        // FPS Analysis
        sb.AppendLine($"\n<color=white><b>📊 Frame Rate (Target: {report.targetFPS} FPS)</b></color>");
        sb.AppendLine(FormatMetricLine("FPS", report.fpsStats, report.targetFPS, true));
        sb.AppendLine(FormatMetricLine("Frame Time", report.frameTimeStats, 1000f / report.targetFPS, false, "ms"));

        string fpsStatus = report.meetsTargetFPS ?
            $"<color=green>✓ Meets target ({report.fpsStats.avg:F1} FPS)</color>" :
            $"<color=red>✗ Below target (need +{report.targetFPS - report.fpsStats.avg:F1} FPS)</color>";
        sb.AppendLine($"  Status: {fpsStatus}");

        // Frame stability analysis
        float fpsStability = (report.fpsStats.min / report.fpsStats.avg) * 100f;
        string stabilityColor = fpsStability >= 90 ? "green" : fpsStability >= 80 ? "yellow" : "red";
        sb.AppendLine($"  Stability: <color={stabilityColor}>{fpsStability:F1}% (Min FPS as % of Avg)</color>");

        // Rendering Stats
        sb.AppendLine($"\n<color=white><b>🎨 Rendering Metrics</b></color>");
        sb.AppendLine(FormatMetricLine("Draw Calls", report.drawCallStats, maxDrawCalls, false));
        sb.AppendLine(FormatMetricLine("SetPass Calls", report.setPassStats, maxSetPassCalls, false));
        sb.AppendLine(FormatMetricLine("Batches", report.batchStats, maxBatches, false));
        sb.AppendLine(FormatMetricLine("Triangles", report.triangleStats, 500000, false));
        sb.AppendLine(FormatMetricLine("Vertices", report.vertexStats, 750000, false));

        // Batching Breakdown
        sb.AppendLine($"\n<color=white><b>🔄 Batching Analysis</b></color>");
        float totalBatched = report.avgDynamicBatched + report.avgStaticBatched + report.avgInstancedBatched;
        float batchingEfficiency = report.drawCallStats.avg > 0 ? (totalBatched / report.drawCallStats.avg) * 100f : 0f;

        sb.AppendLine($"  Dynamic Batched: {report.avgDynamicBatched:F1} draw calls");
        sb.AppendLine($"  Static Batched: {report.avgStaticBatched:F1} draw calls");
        sb.AppendLine($"  GPU Instanced: {report.avgInstancedBatched:F1} draw calls");
        sb.AppendLine($"  Batching Efficiency: {batchingEfficiency:F1}% of draw calls batched");

        if (batchingEfficiency < 30)
        {
            sb.AppendLine($"  <color=red>⚠ Low batching efficiency! Enable static batching and GPU instancing.</color>");
        }
        else if (batchingEfficiency < 60)
        {
            sb.AppendLine($"  <color=yellow>⚠ Moderate batching. Room for improvement.</color>");
        }
        else
        {
            sb.AppendLine($"  <color=green>✓ Good batching efficiency!</color>");
        }

        // Memory Stats
        sb.AppendLine($"\n<color=white><b>💾 Memory Usage</b></color>");
        sb.AppendLine(FormatMetricLine("Total Texture Memory", report.memoryStats, 1024, false, "MB"));

        if (report.memoryStats.avg > 1024)
        {
            float overage = report.memoryStats.avg - 1024;
            sb.AppendLine($"  <color=yellow>⚠ {overage:F0}MB over recommended limit for Quest 3</color>");
        }

        // Quest 3 Compliance
        sb.AppendLine($"\n<color=white><b>🎯 Quest 3 Compliance Check</b></color>");
        sb.AppendLine($"  FPS Target: {FormatComplianceStatus(report.meetsTargetFPS, report.fpsStats.avg, report.targetFPS, "FPS")}");
        sb.AppendLine($"  Draw Calls: {FormatComplianceStatus(report.meetsDrawCallTarget, report.drawCallStats.avg, maxDrawCalls, "calls")}");
        sb.AppendLine($"  SetPass Calls: {FormatComplianceStatus(report.meetsSetPassTarget, report.setPassStats.avg, maxSetPassCalls, "calls")}");
        sb.AppendLine($"  Batches: {FormatComplianceStatus(report.meetsBatchTarget, report.batchStats.avg, maxBatches, "batches")}");

        int passedChecks = (report.meetsTargetFPS ? 1 : 0) + (report.meetsDrawCallTarget ? 1 : 0) +
                          (report.meetsSetPassTarget ? 1 : 0) + (report.meetsBatchTarget ? 1 : 0);
        string complianceColor = passedChecks == 4 ? "green" : passedChecks >= 2 ? "yellow" : "red";
        sb.AppendLine($"  <color={complianceColor}>Overall: {passedChecks}/4 checks passed</color>");

        // Performance Issues & Recommendations
        sb.AppendLine($"\n<color=white><b>💡 OPTIMIZATION RECOMMENDATIONS</b></color>");
        var recommendations = GenerateRecommendations(report);
        if (recommendations.Count == 0)
        {
            sb.AppendLine("  <color=green>✓ No major issues detected! Scene is well-optimized for Quest 3.</color>");
        }
        else
        {
            for (int i = 0; i < recommendations.Count; i++)
            {
                sb.AppendLine($"  {i + 1}. {recommendations[i]}");
            }
        }

        // Detailed Stats Table
        sb.AppendLine($"\n<color=white><b>📋 Detailed Statistics (Percentile Analysis)</b></color>");
        sb.AppendLine("  Metric              Min      Avg      Max      P95      P99");
        sb.AppendLine("  ───────────────────────────────────────────────────────────");
        sb.AppendLine(FormatStatsRow("FPS", report.fpsStats));
        sb.AppendLine(FormatStatsRow("Draw Calls", report.drawCallStats));
        sb.AppendLine(FormatStatsRow("SetPass", report.setPassStats));
        sb.AppendLine(FormatStatsRow("Batches", report.batchStats));
        sb.AppendLine(FormatStatsRow("Triangles", report.triangleStats));
        sb.AppendLine(FormatStatsRow("Frame Time (ms)", report.frameTimeStats));

        sb.AppendLine("\n<color=cyan>═══════════════════════════════════════════════════════════════════</color>");
        if (enableManualExport)
        {
            sb.AppendLine("<color=yellow>💾 Press 'E' during play to export JSON report to Assets/PerformanceReports/</color>");
        }
        sb.AppendLine("<color=cyan>═══════════════════════════════════════════════════════════════════</color>\n");

        return sb.ToString();
    }

    private float CalculatePerformanceScore(SessionReport report)
    {
        float score = 100f;

        // FPS penalty
        if (report.fpsStats.avg < report.targetFPS)
        {
            float fpsDiff = report.targetFPS - report.fpsStats.avg;
            score -= Mathf.Min(fpsDiff * 2f, 40f);
        }

        // Draw calls penalty
        if (report.drawCallStats.avg > maxDrawCalls)
        {
            score -= Mathf.Min((report.drawCallStats.avg - maxDrawCalls) / 10f, 20f);
        }

        // SetPass penalty
        if (report.setPassStats.avg > maxSetPassCalls)
        {
            score -= Mathf.Min((report.setPassStats.avg - maxSetPassCalls) / 5f, 20f);
        }

        // Frame time variability penalty
        float frameTimeVariability = report.frameTimeStats.max - report.frameTimeStats.min;
        if (frameTimeVariability > 20f)
        {
            score -= Mathf.Min(frameTimeVariability / 5f, 10f);
        }

        // Batching bonus
        float totalBatched = report.avgDynamicBatched + report.avgStaticBatched + report.avgInstancedBatched;
        float batchingEfficiency = report.drawCallStats.avg > 0 ? (totalBatched / report.drawCallStats.avg) * 100f : 0f;
        if (batchingEfficiency > 60f)
        {
            score += 5f;
        }

        return Mathf.Clamp(score, 0f, 100f);
    }

    private List<string> GenerateRecommendations(SessionReport report)
    {
        var recs = new List<string>();

        // FPS recommendations
        if (!report.meetsTargetFPS)
        {
            float fpsDiff = report.targetFPS - report.fpsStats.avg;
            recs.Add($"<color=yellow>🎯 FPS below target by {fpsDiff:F1}. Primary optimization needed.</color>");

            if (report.drawCallStats.avg > maxDrawCalls)
            {
                recs.Add($"   → Reduce draw calls ({report.drawCallStats.avg:F0} → {maxDrawCalls}): Consolidate materials and create atlases");
            }

            if (report.setPassStats.avg > maxSetPassCalls)
            {
                recs.Add($"   → Reduce SetPass calls ({report.setPassStats.avg:F0} → {maxSetPassCalls}): Merge materials with same shader");
            }
        }

        // Frame stability
        float fpsStability = (report.fpsStats.min / report.fpsStats.avg) * 100f;
        if (fpsStability < 80)
        {
            recs.Add($"<color=yellow>📉 Unstable frame rate (min {report.fpsStats.min:F1} vs avg {report.fpsStats.avg:F1})</color>");
            recs.Add($"   → Profile CPU spikes with Unity Profiler");
            recs.Add($"   → Check for GC allocations causing stutters");
        }

        // Draw call recommendations
        if (report.drawCallStats.avg > maxDrawCalls)
        {
            recs.Add($"<color=yellow>🎨 Draw calls too high ({report.drawCallStats.avg:F0} vs target {maxDrawCalls})</color>");
            recs.Add($"   → Enable static batching in Player Settings");
            recs.Add($"   → Create texture atlases for materials with same shader");
            recs.Add($"   → Use GPU instancing for repeated objects");
        }

        // SetPass recommendations
        if (report.setPassStats.avg > maxSetPassCalls)
        {
            recs.Add($"<color=yellow>⚙️ SetPass calls too high ({report.setPassStats.avg:F0} vs target {maxSetPassCalls})</color>");
            recs.Add($"   → Too many unique materials detected");
            recs.Add($"   → Consolidate materials and reduce shader variants");
        }

        // Batching recommendations
        float totalBatched = report.avgDynamicBatched + report.avgStaticBatched + report.avgInstancedBatched;
        float batchingEfficiency = report.drawCallStats.avg > 0 ? (totalBatched / report.drawCallStats.avg) * 100f : 0f;

        if (batchingEfficiency < 30 && report.drawCallStats.avg > 100)
        {
            recs.Add($"<color=yellow>🔄 Poor batching efficiency ({batchingEfficiency:F1}%)</color>");
            recs.Add($"   → Enable 'Static Batching' in Player Settings > Other Settings");
            recs.Add($"   → Mark static environment objects as Static in Inspector");
            recs.Add($"   → Enable 'GPU Instancing' on materials for repeated objects");
        }

        if (report.avgStaticBatched < 10 && report.drawCallStats.avg > 100)
        {
            recs.Add($"<color=yellow>📦 Static batching not active ({report.avgStaticBatched:F1} calls)</color>");
            recs.Add($"   → Verify Static Batching is enabled in Player Settings");
            recs.Add($"   → Mark building/environment GameObjects as Static");
        }

        if (report.avgInstancedBatched < 5 && report.drawCallStats.avg > 100)
        {
            recs.Add($"<color=yellow>🔁 GPU instancing not utilized</color>");
            recs.Add($"   → Enable 'GPU Instancing' checkbox on materials");
            recs.Add($"   → Ensure repeated objects share same mesh + material");
        }

        // Triangle count
        if (report.triangleStats.avg > 500000)
        {
            recs.Add($"<color=yellow>🔺 High triangle count ({report.triangleStats.avg / 1000:F0}K tris)</color>");
            recs.Add($"   → Implement LOD (Level of Detail) system");
            recs.Add($"   → Optimize mesh complexity in 3D modeling software");
        }

        // Memory recommendations
        if (report.memoryStats.avg > 1024)
        {
            recs.Add($"<color=yellow>💾 High texture memory ({report.memoryStats.avg:F0}MB)</color>");
            recs.Add($"   → Downscale textures to 1K or lower for VR");
            recs.Add($"   → Use ASTC compression format for Quest 3");
            recs.Add($"   → Consider texture atlasing to reduce memory overhead");
        }

        // Frame time variance
        float frameTimeVariability = report.frameTimeStats.max - report.frameTimeStats.min;
        if (frameTimeVariability > 20f)
        {
            recs.Add($"<color=yellow>⏱️ Inconsistent frame times (variance: {frameTimeVariability:F1}ms)</color>");
            recs.Add($"   → CPU spikes or GC stalls detected");
            recs.Add($"   → Use Unity Profiler to identify bottleneck");
        }

        return recs;
    }

    private string FormatMetricLine(string name, MetricStats stats, float target, bool higherIsBetter, string unit = "")
    {
        bool meetsTarget = higherIsBetter ? stats.avg >= target : stats.avg <= target;
        string statusIcon = meetsTarget ? "✓" : "✗";
        string statusColor = meetsTarget ? "green" : "red";

        return $"  {name,-20} Avg: {stats.avg,7:F1}{unit}  Min: {stats.min,7:F1}{unit}  Max: {stats.max,7:F1}{unit}  " +
               $"<color={statusColor}>{statusIcon}</color>";
    }

    private string FormatComplianceStatus(bool meets, float current, float target, string unit)
    {
        if (meets)
        {
            return $"<color=green>✓ Pass</color> ({current:F1} {unit})";
        }
        else
        {
            float diff = Mathf.Abs(current - target);
            return $"<color=red>✗ Fail</color> ({current:F1} vs {target} {unit}, off by {diff:F1})";
        }
    }

    private string FormatStatsRow(string name, MetricStats stats)
    {
        return $"  {name,-18} {stats.min,7:F1} {stats.avg,8:F1} {stats.max,8:F1} {stats.p95,8:F1} {stats.p99,8:F1}";
    }

    private void ExportSessionData()
    {
        if (currentSessionReport == null || samples.Count == 0)
        {
            Debug.LogWarning("⚠ No session data to export. Play the scene first.");
            return;
        }

        string dir = "Assets/PerformanceReports";
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");

        // Save JSON data
        string jsonFile = Path.Combine(dir, $"PerformanceReport_{timestamp}.json");
        string json = JsonUtility.ToJson(currentSessionReport, true);
        File.WriteAllText(jsonFile, json);

        Debug.Log($"<color=green>✅ Performance report exported to: {jsonFile}</color>");

        AssetDatabase.Refresh();
    }
    private bool IsExportKeyPressed()
    {
#if ENABLE_INPUT_SYSTEM
        // New Input System
        return Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
    // Old Input Manager
    return Input.GetKeyDown(KeyCode.E);
#else
    return false;
#endif
    }


    [System.Serializable]
    private class PerformanceSample
    {
        public float timestamp;
        public float fps;
        public int triangles;
        public int vertices;
        public int batches;
        public int drawCalls;
        public int setPassCalls;
        public float totalMemoryMB;
        public float renderTextureMemoryMB;
        public int dynamicBatchedDrawCalls;
        public int staticBatchedDrawCalls;
        public int instancedBatchedDrawCalls;
        public int shadowCasters;
        public float frameTimeMS;
    }

    [System.Serializable]
    private class MetricStats
    {
        public float min;
        public float max;
        public float avg;
        public float median;
        public float p95;
        public float p99;
    }

    [System.Serializable]
    private class SessionReport
    {
        public System.DateTime timestamp;
        public float duration;
        public int sampleCount;
        public float targetFPS;

        public MetricStats fpsStats;
        public MetricStats drawCallStats;
        public MetricStats setPassStats;
        public MetricStats batchStats;
        public MetricStats triangleStats;
        public MetricStats vertexStats;
        public MetricStats frameTimeStats;
        public MetricStats memoryStats;

        public float avgDynamicBatched;
        public float avgStaticBatched;
        public float avgInstancedBatched;

        public bool meetsTargetFPS;
        public bool meetsDrawCallTarget;
        public bool meetsSetPassTarget;
        public bool meetsBatchTarget;
    }
}
#endif