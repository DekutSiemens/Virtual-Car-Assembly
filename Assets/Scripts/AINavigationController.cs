using System.Collections;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class AINavigationController : MonoBehaviour
{
    [Header("Target Configuration")]
    [Tooltip("Set target positions for the agent.")]
    public Transform[] targetPositions;

    [Header("Navigation Settings")]
    [Tooltip("Time in seconds before considering agent stuck")]
    public float stuckThreshold = 3f;
    [Tooltip("Minimum velocity to consider agent moving")]
    public float minMovementVelocity = 0.1f;

    [Header("Runtime NavMesh Baking")]
    [Tooltip("Enable automatic NavMesh rebaking")]
    public bool enableRuntimeBaking = false;
    [Tooltip("Time between NavMesh rebakes (seconds)")]
    public float bakingInterval = 2f;
    [Tooltip("NavMeshSurface component for runtime baking")]
    public NavMeshSurface navMeshSurface;

    [Header("Path Visualization")]
    [Tooltip("Enable path calculation and visualization")]
    public bool enablePathVisualization = false;
    [Tooltip("Calculate paths to all targets for visualization")]
    public bool calculateAllTargetPaths = false;

    [Header("Debug Settings")]
    public bool enableDebugLogs = true;
    public bool showGizmos = true;

    private NavMeshAgent agent;
    private int currentTargetIndex = -1;
    private float stuckTimer = 0f;
    private Vector3 lastPosition;
    private Coroutine bakingCoroutine;

    // Path data for visualization
    private Dictionary<int, NavMeshPath> calculatedPaths = new Dictionary<int, NavMeshPath>();
    private Dictionary<int, float> pathDistances = new Dictionary<int, float>();

    // Events for other systems to hook into
    public System.Action<int> OnDestinationSet;
    public System.Action<int> OnDestinationReached;
    public System.Action OnAgentStuck;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        lastPosition = transform.position;
    }

    void Start()
    {
        if (enableRuntimeBaking && navMeshSurface != null)
        {
            StartRuntimeBaking();
        }

        if (enablePathVisualization)
        {
            StartPathCalculation();
        }
    }

    void Update()
    {
        CheckIfStuck();
        CheckDestinationReached();
    }

    /// <summary>
    /// Call this method from other scripts to move the agent.
    /// </summary>
    /// <param name="index">Index of the target position</param>
    public void SetDestinationTo(int index)
    {
        if (!ValidateTargetIndex(index)) return;

        Vector3 target = targetPositions[index].position;

        // Check if path is valid before setting destination
        NavMeshPath path = new NavMeshPath();
        if (agent.CalculatePath(target, path))
        {
            if (path.status == NavMeshPathStatus.PathComplete)
            {
                agent.SetDestination(target);
                currentTargetIndex = index;
                stuckTimer = 0f;

                if (enableDebugLogs)
                {
                    float distance = Vector3.Distance(transform.position, target);
                    Debug.Log($"[AINav] Moving to target {index} - Distance: {distance:F2} units");
                }

                OnDestinationSet?.Invoke(index);
            }
            else
            {
                if (enableDebugLogs)
                    Debug.LogWarning($"[AINav] Path to target {index} is incomplete or invalid");
            }
        }
        else
        {
            if (enableDebugLogs)
                Debug.LogWarning($"[AINav] Cannot calculate path to target {index}");
        }
    }

    /// <summary>
    /// Set destination to a world position instead of using predefined targets
    /// </summary>
    public void SetDestinationToPosition(Vector3 worldPosition)
    {
        NavMeshPath path = new NavMeshPath();
        if (agent.CalculatePath(worldPosition, path) && path.status == NavMeshPathStatus.PathComplete)
        {
            agent.SetDestination(worldPosition);
            currentTargetIndex = -1; // No predefined target
            stuckTimer = 0f;

            if (enableDebugLogs)
            {
                float distance = Vector3.Distance(transform.position, worldPosition);
                Debug.Log($"[AINav] Moving to world position - Distance: {distance:F2} units");
            }
        }
    }

    /// <summary>
    /// Stop the agent and clear current destination
    /// </summary>
    public void Stop()
    {
        agent.ResetPath();
        currentTargetIndex = -1;
        if (enableDebugLogs)
            Debug.Log("[AINav] Agent stopped");
    }

    /// <summary>
    /// Get the current target index, -1 if no target set
    /// </summary>
    public int GetCurrentTargetIndex() => currentTargetIndex;

    /// <summary>
    /// Check if agent has reached its destination
    /// </summary>
    public bool HasReachedDestination()
    {
        return !agent.pathPending &&
               agent.remainingDistance < 0.5f &&
               (!agent.hasPath || agent.velocity.sqrMagnitude < 0.1f);
    }

    /// <summary>
    /// Get the calculated path to a specific target
    /// </summary>
    /// <param name="targetIndex">Index of the target</param>
    /// <returns>NavMeshPath to the target, null if not calculated or invalid</returns>
    public NavMeshPath GetPathToTarget(int targetIndex)
    {
        if (calculatedPaths.ContainsKey(targetIndex))
        {
            return calculatedPaths[targetIndex];
        }
        return null;
    }

    /// <summary>
    /// Get the path corners (waypoints) for a specific target
    /// </summary>
    /// <param name="targetIndex">Index of the target</param>
    /// <returns>Array of world positions representing the path</returns>
    public Vector3[] GetPathCornersToTarget(int targetIndex)
    {
        NavMeshPath path = GetPathToTarget(targetIndex);
        return path?.corners ?? new Vector3[0];
    }

    /// <summary>
    /// Get the calculated distance to a specific target via NavMesh
    /// </summary>
    /// <param name="targetIndex">Index of the target</param>
    /// <returns>Distance in world units, -1 if not calculated or invalid</returns>
    public float GetDistanceToTarget(int targetIndex)
    {
        if (pathDistances.ContainsKey(targetIndex))
        {
            return pathDistances[targetIndex];
        }
        return -1f;
    }

    /// <summary>
    /// Get all calculated target distances
    /// </summary>
    /// <returns>Dictionary with target index as key, distance as value</returns>
    public Dictionary<int, float> GetAllTargetDistances()
    {
        return new Dictionary<int, float>(pathDistances);
    }

    /// <summary>
    /// Force recalculation of paths to all targets
    /// </summary>
    public void RecalculateAllPaths()
    {
        if (targetPositions == null) return;

        calculatedPaths.Clear();
        pathDistances.Clear();

        for (int i = 0; i < targetPositions.Length; i++)
        {
            CalculatePathToTarget(i);
        }
    }

    /// <summary>
    /// Set the NavMesh baking interval
    /// </summary>
    public void SetBakingInterval(float interval)
    {
        bakingInterval = Mathf.Max(0.5f, interval); // Minimum 0.5 seconds

        if (bakingCoroutine != null)
        {
            StopCoroutine(bakingCoroutine);
            bakingCoroutine = StartCoroutine(RuntimeBakingCoroutine());
        }
    }

    /// <summary>
    /// Toggle runtime baking on/off
    /// </summary>
    public void SetRuntimeBaking(bool enabled)
    {
        enableRuntimeBaking = enabled;

        if (enabled && navMeshSurface != null)
        {
            StartRuntimeBaking();
        }
        else
        {
            StopRuntimeBaking();
        }
    }

    /// <summary>
    /// Get distance to current destination
    /// </summary>
    public float GetDistanceToDestination() => agent.remainingDistance;

    private void CalculatePathToTarget(int targetIndex)
    {
        if (!ValidateTargetIndex(targetIndex)) return;

        NavMeshPath path = new NavMeshPath();
        Vector3 targetPos = targetPositions[targetIndex].position;

        if (agent.CalculatePath(targetPos, path))
        {
            calculatedPaths[targetIndex] = path;

            // Calculate total distance along the path
            float totalDistance = 0f;
            Vector3[] corners = path.corners;

            if (corners.Length > 1)
            {
                for (int i = 0; i < corners.Length - 1; i++)
                {
                    totalDistance += Vector3.Distance(corners[i], corners[i + 1]);
                }
            }

            pathDistances[targetIndex] = totalDistance;

            if (enableDebugLogs)
            {
                Debug.Log($"[AINav] Calculated path to target {targetIndex}: {totalDistance:F2} units, {corners.Length} waypoints");
            }
        }
        else
        {
            // Remove invalid paths
            calculatedPaths.Remove(targetIndex);
            pathDistances.Remove(targetIndex);

            if (enableDebugLogs)
                Debug.LogWarning($"[AINav] Could not calculate path to target {targetIndex}");
        }
    }

    private void StartPathCalculation()
    {
        if (calculateAllTargetPaths)
        {
            RecalculateAllPaths();
        }
    }

    private void StartRuntimeBaking()
    {
        if (bakingCoroutine != null)
        {
            StopCoroutine(bakingCoroutine);
        }

        bakingCoroutine = StartCoroutine(RuntimeBakingCoroutine());

        if (enableDebugLogs)
            Debug.Log($"[AINav] Started runtime NavMesh baking every {bakingInterval} seconds");
    }

    private void StopRuntimeBaking()
    {
        if (bakingCoroutine != null)
        {
            StopCoroutine(bakingCoroutine);
            bakingCoroutine = null;
        }

        if (enableDebugLogs)
            Debug.Log("[AINav] Stopped runtime NavMesh baking");
    }

    private IEnumerator RuntimeBakingCoroutine()
    {
        while (enableRuntimeBaking && navMeshSurface != null)
        {
            yield return new WaitForSeconds(bakingInterval);

            if (enableDebugLogs)
                Debug.Log("[AINav] Rebuilding NavMesh...");

            navMeshSurface.BuildNavMesh();

            // Recalculate paths after baking if visualization is enabled
            if (enablePathVisualization && calculateAllTargetPaths)
            {
                yield return new WaitForEndOfFrame(); // Wait for NavMesh to fully update
                RecalculateAllPaths();
            }
        }
    }

    private bool ValidateTargetIndex(int index)
    {
        if (targetPositions == null || targetPositions.Length == 0)
        {
            if (enableDebugLogs)
                Debug.LogWarning("[AINav] Target positions not assigned.");
            return false;
        }

        if (index < 0 || index >= targetPositions.Length)
        {
            if (enableDebugLogs)
                Debug.LogWarning($"[AINav] Invalid target index: {index}");
            return false;
        }

        if (targetPositions[index] == null)
        {
            if (enableDebugLogs)
                Debug.LogWarning($"[AINav] Target at index {index} is null");
            return false;
        }

        return true;
    }

    private void CheckIfStuck()
    {
        if (agent.hasPath)
        {
            float distanceMoved = Vector3.Distance(transform.position, lastPosition);

            if (distanceMoved < minMovementVelocity * Time.deltaTime)
            {
                stuckTimer += Time.deltaTime;

                if (stuckTimer >= stuckThreshold)
                {
                    if (enableDebugLogs)
                        Debug.LogWarning("[AINav] Agent appears to be stuck!");

                    OnAgentStuck?.Invoke();
                    stuckTimer = 0f; // Reset to prevent spam
                }
            }
            else
            {
                stuckTimer = 0f; // Reset stuck timer if moving
            }
        }

        lastPosition = transform.position;
    }

    private void CheckDestinationReached()
    {
        if (currentTargetIndex >= 0 && HasReachedDestination())
        {
            if (enableDebugLogs)
                Debug.Log($"[AINav] Reached target {currentTargetIndex}");

            int reachedIndex = currentTargetIndex;
            currentTargetIndex = -1;
            OnDestinationReached?.Invoke(reachedIndex);
        }
    }

    void OnDestroy()
    {
        // Stop runtime baking
        if (bakingCoroutine != null)
        {
            StopCoroutine(bakingCoroutine);
        }
    }

    /// <summary>
    /// Visualize targets and paths in editor
    /// </summary>
    void OnDrawGizmosSelected()
    {
        if (!showGizmos || targetPositions == null) return;

        // Draw target positions
        Gizmos.color = Color.yellow;
        for (int i = 0; i < targetPositions.Length; i++)
        {
            if (targetPositions[i] != null)
            {
                Vector3 pos = targetPositions[i].position;
                Gizmos.DrawSphere(pos, 0.3f);

                // Draw index labels (visible in Scene view)
#if UNITY_EDITOR
                UnityEditor.Handles.Label(pos + Vector3.up * 0.5f, i.ToString());
#endif
            }
        }

        // Draw current path
        if (Application.isPlaying && agent != null && agent.hasPath)
        {
            Gizmos.color = Color.green;
            Vector3[] pathCorners = agent.path.corners;

            for (int i = 0; i < pathCorners.Length - 1; i++)
            {
                Gizmos.DrawLine(pathCorners[i], pathCorners[i + 1]);
            }
        }

        // Draw calculated paths to all targets (for visualization)
        if (Application.isPlaying && enablePathVisualization)
        {
            foreach (var kvp in calculatedPaths)
            {
                int targetIndex = kvp.Key;
                NavMeshPath path = kvp.Value;

                if (path != null && path.corners.Length > 1)
                {
                    // Different color for each path
                    Gizmos.color = Color.Lerp(Color.blue, Color.magenta, (float)targetIndex / targetPositions.Length);

                    for (int i = 0; i < path.corners.Length - 1; i++)
                    {
                        Gizmos.DrawLine(path.corners[i], path.corners[i + 1]);
                    }

                    // Draw distance label
#if UNITY_EDITOR
                    if (pathDistances.ContainsKey(targetIndex))
                    {
                        Vector3 labelPos = path.corners[path.corners.Length / 2] + Vector3.up * 1f;
                        UnityEditor.Handles.Label(labelPos, $"{pathDistances[targetIndex]:F1}m");
                    }
#endif
                }
            }
        }

        // Highlight current target
        if (Application.isPlaying && currentTargetIndex >= 0 && currentTargetIndex < targetPositions.Length)
        {
            if (targetPositions[currentTargetIndex] != null)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(targetPositions[currentTargetIndex].position, 0.5f);
            }
        }
    }
}