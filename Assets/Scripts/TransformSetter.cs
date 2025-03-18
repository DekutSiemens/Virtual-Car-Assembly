using UnityEngine;

public class TransformSetter : MonoBehaviour
{
    // Public variable to set the transform input
    public Transform targetTransform;

    // Public function to set the transform of the object
    public void SetTransform()
    {
        if (targetTransform != null)
        {
            transform.position = targetTransform.position;
            transform.rotation = targetTransform.rotation;
            transform.localScale = targetTransform.localScale;
        }
        else
        {
            Debug.LogWarning("Target Transform is not set!");
        }
    }
}