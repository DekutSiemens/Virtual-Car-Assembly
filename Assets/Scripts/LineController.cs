using UnityEngine;
using System.Collections;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

public class LineController : MonoBehaviour
{
    [SerializeField] private ConveyorBelt outputConveyor;
    [SerializeField] private Animator endEffectorAnimator;
    [SerializeField] private Animator pickAndPlaceAnimator;
    [SerializeField] private XRSocketInteractor socketInteractor;
    [SerializeField] private Stamping stampingPress;

    [Header("Timing Configuration")]
    [SerializeField] private float pickAnimationDuration = 4f;
    [SerializeField] private float feedAnimationDuration = 2f;

    private bool isProcessing = false;
    private AssemblyLineController controller;
    private Rigidbody currentSheetRigidbody;
    private bool hasSheetWaiting = false;

    public void Initialize(AssemblyLineController controller)
    {
        this.controller = controller;
        if (!ValidateComponents())
        {
            Debug.LogError($"Initialization failed for LineController on {gameObject.name}");
            return;
        }

        if (controller != null)
        {
            controller.RegisterConveyor(outputConveyor);
        }
        else
        {
            Debug.LogError("Assembly Line Controller reference is null during initialization!");
        }
    }

    private bool ValidateComponents()
    {
        bool isValid = true;

        if (outputConveyor == null)
        {
            Debug.LogError($"Output Conveyor reference missing on {gameObject.name}");
            isValid = false;
        }

        if (endEffectorAnimator == null)
        {
            Debug.LogError($"End Effector Animator reference missing on {gameObject.name}");
            isValid = false;
        }

        if (pickAndPlaceAnimator == null)
        {
            Debug.LogError($"Pick and Place Animator reference missing on {gameObject.name}");
            isValid = false;
        }

        if (socketInteractor == null)
        {
            Debug.LogError($"Socket Interactor reference missing on {gameObject.name}");
            isValid = false;
        }

        if (stampingPress == null)
        {
            Debug.LogError($"Stamping Press reference missing on {gameObject.name}");
            isValid = false;
        }

        return isValid;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Sheet"))
        {
            outputConveyor.TurnOff(); // Always stop the conveyor

            if (!hasSheetWaiting && !isProcessing)
            {
                currentSheetRigidbody = other.attachedRigidbody;
                hasSheetWaiting = true;
                controller.SetStateToWaitingForStampingPress();
            }
        }
    }

    public void StartProcessingSheet()
    {
        if (hasSheetWaiting && !isProcessing && currentSheetRigidbody != null)
        {
            StartCoroutine(ProcessSheet());
        }
    }

    private IEnumerator ProcessSheet()
    {
        isProcessing = true;
        controller.ReportMachineStatus("LineController", "StartingSheetProcess");

        // Start pick animation
        endEffectorAnimator.SetTrigger("Pick");
        yield return new WaitForSeconds(pickAnimationDuration);

        // Remove sheet from conveyor's tracking
        if (currentSheetRigidbody != null)
        {
            outputConveyor.RemoveObject(currentSheetRigidbody);
        }

        // Feed to stamping press
        pickAndPlaceAnimator.SetTrigger("Feed");
        yield return new WaitForSeconds(feedAnimationDuration);

        // Release sheet
        socketInteractor.socketActive = false;
        yield return new WaitForSeconds(0.5f);

        // Return to home position
        pickAndPlaceAnimator.SetTrigger("Unfeed");
        yield return new WaitForSeconds(0.5f);

        // Re-enable socket
        socketInteractor.socketActive = true;

        currentSheetRigidbody = null;
        hasSheetWaiting = false;
        isProcessing = false;
        controller.ReportMachineStatus("LineController", "SheetProcessComplete");
    }
    public void RestartOutputConveyor()
    {
        if (outputConveyor != null)
        {
            outputConveyor.TurnOn();
            controller.ReportMachineStatus("LineController", "RestartedOutputConveyor");
        }
    }

    public bool IsReady()
    {
        return !isProcessing;
    }

    private void OnDisable()
    {
        StopAllCoroutines();
        isProcessing = false;
        hasSheetWaiting = false;
        if (currentSheetRigidbody != null)
        {
            currentSheetRigidbody = null;
        }
        if (socketInteractor != null)
        {
            socketInteractor.socketActive = true;
        }
    }
}