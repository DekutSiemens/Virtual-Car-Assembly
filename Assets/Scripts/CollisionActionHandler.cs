using UnityEngine;
using UnityEngine.Events;
using System;

[AddComponentMenu("Custom/Collision Action Handler")]
public class CollisionActionHandler : MonoBehaviour
{
    // Enum to define collision detection types
    public enum CollisionDetectionType
    {
        OnCollisionEnter,
        OnCollisionExit,
        OnCollisionStay,
        OnTriggerEnter,
        OnTriggerExit,
        OnTriggerStay
    }

    [Tooltip("Choose which collision detection method to use")]
    [SerializeField] private CollisionDetectionType detectionType = CollisionDetectionType.OnTriggerEnter;

    [Tooltip("Optional tag filter - leave empty to detect all collisions")]
    [SerializeField] private string tagFilter = "";

    [Tooltip("Optional layer mask filter")]
    [SerializeField] private LayerMask layerFilter = -1; // -1 = everything

    [Header("Callbacks")]
    [SerializeField] private UnityEvent<Collider> onTriggerAction;
    [SerializeField] private UnityEvent<Collision> onCollisionAction;

    [Header("Debug")]
    [SerializeField] private bool showDebugMessages = true;

    // Cache components
    private Collider attachedCollider;

    private void Awake()
    {
        // Check if this GameObject has a collider
        attachedCollider = GetComponent<Collider>();

        if (attachedCollider == null)
        {
            Debug.LogError($"[CollisionActionHandler] No Collider found on {gameObject.name}. Please add a Collider component!", this);
            enabled = false;
            return;
        }

        // Check if using trigger methods without a trigger
        if ((detectionType == CollisionDetectionType.OnTriggerEnter ||
             detectionType == CollisionDetectionType.OnTriggerExit ||
             detectionType == CollisionDetectionType.OnTriggerStay) &&
            !attachedCollider.isTrigger)
        {
            Debug.LogWarning($"[CollisionActionHandler] {gameObject.name} is set to use trigger detection, but the Collider is not set as a trigger!", this);
        }

        // Check if using collision methods with a trigger
        if ((detectionType == CollisionDetectionType.OnCollisionEnter ||
             detectionType == CollisionDetectionType.OnCollisionExit ||
             detectionType == CollisionDetectionType.OnCollisionStay) &&
            attachedCollider.isTrigger)
        {
            Debug.LogWarning($"[CollisionActionHandler] {gameObject.name} is set to use collision detection, but the Collider is set as a trigger!", this);
        }
    }

    #region Collision Methods
    private void OnCollisionEnter(Collision collision)
    {
        if (detectionType != CollisionDetectionType.OnCollisionEnter) return;

        if (PassesFilters(collision.gameObject))
        {
            LogDebug($"OnCollisionEnter with {collision.gameObject.name}");
            onCollisionAction?.Invoke(collision);
        }
    }

    private void OnCollisionExit(Collision collision)
    {
        if (detectionType != CollisionDetectionType.OnCollisionExit) return;

        if (PassesFilters(collision.gameObject))
        {
            LogDebug($"OnCollisionExit with {collision.gameObject.name}");
            onCollisionAction?.Invoke(collision);
        }
    }

    private void OnCollisionStay(Collision collision)
    {
        if (detectionType != CollisionDetectionType.OnCollisionStay) return;

        if (PassesFilters(collision.gameObject))
        {
            LogDebug($"OnCollisionStay with {collision.gameObject.name}");
            onCollisionAction?.Invoke(collision);
        }
    }
    #endregion

    #region Trigger Methods
    private void OnTriggerEnter(Collider other)
    {
        if (detectionType != CollisionDetectionType.OnTriggerEnter) return;

        if (PassesFilters(other.gameObject))
        {
            LogDebug($"OnTriggerEnter with {other.gameObject.name}");
            onTriggerAction?.Invoke(other);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (detectionType != CollisionDetectionType.OnTriggerExit) return;

        if (PassesFilters(other.gameObject))
        {
            LogDebug($"OnTriggerExit with {other.gameObject.name}");
            onTriggerAction?.Invoke(other);
        }
    }

    private void OnTriggerStay(Collider other)
    {
        if (detectionType != CollisionDetectionType.OnTriggerStay) return;

        if (PassesFilters(other.gameObject))
        {
            LogDebug($"OnTriggerStay with {other.gameObject.name}");
            onTriggerAction?.Invoke(other);
        }
    }
    #endregion

    #region Helper Methods
    private bool PassesFilters(GameObject obj)
    {
        // Check tag filter if specified
        if (!string.IsNullOrEmpty(tagFilter) && !obj.CompareTag(tagFilter))
        {
            return false;
        }

        // Check layer filter
        if (layerFilter != -1 && ((1 << obj.layer) & layerFilter) == 0)
        {
            return false;
        }

        return true;
    }

    private void LogDebug(string message)
    {
        if (showDebugMessages)
        {
            Debug.Log($"[CollisionActionHandler] {message}", this);
        }
    }
    #endregion

    // Inspector validation
    private void OnValidate()
    {
        // Auto-check if the collision type matches with the correct event
        if (detectionType == CollisionDetectionType.OnTriggerEnter ||
            detectionType == CollisionDetectionType.OnTriggerExit ||
            detectionType == CollisionDetectionType.OnTriggerStay)
        {
            // No need to show warning in Inspector as we already show it at runtime
        }
    }
}