using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class VRPathVisualizer : MonoBehaviour
{
    [Header("Path Visualization Settings")]
    [Tooltip("Reference to the AI Navigation Controller")]
    public AINavigationController navController;

    [Header("Visual Settings")]
    [Tooltip("Which target to visualize path to (-1 for current destination)")]
    public int targetToVisualize = -1;
    [Tooltip("Update path visualization every X seconds")]
    public float updateInterval = 0.5f;
    [Tooltip("Height offset for path above ground")]
    public float pathHeightOffset = 0.1f;

    [Header("Line Appearance")]
    [Tooltip("Width of the path line")]
    public float lineWidth = 0.05f;
    [Tooltip("Material for the path line")]
    public Material pathMaterial;

    [Header("Animation")]
    [Tooltip("Animate the path line")]
    public bool animatePath = true;
    [Tooltip("Animation speed for moving effects")]
    public float animationSpeed = 2f;

    private LineRenderer lineRenderer;
    private float lastUpdateTime;
    private Material animatedMaterial;

    void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();
        SetupLineRenderer();
    }

    void Start()
    {
        if (navController == null)
        {
            navController = GetComponent<AINavigationController>();
        }

        if (pathMaterial != null && animatePath)
        {
            // Create animated material instance
            animatedMaterial = new Material(pathMaterial);
            lineRenderer.material = animatedMaterial;
        }
    }

    void Update()
    {
        if (Time.time - lastUpdateTime >= updateInterval)
        {
            UpdatePathVisualization();
            lastUpdateTime = Time.time;
        }

        if (animatePath && animatedMaterial != null)
        {
            AnimatePath();
        }
    }

    void SetupLineRenderer()
    {
        lineRenderer.useWorldSpace = true;
        lineRenderer.startWidth = lineWidth;
        lineRenderer.endWidth = lineWidth;
        lineRenderer.positionCount = 0;
        lineRenderer.material = pathMaterial;

        // Optimize for VR performance
        lineRenderer.receiveShadows = false;
        lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lineRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
    }

    void UpdatePathVisualization()
    {
        if (navController == null) return;

        Vector3[] pathCorners = null;

        if (targetToVisualize >= 0)
        {
            // Show path to specific target
            pathCorners = navController.GetPathCornersToTarget(targetToVisualize);
        }
        else
        {
            // Show current agent path
            if (navController.GetComponent<UnityEngine.AI.NavMeshAgent>().hasPath)
            {
                pathCorners = navController.GetComponent<UnityEngine.AI.NavMeshAgent>().path.corners;
            }
        }

        if (pathCorners != null && pathCorners.Length > 1)
        {
            RenderPath(pathCorners);
        }
        else
        {
            // Clear line if no path
            lineRenderer.positionCount = 0;
        }
    }

    void RenderPath(Vector3[] corners)
    {
        // Add height offset to make path visible above ground
        Vector3[] adjustedCorners = new Vector3[corners.Length];
        for (int i = 0; i < corners.Length; i++)
        {
            adjustedCorners[i] = corners[i] + Vector3.up * pathHeightOffset;
        }

        lineRenderer.positionCount = adjustedCorners.Length;
        lineRenderer.SetPositions(adjustedCorners);
    }

    void AnimatePath()
    {
        if (animatedMaterial.HasProperty("_MainTex"))
        {
            // Animate texture offset for flowing effect
            float offset = Time.time * animationSpeed;
            animatedMaterial.SetTextureOffset("_MainTex", new Vector2(offset, 0));
        }
    }

    /// <summary>
    /// Set which target to visualize path to
    /// </summary>
    public void SetTargetToVisualize(int targetIndex)
    {
        targetToVisualize = targetIndex;
        UpdatePathVisualization();
    }

    /// <summary>
    /// Show path to closest target
    /// </summary>
    public void ShowPathToClosestTarget()
    {
        if (navController == null) return;

        var distances = navController.GetAllTargetDistances();
        float minDistance = float.MaxValue;
        int closestTarget = -1;

        foreach (var kvp in distances)
        {
            if (kvp.Value < minDistance && kvp.Value > 0)
            {
                minDistance = kvp.Value;
                closestTarget = kvp.Key;
            }
        }

        if (closestTarget >= 0)
        {
            SetTargetToVisualize(closestTarget);
        }
    }

    /// <summary>
    /// Toggle path visibility
    /// </summary>
    public void SetPathVisible(bool visible)
    {
        lineRenderer.enabled = visible;
    }

    void OnDestroy()
    {
        // Clean up animated material instance
        if (animatedMaterial != null)
        {
            DestroyImmediate(animatedMaterial);
        }
    }
}