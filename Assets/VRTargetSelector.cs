using UnityEngine;

public class VRTargetSelector : MonoBehaviour
{
    [Header("Navigation")]
    public AINavigationController navController;
    public VRPathVisualizer pathVisualizer; // Optional

    [Header("Target Selection")]
    [Tooltip("Enter target index (0, 1, 2, etc.) to navigate to that destination")]
    public int targetIndex = 1;
    [Tooltip("Names of targets for debug display")]
    public string[] targetNames = { "Elevator", "Exit", "Workshop", "Office" };

    [Header("Controls")]
    [Tooltip("Set to true to navigate to the target specified in Target Index")]
    public bool navigateToTarget = false;
    [Tooltip("Set to true to stop current navigation")]
    public bool cancelNavigation = false;
    [Tooltip("Set to true to go to closest target")]
    public bool goToClosest = false;

    private int lastTargetIndex = -1;
    private int currentTarget = -1;

    void Start()
    {
        if (navController != null)
        {
            navController.OnDestinationReached += OnDestinationReached;
        }

        ShowAvailableTargets();
    }

    void Update()
    {
        // Check for navigation request
        if (navigateToTarget)
        {
            navigateToTarget = false; // Reset the toggle
            SelectTarget(targetIndex);
        }

        // Check for cancel request
        if (cancelNavigation)
        {
            cancelNavigation = false; // Reset the toggle
            CancelNavigation();
        }

        // Check for closest target request
        if (goToClosest)
        {
            goToClosest = false; // Reset the toggle
            SelectClosestTarget();
        }
    }

    /// <summary>
    /// Navigate to specified target index
    /// </summary>
    public void SelectTarget(int index)
    {
        if (navController == null)
        {
            Debug.LogWarning("[VRTargetSelector] No navigation controller assigned!");
            return;
        }

        if (index < 0 || index >= navController.targetPositions.Length)
        {
            Debug.LogWarning($"[VRTargetSelector] Invalid target index: {index}. Valid range: 0-{navController.targetPositions.Length - 1}");
            return;
        }

        if (navController.targetPositions[index] == null)
        {
            Debug.LogWarning($"[VRTargetSelector] Target at index {index} is null!");
            return;
        }

        currentTarget = index;
        lastTargetIndex = index;

        // Set navigation destination
        navController.SetDestinationTo(index);

        // Update path visualization
        if (pathVisualizer != null)
        {
            pathVisualizer.SetTargetToVisualize(index);
            pathVisualizer.SetPathVisible(true);
        }

        string targetName = GetTargetName(index);
        float distance = navController.GetDistanceToTarget(index);

        Debug.Log($"[VRTargetSelector] Navigating to: {targetName} (Index: {index}, Distance: {distance:F1}m)");
    }

    /// <summary>
    /// Go to the closest available target
    /// </summary>
    public void SelectClosestTarget()
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
            targetIndex = closestTarget; // Update the inspector field
            SelectTarget(closestTarget);
        }
        else
        {
            Debug.Log("[VRTargetSelector] No valid targets found");
        }
    }

    /// <summary>
    /// Cancel current navigation
    /// </summary>
    public void CancelNavigation()
    {
        if (navController != null)
        {
            navController.Stop();
        }

        if (pathVisualizer != null)
        {
            pathVisualizer.SetPathVisible(false);
        }

        currentTarget = -1;
        targetIndex = -1; // Reset inspector field

        Debug.Log("[VRTargetSelector] Navigation cancelled");
    }

    string GetTargetName(int index)
    {
        if (index >= 0 && index < targetNames.Length)
        {
            return targetNames[index];
        }
        return $"Target {index}";
    }

    void ShowAvailableTargets()
    {
        Debug.Log("=== Available Navigation Targets ===");
        if (navController != null && navController.targetPositions != null)
        {
            for (int i = 0; i < navController.targetPositions.Length; i++)
            {
                if (navController.targetPositions[i] != null)
                {
                    string targetName = GetTargetName(i);
                    Debug.Log($"Index {i}: {targetName}");
                }
            }
        }
        Debug.Log("Set 'Target Index' and check 'Navigate To Target' to start navigation");
        Debug.Log("====================================");
    }

    void OnDestinationReached(int index)
    {
        string targetName = GetTargetName(index);
        Debug.Log($"[VRTargetSelector] ✅ Arrived at: {targetName} (Index: {index})");

        currentTarget = -1;
        targetIndex = -1; // Reset inspector field

        // Hide path visualization
        if (pathVisualizer != null)
        {
            pathVisualizer.SetPathVisible(false);
        }
    }

    /// <summary>
    /// Public method to set target from other scripts
    /// </summary>
    public void SetTargetAndNavigate(int index)
    {
        targetIndex = index;
        SelectTarget(index);
    }

    /// <summary>
    /// Get current navigation status
    /// </summary>
    public string GetNavigationStatus()
    {
        if (currentTarget >= 0)
        {
            float distance = navController.GetDistanceToTarget(currentTarget);
            return $"Going to: {GetTargetName(currentTarget)} | Distance: {distance:F1}m";
        }
        return "No active navigation";
    }

    void OnDestroy()
    {
        if (navController != null)
        {
            navController.OnDestinationReached -= OnDestinationReached;
        }
    }

    // Show current status in inspector
    void OnValidate()
    {
        if (Application.isPlaying && navController != null)
        {
            // Validate target index range
            if (targetIndex >= navController.targetPositions.Length)
            {
                targetIndex = navController.targetPositions.Length - 1;
            }
        }
    }
}