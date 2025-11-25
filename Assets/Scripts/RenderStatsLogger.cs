#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

public class RenderStatsLogger : MonoBehaviour
{
    public float interval = 1f;

    void OnEnable() => InvokeRepeating(nameof(Log), interval, interval);
    void OnDisable() => CancelInvoke(nameof(Log));

    void Log()
    {
        Debug.Log(
            $"[RenderStats] Tris={UnityStats.triangles:N0}, " +
            $"Verts={UnityStats.vertices:N0}, " +
            $"Batches={UnityStats.batches:N0}, " +
            $"DrawCalls={UnityStats.drawCalls:N0}, " +
            $"SetPass={UnityStats.setPassCalls:N0}"
        );
    }
}
#endif
