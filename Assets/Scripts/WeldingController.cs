using UnityEngine;
using System.Collections;

public class WeldingController : MonoBehaviour
{
    [System.Serializable]
    public class TiltWorktableSequence
    {
        public string functionNameTrigger;
        public bool tilt45;
        public bool tiltNegative45;
        public bool resetTilts;
        public bool positionerTurn180;
        public bool positionerTurnNegative180;
        public bool resetPositionerTurns;
    }

    [Header("References")]
    public ABBRobotController robotController;
    public Animator worktableAnimator;
    public Animator positionerAnimator;

    [Header("Timing Settings")]
    public float resumeDelay = 10.0f;         // Delay before resuming automation
    public float tiltingStartDelay = 2.0f;   // Delay before starting the tilting sequence

    [Header("Tilt Sequence")]
    public TiltWorktableSequence[] TiltingSequence;

    private void Start()
    {
        if (robotController == null)
        {
            robotController = FindObjectOfType<ABBRobotController>();
        }

        if (robotController != null)
        {
            // Subscribe to the OnFunctionStart event
            robotController.OnFunctionStart += HandleFunctionStart;
        }
        else
        {
            Debug.LogError("No ABBRobotController found in the scene!");
        }

        ValidateReferences();
    }

    private void ValidateReferences()
    {
        if (worktableAnimator == null)
        {
            Debug.LogError("Please assign an Animator component for the welding animations!");
        }
        if (positionerAnimator == null)
        {
            Debug.LogError("Please assign an Animator component for the worktable animations!");
        }
    }

    private void HandleFunctionStart(string functionName)
    {
        foreach (var sequence in TiltingSequence)
        {
            if (sequence.functionNameTrigger == functionName)
            {
                // Pause automation immediately when the function starts
                robotController.pauseAutomation = true;

                // Start the tilting sequence with a delay
                StartCoroutine(DelayedTiltingSequence(sequence));
                break;
            }
        }
    }

    private IEnumerator DelayedTiltingSequence(TiltWorktableSequence sequence)
    {
        // Wait for the specified delay before starting the tilting sequence
        yield return new WaitForSeconds(tiltingStartDelay);

        // Start the tilting and positioner animations
        StartTiltingSequence(sequence);

        // Schedule automation resume after the tilting sequence
        Invoke(nameof(ResumeAutomation), resumeDelay);
    }

    private void StartTiltingSequence(TiltWorktableSequence sequence)
    {
        // Tilt animations
        if (sequence.tilt45)
        {
            worktableAnimator.SetBool("Tilt45", true);
        }
        if (sequence.tiltNegative45)
        {
            worktableAnimator.SetBool("Tilt-45", true);
        }
        if (sequence.resetTilts)
        {
            ResetTableTilts();
        }

        // Worktable Positioner turn animations
        if (sequence.positionerTurn180)
        {
            positionerAnimator.SetBool("Turn180", true);
        }
        if (sequence.positionerTurnNegative180)
        {
            positionerAnimator.SetBool("Turn-180", true);
        }
        if (sequence.resetPositionerTurns)
        {
            ResetWorkTablePositionerTurns();
        }
    }

    private void ResumeAutomation()
    {
        robotController.pauseAutomation = false;
    }

    public void ResetTableTilts()
    {
        worktableAnimator.SetBool("Tilt45", false);
        worktableAnimator.SetBool("Tilt-45", false);
    }

    public void ResetWorkTablePositionerTurns()
    {
        positionerAnimator.SetBool("Turn180", false);
        positionerAnimator.SetBool("Turn-180", false);
    }

    private void OnDestroy()
    {
        if (robotController != null)
        {
            robotController.OnFunctionStart -= HandleFunctionStart;
        }
    }
}
