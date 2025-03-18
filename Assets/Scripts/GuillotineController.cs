using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class GuillotineController : MonoBehaviour
{
    [Header("Component References")]
    [SerializeField] private Animator pressAnimator;
    [SerializeField] private Animator cutterAnimator;

    [Header("Timing Settings")]
    [SerializeField] private float sheetFeedTime = 2f;
    [SerializeField] private float pressingTime = 2f;
    [SerializeField] private float slicingTime = 5f;
    [SerializeField] private float unslicingTime = 3f;
    [SerializeField] private float unpressingTime = 2f;
    [SerializeField] private float conveyorRestartDelay = 3f;

    private List<GameObject> objectsInCollider = new List<GameObject>();
    private GuillotineState currentState = GuillotineState.Idle;
    private float stateTimer = 0f;
    private AssemblyLineController controller;

    private enum GuillotineState
    {
        Idle,
        FeedingSheet,
        Pressing,
        Slicing,
        Unslicing,
        Unpressing,
        WaitingToRestart
    }

    public void Initialize(AssemblyLineController controller)
    {
        this.controller = controller;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Sheet"))
        {
            objectsInCollider.Add(other.gameObject);
            controller.ReportMachineStatus("Guillotine", "SheetDetected");

            if (currentState == GuillotineState.Idle)
            {
                StartCuttingProcess();
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Sheet"))
        {
            objectsInCollider.Remove(other.gameObject);
            if (objectsInCollider.Count == 0)
            {
                controller.ReportMachineStatus("Guillotine", "Empty");
            }
        }
    }

    private void StartCuttingProcess()
    {
        SetState(GuillotineState.FeedingSheet, null, sheetFeedTime);
    }

    private void Update()
    {
        if (stateTimer > 0)
        {
            stateTimer -= Time.deltaTime;
            if (stateTimer <= 0)
            {
                ProcessNextState();
            }
        }
    }

    private void ProcessNextState()
    {
        switch (currentState)
        {
            case GuillotineState.FeedingSheet:
                controller.StopConveyor();
                SetState(GuillotineState.Pressing, "Pressing", pressingTime);
                break;
            case GuillotineState.Pressing:
                SetState(GuillotineState.Slicing, "Slice", slicingTime);
                break;
            case GuillotineState.Slicing:
                SetState(GuillotineState.Unslicing, "Unslice", unslicingTime);
                break;
            case GuillotineState.Unslicing:
                SetState(GuillotineState.Unpressing, "Unpressing", unpressingTime);
                break;
            case GuillotineState.Unpressing:
                SetState(GuillotineState.WaitingToRestart, null, conveyorRestartDelay);
                break;
            case GuillotineState.WaitingToRestart:
                ResetGuillotine();
                break;
        }
    }

    private void ResetGuillotine()
    {
        currentState = GuillotineState.Idle;
        stateTimer = 0f;

        pressAnimator.ResetTrigger("Pressing");
        pressAnimator.ResetTrigger("Unpressing");
        cutterAnimator.ResetTrigger("Slice");
        cutterAnimator.ResetTrigger("Unslice");

        StartCoroutine(RestartConveyor());
    }

    private void SetState(GuillotineState newState, string animatorTrigger, float delay)
    {
        currentState = newState;
        stateTimer = delay;

        if (animatorTrigger != null)
        {
            if (newState == GuillotineState.Pressing || newState == GuillotineState.Unpressing)
            {
                pressAnimator.SetTrigger(animatorTrigger);
            }
            else
            {
                cutterAnimator.SetTrigger(animatorTrigger);
            }
        }

        controller.ReportMachineStatus("Guillotine", newState.ToString());
    }

    private IEnumerator RestartConveyor()
    {
        yield return new WaitForSeconds(conveyorRestartDelay);

        controller.StartConveyor();

        if (objectsInCollider.Count > 0)
        {
            StartCuttingProcess();
        }
    }

    public bool IsReady()
    {
        return currentState == GuillotineState.Idle;
    }
}