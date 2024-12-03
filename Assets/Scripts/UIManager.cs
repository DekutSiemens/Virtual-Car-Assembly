using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;


public class UIManager : MonoBehaviour
{
    public Canvas uiCanvas;
    public Dropdown machineDropdown;
    public Slider speedSlider;
    public TextMeshProUGUI statusText;
    public Toggle onOffToggle;

    private List<BaseMachineScript> currentMachines = new List<BaseMachineScript>();
    

    private void Awake()
    {
       
    }

    private void Start()
    {
        

        // Initially hide the UI
        if (uiCanvas != null)
        {
            uiCanvas.gameObject.SetActive(true);
           
        }
        else
        {
            Debug.LogError("UIManager: uiCanvas is not assigned in the Inspector");
        }

        // Check each UI element individually
        if (machineDropdown == null) Debug.LogError("UIManager: machineDropdown is not assigned in the Inspector");
        if (speedSlider == null) Debug.LogError("UIManager: speedSlider is not assigned in the Inspector");
        if (statusText == null) Debug.LogError("UIManager: statusText is not assigned in the Inspector");
        if (onOffToggle == null) Debug.LogError("UIManager: onOffToggle is not assigned in the Inspector");

        // Register event listeners
        if (machineDropdown != null) machineDropdown.onValueChanged.AddListener(OnMachineSelected);
        if (onOffToggle != null) onOffToggle.onValueChanged.AddListener(OnOffToggleChanged);
        if (speedSlider != null) speedSlider.onValueChanged.AddListener(OnSpeedChanged);

        // Initialize the UI with the first machine
        if (machineDropdown != null && machineDropdown.options.Count > 0)
        {
            OnMachineSelected(0); // Initialize with the first option
        }
        else
        {
            Debug.LogWarning("MachineDropdown has no options available.");
        }
    }

    private void Update()
    {
       
    }

   

    public void OnMachineSelected(int index)
    {
        Debug.Log($"OnMachineSelected called with index: {index}");

        if (index < 0 || index >= machineDropdown.options.Count)
        {
            Debug.LogError("Invalid dropdown index: " + index);
            return;
        }

        string selectedMachineTag = machineDropdown.options[index].text;
        Debug.Log($"Selected machine tag: {selectedMachineTag}");

        // Turn off all previously selected machines
        foreach (var machine in currentMachines)
        {
            machine.TurnOff();
            
            Debug.Log($"Turned off previous machine: {machine.name}");
        }

        // Clear the current machines list
        currentMachines.Clear();

        // Find all GameObjects with the selected tag
        GameObject[] machineObjects = GameObject.FindGameObjectsWithTag(selectedMachineTag);
        if (machineObjects.Length > 0)
        {
            foreach (var machineObject in machineObjects)
            {
                BaseMachineScript machine = machineObject.GetComponent<BaseMachineScript>();
                if (machine != null)
                {
                    currentMachines.Add(machine);
                    Debug.Log($"Found new machine object: {machineObject.name}");
                }
            }
            UpdateUI();
        }
        else
        {
            Debug.LogError($"No machine objects found with tag: {selectedMachineTag}");
        }
    }

    public void OnOffToggleChanged(bool isOn)
    {
        Debug.Log($"OnOffToggle changed: {isOn}");

        if (currentMachines.Count > 0)
        {
            foreach (var machine in currentMachines)
            {
                if (isOn)
                {
                    Debug.Log($"Turning on machine: {machine.name}");
                    machine.TurnOn();
                }
                else
                {
                    Debug.Log($"Turning off machine: {machine.name}");
                    machine.TurnOff();
                }
            }
            SetStatusText(isOn ? "Running" : "Stopped");
        }
        else
        {
            Debug.LogWarning("No machines selected to turn on/off.");
        }
    }

    public void OnSpeedChanged(float speed)
    {
        Debug.Log($"Speed changed: {speed}");

        if (currentMachines.Count > 0)
        {
            foreach (var machine in currentMachines)
            {
                
                Debug.Log($"Set speed of {machine.name} to {speed}");
            }
        }
        else
        {
            Debug.LogWarning("No machines selected to set speed.");
        }
    }

    public void UpdateUI()
    {
        if (currentMachines.Count > 0)
        {
            Debug.Log($"Updating UI for {currentMachines.Count} machines");
            // Set all machines to default off state and speed to zero
            foreach (var machine in currentMachines)
            {
                machine.TurnOff();
               
            }

            // Update the status text to reflect the current machine status
            SetStatusText("Stopped");
            // Update the speed slider to reflect the current machine speed (assuming all machines have the same speed)
            speedSlider.value = currentMachines[0].GetSpeed();
            // Set the toggle to off
            onOffToggle.isOn = false;
        }
        else
        {
            Debug.LogWarning("No machines selected to update UI.");
        }
    }
    private void SetStatusText(string text)
    {
        if (statusText != null)
        {
            statusText.text = text;
        }
        else
        {
            Debug.LogError("statusText is not assigned.");
        }
    }
}