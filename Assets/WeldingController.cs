using UnityEngine;

public class WeldingController : MonoBehaviour
{
    [System.Serializable]
    public class WeldingSequence
    {
        public string functionNameTrigger; // Name of the ABB function that triggers this sequence
        public bool tilt45;               // Should this sequence trigger Tilt45?
        public bool tiltNegative45;       // Should this sequence trigger Tilt-45?
        public bool resetTilts ;
    }

    [Header("References")]
    public ABBRobotController robotController;
    public Animator weldingAnimator;
    public float resumeDelay = 10.0f;

    [Header("Welding Sequences")]
    public WeldingSequence[] weldingSequences;

    private void Start()
    {
        if (robotController == null)
        {
            robotController = FindObjectOfType<ABBRobotController>();
        }

        // Subscribe to the ABB Robot Controller's events
        if (robotController != null)
        {
            robotController.OnFunctionComplete += HandleFunctionComplete;
        }
        else
        {
            Debug.LogError("No ABBRobotController found in the scene!");
        }

        if (weldingAnimator == null)
        {
            Debug.LogError("Please assign an Animator component for the welding animations!");
        }
    }

    private void HandleFunctionComplete(string functionName)
    {
        // Find if this function should trigger a welding sequence
        foreach (var sequence in weldingSequences)
        {
            if (sequence.functionNameTrigger == functionName)
            {
                StartWeldingSequence(sequence);
                break;
            }
        }
    }

    private void StartWeldingSequence(WeldingSequence sequence)
    {
        // Pause the robot automation
        robotController.pauseAutomation = true;

        // Set the animation bools
        if (sequence.tilt45)
        {
            weldingAnimator.SetBool("Tilt45", true);
        }
        if (sequence.tiltNegative45)
        {
            weldingAnimator.SetBool("Tilt-45", true);
        }
        if (sequence.resetTilts)
        {
            ResetWeldingTilts();
        }

        // Resume robot automation after a short delay
        Invoke("ResumeAutomation", resumeDelay);
    }

    private void ResumeAutomation()
    {
        robotController.pauseAutomation = false;
    }

    public void ResetWeldingTilts()
    {
        // Reset both tilt animations to false
        weldingAnimator.SetBool("Tilt45", false);
        weldingAnimator.SetBool("Tilt-45", false);
    }

    private void OnDestroy()
    {
        // Unsubscribe from events when the component is destroyed
        if (robotController != null)
        {
            robotController.OnFunctionComplete -= HandleFunctionComplete;
        }
    }
}