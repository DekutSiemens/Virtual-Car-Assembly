using System.Collections;
using UnityEngine;

/// <summary>
/// A micro-optimized door controller.
/// Caches WaitForSeconds and uses a state bool to avoid redundant SetBool calls.
/// Now also triggers a single AudioSource on open and close.
/// </summary>
public class AutomatedDoorController : MonoBehaviour
{
    [Header("Animator")]
    [SerializeField]
    private Animator doorAnimator;

    [SerializeField]
    private float closeDelay = 1.5f;

    [SerializeField]
    private string targetTag = "Player";

    // Animator parameter names
    private const string OPEN_PARAM = "Open";
    private const string CLOSE_PARAM = "Close";

    [Header("Audio")]
    [Tooltip("AudioSource on/near the door. Clip is assigned directly on this source.")]
    [SerializeField]
    private AudioSource doorAudioSource;

    // --- Optimization Variables ---
    private Coroutine closeCoroutine;
    private WaitForSeconds waitToClose; // Cached to avoid garbage allocation
    private bool isPlayerInside = false; // Tracks state to avoid redundant calls

    void Start()
    {
        // Cache the WaitForSeconds object on Start
        waitToClose = new WaitForSeconds(closeDelay);

        // Ensure door is closed
        if (doorAnimator != null)
        {
            doorAnimator.SetBool(OPEN_PARAM, false);
            doorAnimator.SetBool(CLOSE_PARAM, false);
        }

        // Ensure audio doesn't auto-play
        if (doorAudioSource != null)
        {
            doorAudioSource.playOnAwake = false;
        }
    }

    private void OnTriggerStay(Collider other)
    {
        if (!other.CompareTag(targetTag) || doorAnimator == null)
            return;

        // Stop the door from closing if it's in the process
        if (closeCoroutine != null)
        {
            StopCoroutine(closeCoroutine);
            closeCoroutine = null;
        }

        // Only update the animator if the state is changing
        if (!isPlayerInside)
        {
            doorAnimator.SetBool(OPEN_PARAM, true);
            doorAnimator.SetBool(CLOSE_PARAM, false);
            isPlayerInside = true;

            // Play sound when door starts opening
            PlayDoorSound();
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag(targetTag) || doorAnimator == null)
            return;

        // Player is officially "out"
        isPlayerInside = false;

        // Start the close routine
        closeCoroutine = StartCoroutine(CloseDoorAfterDelay());
    }

    private IEnumerator CloseDoorAfterDelay()
    {
        // Use the cached WaitForSeconds object
        yield return waitToClose;

        // If the player re-entered, OnTriggerStay would have
        // stopped this coroutine already.
        doorAnimator.SetBool(CLOSE_PARAM, true);
        doorAnimator.SetBool(OPEN_PARAM, false);

        // Play sound when door starts closing
        PlayDoorSound();

        closeCoroutine = null;
    }

    // ---------------- AUDIO ----------------

    private void PlayDoorSound()
    {
        if (doorAudioSource == null)
            return;

        // Restart the currently assigned clip on this source
        doorAudioSource.Stop();
        doorAudioSource.Play();
    }
}
