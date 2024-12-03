using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class AssemblyLineController : MonoBehaviour
{
    [Header("Machine References")]
    [SerializeField] private ConveyorBelt inputConveyor;
    [SerializeField] private GuillotineController guillotineCutter;
    [SerializeField] private LineController lineController;
    [SerializeField] private Stamping stampingPress;
    [SerializeField] private PostStampingHandler postStampingHandler;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = true;

    private Queue<GameObject> sheetMetalQueue = new Queue<GameObject>();
    private HashSet<ConveyorBelt> initializedConveyors = new HashSet<ConveyorBelt>();

    private enum AssemblyLineState
    {
        Idle,
        FeedingSheetMetal,
        CuttingSheetMetal,
        WaitingForStampingPress,
        TransferringToPress,
        Stamping,
        PickingStampedPart,
        FinishingProduct
    }

    private AssemblyLineState currentState = AssemblyLineState.Idle;
    private int producedParts = 0;
    private float totalOperationTime = 0f;

    private void Start()
    {
        InitializeMachines();
        StartCoroutine(MainUpdateLoop());
    }

    public void RegisterConveyor(ConveyorBelt conveyor)
    {
        if (conveyor != null && !initializedConveyors.Contains(conveyor))
        {
            conveyor.Initialize(this);
            initializedConveyors.Add(conveyor);
            if (showDebugLogs)
            {
                Debug.Log($"Registered and initialized conveyor: {conveyor.gameObject.name}");
            }
        }
    }

    private void InitializeMachines()
    {
        // Initialize the main input conveyor
        RegisterConveyor(inputConveyor);

        // Initialize other machines
        if (lineController != null)
        {
            lineController.Initialize(this);
        }
        if (guillotineCutter != null)
        {
            guillotineCutter.Initialize(this);
        }
        if (stampingPress != null)
        {
            stampingPress.Initialize(this);
        }
        if (postStampingHandler != null)
        {
            postStampingHandler.Initialize(this);
        }
    }

    private IEnumerator MainUpdateLoop()
    {
        while (true)
        {
            UpdateMachineStatuses();
            ProcessQueue();
            UpdateState();
            totalOperationTime += Time.deltaTime;

            yield return new WaitForSeconds(0.1f);
        }
    }

    private void UpdateMachineStatuses()
    {
        // This method can be expanded to check and update the status of each machine
    }

    private void ProcessQueue()
    {
        if (sheetMetalQueue.Count > 0 && inputConveyor.IsReady() && currentState == AssemblyLineState.Idle)
        {
            GameObject sheet = sheetMetalQueue.Dequeue();
            inputConveyor.AddSheetMetal(sheet);
            currentState = AssemblyLineState.FeedingSheetMetal;
        }
    }

    private void UpdateState()
    {
        switch (currentState)
        {
            case AssemblyLineState.WaitingForStampingPress:
                if (!stampingPress.IsSheetOnPress && !postStampingHandler.IsProcessing)
                {
                    currentState = AssemblyLineState.TransferringToPress;
                    lineController.StartProcessingSheet();
                }
                break;
            case AssemblyLineState.TransferringToPress:
                if (stampingPress.IsSheetOnPress)
                {
                    currentState = AssemblyLineState.Stamping;
                }
                break;
            case AssemblyLineState.Stamping:
                if (stampingPress.IsStampingComplete)
                {
                    currentState = AssemblyLineState.PickingStampedPart;
                    postStampingHandler.StartPickAndPlace();
                }
                break;
            case AssemblyLineState.PickingStampedPart:
                if (postStampingHandler.IsReady())
                {
                    currentState = AssemblyLineState.FinishingProduct;
                    lineController.RestartOutputConveyor(); // Add this line to restart the conveyor
                }
                break;
            case AssemblyLineState.FinishingProduct:
                currentState = AssemblyLineState.Idle;
                break;
        }
    }

    public void SetStateToWaitingForStampingPress()
    {
        currentState = AssemblyLineState.WaitingForStampingPress;
    }


    public void ReportMachineStatus(string machineName, string status)
    {
        Debug.Log($"{machineName} status: {status}");
        // Update internal state based on machine status
    }

    public void StopConveyor()
    {
        inputConveyor.TurnOff();
    }

    public void StartConveyor()
    {
        inputConveyor.TurnOn();
    }

    public void IncrementProducedParts()
    {
        producedParts++;
        Debug.Log($"Total parts produced: {producedParts}");
    }

    public void AddSheetMetal(GameObject sheetMetal)
    {
        sheetMetalQueue.Enqueue(sheetMetal);
    }

    public float GetProductionRate()
    {
        return totalOperationTime > 0 ? producedParts / (totalOperationTime / 60f) : 0f;
    }
}