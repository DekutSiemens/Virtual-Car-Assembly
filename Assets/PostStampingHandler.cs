using UnityEngine;
using System.Collections;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

public class PostStampingHandler : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Animator pickAndPlaceAnimator;
    [SerializeField] private Animator endEffectorAnimator;
    [SerializeField] private XRSocketInteractor socketInteractor;

    [Header("Timing")]
    [SerializeField] private float delayBeforePick = 4f;
    [SerializeField] private float delayAfterPick = 2f;
    [SerializeField] private float delayAfterExtend = 2f;

    private bool isProcessing = false;
    public bool IsProcessing { get; private set; }
    private AssemblyLineController controller;
    private WaitForSeconds waitBeforePick;
    private WaitForSeconds waitAfterPick;
    private WaitForSeconds waitAfterExtend;

    private void Awake()
    {
        waitBeforePick = new WaitForSeconds(delayBeforePick);
        waitAfterPick = new WaitForSeconds(delayAfterPick);
        waitAfterExtend = new WaitForSeconds(delayAfterExtend);
    }

    public void Initialize(AssemblyLineController controller)
    {
        this.controller = controller;
        if (socketInteractor == null)
        {
            Debug.LogError("XRSocketInteractor reference is missing on PostStampingHandler!");
        }
    }

    public void StartPickAndPlace()
    {
        if (!IsProcessing)
        {
            IsProcessing = true;
            StartCoroutine(HandlePickAndPlace());
        }
    }

    private IEnumerator HandlePickAndPlace()
    {
        controller.ReportMachineStatus("PostStampingHandler", "PickingStampedPart");

        // Enable socket to grab the stamped part

        socketInteractor.socketActive = true;
        yield return waitBeforePick;
        pickAndPlaceAnimator.SetTrigger("Pick");
        yield return waitAfterPick;

        endEffectorAnimator.SetTrigger("Extend");
        yield return waitAfterExtend;


        pickAndPlaceAnimator.SetTrigger("Return");
        yield return new WaitForSeconds(2f);
        // Disable socket to release the part
        socketInteractor.socketActive = false;
        yield return new WaitForSeconds(2f); // Short delay to ensure part is released

        // Reset all triggers
        pickAndPlaceAnimator.ResetTrigger("Pick");
        pickAndPlaceAnimator.ResetTrigger("Return");
        endEffectorAnimator.ResetTrigger("Extend");

        // Re-enable socket for next cycle
        socketInteractor.socketActive = true;

        controller.ReportMachineStatus("PostStampingHandler", "CompletedPickAndPlace");
        IsProcessing = false;

        // Increment the produced parts counter and notify that processing is complete
        controller.IncrementProducedParts();
    }

    public bool IsReady()
    {
        return !isProcessing;
    }

    private void OnDisable()
    {
        StopAllCoroutines();
        IsProcessing = false;
        if (socketInteractor != null)
        {
            socketInteractor.socketActive = true;
        }
    }
}