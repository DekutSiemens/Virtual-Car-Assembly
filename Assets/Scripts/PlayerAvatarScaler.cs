using UnityEngine;

public class PlayerAvatarScaler : MonoBehaviour
{
    [Header("Scaling Parameters")]
    [Tooltip("Reference height for scaling (in meters)")]
    public float referenceHeight = 1.8f;

    [Tooltip("Minimum scale limit")]
    public float minScaleFactor = 0.5f;

    [Tooltip("Maximum scale limit")]
    public float maxScaleFactor = 2.0f;

    [Header("Components")]
    [Tooltip("Reference to the character's main transform")]
    public Transform characterTransform;

    [Tooltip("Optional: Animator for adjusting animations")]
    public Animator characterAnimator;

    private Vector3 originalScale;

    void Start()
    {
        // Store the original scale if not set manually
        if (characterTransform == null)
            characterTransform = transform;

        originalScale = characterTransform.localScale;
    }

    /// <summary>
    /// Scale the avatar based on player height
    /// </summary>
    /// <param name="playerHeight">Player's actual height in meters</param>
    public void ScaleAvatarToHeight(float playerHeight)
    {
        // Calculate scaling factor
        float scaleFactor = playerHeight / referenceHeight;

        // Clamp the scale factor
        scaleFactor = Mathf.Clamp(scaleFactor, minScaleFactor, maxScaleFactor);

        // Apply uniform scaling
        Vector3 newScale = originalScale * scaleFactor;
        characterTransform.localScale = newScale;

        // Optionally adjust animator parameters
        if (characterAnimator != null)
        {
            characterAnimator.SetFloat("HeightScale", scaleFactor);
        }
    }

    /// <summary>
    /// Reset avatar to original scale
    /// </summary>
    public void ResetScale()
    {
        characterTransform.localScale = originalScale;

        if (characterAnimator != null)
        {
            characterAnimator.SetFloat("HeightScale", 1f);
        }
    }
}