using System.Collections;
using UnityEngine;

/// <summary>
/// A micro-optimized door controller.
/// Caches WaitForSeconds and uses a state bool to avoid redundant SetBool calls.
/// </summary>
public class AutomatedDoorController : MonoBehaviour
{
    [SerializeField]
    private Animator doorAnimator;
    [SerializeField]
    private float closeDelay = 1.5f;
    [SerializeField]
    private string targetTag = "Player";

    // Animator parameter names
    private const string OPEN_PARAM = "Open";
    private const string CLOSE_PARAM = "Close";

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
    }

    private void OnTriggerStay(Collider other)
    {
        if (other.CompareTag(targetTag) && doorAnimator != null)
        {
            // Stop the door from closing if it's in the process
            if (closeCoroutine != null)
            {
                StopCoroutine(closeCoroutine);
                closeCoroutine = null;
            }

            // --- Optimization ---
            // Only update the animator if the state is changing
            if (!isPlayerInside)
            {
                doorAnimator.SetBool(OPEN_PARAM, true);
                doorAnimator.SetBool(CLOSE_PARAM, false);
                isPlayerInside = true;
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag(targetTag) && doorAnimator != null)
        {
            // Player is officially "out"
            isPlayerInside = false;

            // Start the close routine
            closeCoroutine = StartCoroutine(CloseDoorAfterDelay());
        }
    }

    private IEnumerator CloseDoorAfterDelay()
    {
        // --- Optimization ---
        // Use the cached WaitForSeconds object
        yield return waitToClose;

        // Note: We don't need to check isPlayerInside here, because
        // if the player re-entered, OnTriggerStay would have
        // stopped this coroutine already.

        doorAnimator.SetBool(CLOSE_PARAM, true);
        doorAnimator.SetBool(OPEN_PARAM, false);

        closeCoroutine = null;
    }
}