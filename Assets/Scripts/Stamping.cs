using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class Stamping : MonoBehaviour
{
    [SerializeField] private Animator stampingAnimator;
    [SerializeField] private GameObject stampedSheetPrefab;
    [SerializeField] private Transform spawnTransform;
    [SerializeField] private int poolSize = 10;

    [Header("Timing Settings")]
    [SerializeField] private float stampingDuration = 4.5f;
    [SerializeField] private float preStampDelay;

    private Queue<GameObject> stampedSheetPool;
    private bool isProcessing = false;
    public bool IsSheetOnPress { get; private set; }
    public bool IsStampingComplete { get; private set; }
    private AssemblyLineController controller;
    private WaitForSeconds waitPreStampDelay;

    private void Awake()
    {
        preStampDelay = stampingDuration / 2f;
        waitPreStampDelay = new WaitForSeconds(preStampDelay);
        InitializeObjectPool();
    }

    public void Initialize(AssemblyLineController controller)
    {
        this.controller = controller;
    }

    private void InitializeObjectPool()
    {
        stampedSheetPool = new Queue<GameObject>(poolSize);
        for (int i = 0; i < poolSize; i++)
        {
            GameObject obj = Instantiate(stampedSheetPrefab);
            obj.transform.position = spawnTransform.position;
            obj.transform.rotation = spawnTransform.rotation;
            obj.SetActive(false);
            obj.tag = "StampedSheet";
            stampedSheetPool.Enqueue(obj);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!isProcessing && other.CompareTag("Sheet"))
        {
            isProcessing = true;
            IsSheetOnPress = true;
            IsStampingComplete = false;
            controller.ReportMachineStatus("Stamping", "SheetLoaded");
            StartCoroutine(ProcessStamping(other.gameObject));
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("StampedSheet") || other.CompareTag("Sheet"))
        {
            IsSheetOnPress = false;
            IsStampingComplete = false;
            controller.ReportMachineStatus("Stamping", "SheetUnloaded");

            // If a sheet exits while processing (shouldn't normally happen), clean up
            if (isProcessing)
            {
                StopAllCoroutines();
                isProcessing = false;
                controller.ReportMachineStatus("Stamping", "ProcessingInterrupted");
            }
        }
    }

    private IEnumerator ProcessStamping(GameObject unstampedSheet)
    {
        controller.ReportMachineStatus("Stamping", "StampingStarted");

        yield return waitPreStampDelay;

        stampingAnimator.SetTrigger("Stamp_down");

        yield return new WaitForSeconds(stampingDuration / 2f);

        SwapSheets(unstampedSheet);

        yield return new WaitForSeconds(stampingDuration / 4f);

        stampingAnimator.SetTrigger("Stamp_up");

        IsStampingComplete = true;
        controller.ReportMachineStatus("Stamping", "StampingCompleted");
        isProcessing = false;
    }

    private void SwapSheets(GameObject unstampedSheet)
    {
        if (stampedSheetPool.Count > 0)
        {
            // Get next stamped sheet from pool
            GameObject stampedSheet = stampedSheetPool.Dequeue();

            // Deactivate and pool the unstamped sheet
            unstampedSheet.SetActive(false);
            stampedSheetPool.Enqueue(unstampedSheet);

            // Position and activate the stamped sheet
            stampedSheet.transform.position = spawnTransform.position;
            stampedSheet.transform.rotation = spawnTransform.rotation;
            stampedSheet.SetActive(true);

            controller.IncrementProducedParts();
        }
        else
        {
            Debug.LogWarning("Stamped sheet pool is empty. Consider increasing pool size.");
        }
    }

    public bool IsReady()
    {
        return !isProcessing && !IsSheetOnPress;
    }

    private void OnDisable()
    {
        StopAllCoroutines();
        isProcessing = false;
        IsSheetOnPress = false;
        IsStampingComplete = false;
    }
}