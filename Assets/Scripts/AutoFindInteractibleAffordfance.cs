using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using UnityEngine.XR.Interaction.Toolkit.AffordanceSystem.State;

public class AutoFindInteractibleAffordfance : MonoBehaviour
{
    private void Awake()
    {
        GetComponent<XRInteractableAffordanceStateProvider>().interactableSource = GetComponentInParent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable>();
    }
}
