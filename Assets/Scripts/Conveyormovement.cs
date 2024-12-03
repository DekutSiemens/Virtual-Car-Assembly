using System.Collections.Generic;
using UnityEngine;

public class ConveyorBelt : MonoBehaviour
{
    [Header("Conveyor Settings")]
    public Material conveyorMaterial;
    public float speed = 1f;
    public float scrollSpeedX = 0.2f;
    public List<Rigidbody> objectsOnBelt = new List<Rigidbody>();

    [Header("Audio")]
    [SerializeField] private AudioSource conveyorAudioSource;
    [SerializeField] private float fadeTime = 0.5f;
    private float targetVolume;
    private float currentVolume;

    private bool isRunning = true;
    private Vector3 beltVelocity;
    private int materialScrollSpeedID;
    private AssemblyLineController controller;

    private void Awake()
    {
        beltVelocity = Vector3.forward * speed;
        materialScrollSpeedID = Shader.PropertyToID("_ScrollSpeedX");

        // Get or add AudioSource component
        if (conveyorAudioSource == null)
        {
            conveyorAudioSource = GetComponent<AudioSource>();
            if (conveyorAudioSource == null)
            {
                conveyorAudioSource = gameObject.AddComponent<AudioSource>();
            }
        }

        // Configure AudioSource
        conveyorAudioSource.loop = true;
        conveyorAudioSource.playOnAwake = false;
        conveyorAudioSource.volume = 0f;
    }

    public void Initialize(AssemblyLineController controller)
    {
        this.controller = controller;
        Collider beltCollider = GetComponent<Collider>();
        if (beltCollider != null)
        {
            beltCollider.isTrigger = true;
        }
        else
        {
            Debug.LogWarning("No Collider found on the ConveyorBelt. Please add a Collider component.");
        }
    }

    private void FixedUpdate()
    {
        if (isRunning)
        {
            foreach (Rigidbody rb in objectsOnBelt)
            {
                rb.linearVelocity = beltVelocity;
            }
        }
    }

    private void Update()
    {
        conveyorMaterial.SetFloat(materialScrollSpeedID, isRunning ? scrollSpeedX : 0f);

        // Handle audio fade
        if (currentVolume != targetVolume)
        {
            currentVolume = Mathf.MoveTowards(currentVolume, targetVolume, Time.deltaTime / fadeTime);
            conveyorAudioSource.volume = currentVolume;

            // Stop the audio if volume reaches 0
            if (currentVolume == 0f && !isRunning)
            {
                conveyorAudioSource.Stop();
            }
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Sheet"))
        {
            Rigidbody rb = other.attachedRigidbody;
            if (rb != null && !objectsOnBelt.Contains(rb))
            {
                objectsOnBelt.Add(rb);
                controller.ReportMachineStatus("ConveyorBelt", "SheetAdded");
            }
        }
    }

    public void RemoveObject(Rigidbody rb)
    {
        if (objectsOnBelt.Contains(rb))
        {
            objectsOnBelt.Remove(rb);
            if (objectsOnBelt.Count == 0)
            {
                controller.ReportMachineStatus("ConveyorBelt", "Empty");
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Sheet"))
        {
            Rigidbody rb = other.attachedRigidbody;
            if (rb != null)
            {
                RemoveObject(rb);
            }
        }
    }

    public void TurnOn()
    {
        isRunning = true;
        // Start audio
        if (!conveyorAudioSource.isPlaying)
        {
            conveyorAudioSource.Play();
        }
        targetVolume = 1f;
        controller.ReportMachineStatus("ConveyorBelt", "Running");
    }

    public void TurnOff()
    {
        isRunning = false;
        // Fade out audio
        targetVolume = 0f;

        foreach (Rigidbody rb in objectsOnBelt)
        {
            rb.linearVelocity = Vector3.zero;
        }
        controller.ReportMachineStatus("ConveyorBelt", "Stopped");
    }

    public float GetSpeed()
    {
        return speed;
    }

    public bool IsReady()
    {
        return isRunning && objectsOnBelt.Count < 5;
    }

    public Vector3 GetDirection()
    {
        return Vector3.right;
    }

    public void AddSheetMetal(GameObject sheetMetal)
    {
        if (sheetMetal.TryGetComponent<Rigidbody>(out Rigidbody rb))
        {
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.transform.position = transform.position + Vector3.up * 0.1f;
            objectsOnBelt.Add(rb);
            controller.ReportMachineStatus("ConveyorBelt", "SheetAdded");
        }
    }

    private void OnDisable()
    {
        // Make sure to stop audio when disabled
        if (conveyorAudioSource != null)
        {
            conveyorAudioSource.Stop();
            currentVolume = 0f;
            targetVolume = 0f;
        }
    }

    private void OnApplicationQuit()
    {
        conveyorMaterial.SetFloat(materialScrollSpeedID, 0f);
    }
}