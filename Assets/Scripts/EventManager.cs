using UnityEngine;
using System;

public class EventManager : MonoBehaviour
{
    public static EventManager Instance { get; private set; }

    // Main coordinating events
    public event Action OnSheetDetectedForPickup;
    public event Action OnSheetPickupComplete;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    // Methods to trigger events
    public void TriggerSheetDetectedForPickup()
    {
        OnSheetDetectedForPickup?.Invoke();
    }

    public void TriggerSheetPickupComplete()
    {
        OnSheetPickupComplete?.Invoke();
    }
}