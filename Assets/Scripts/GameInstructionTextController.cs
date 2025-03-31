using UnityEngine;
using TMPro; // Import TextMeshPro namespace

public class GameInstructionTextController : MonoBehaviour
{
    [SerializeField]
    private TextMeshProUGUI instructionText; // Reference to the TextMeshPro Text component

    [SerializeField]
    private string[] instructionElements; // Array to store all game instructions

    // Method to set instruction text by index
    public void SetInstruction(int index)
    {
        // Check if index is valid
        if (index >= 0 && index < instructionElements.Length)
        {
            instructionText.text = instructionElements[index];
        }
        else
        {
            Debug.LogWarning($"Invalid instruction index: {index}");
        }
    }

    // Optional method to clear the text
    public void ClearInstructions()
    {
        instructionText.text = "";
    }
}