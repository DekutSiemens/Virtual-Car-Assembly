using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;

public class ABBRobotController : MonoBehaviour
{
    [System.Serializable]
    public class RobotFunction
    {
        public string name;
        public List<Transform> points = new List<Transform>();
        public bool activateManually = false;
        public float pointDelay = 0f;
        public bool isLooping = false;
        public AnimationCurve speedCurve = AnimationCurve.Linear(0, 1, 1, 1);
        public bool useLocalSpace = false;
        [Tooltip("Enable for seamless movement through all points")]
        public bool seamlessMovement = false;
    }

    [Header("Robot Configuration")]
    public List<RobotFunction> robotFunctions = new List<RobotFunction>();
    public float baseSpeed = 5f;
    public float functionDelay = 2f;
    public bool pauseAutomation = false;
    [Tooltip("When enabled, the controller will restart from the first function after completing the last one")]
    public bool loopAllFunctions = true;
    [Tooltip("Delay before starting the next cycle when looping all functions")]
    public float cycleCooldown = 3f;

    [Header("Movement Settings")]
    public bool useRotation = true;
    public float rotationSpeed = 10f;
    public float positionThreshold = 0.01f;
    public float rotationThreshold = 0.1f;

    [Header("Transform Settings")]
    public bool useLocalSpace = false;
    public Transform robotBase;

    [Header("Audio Settings")]
    public float audioPlaybackSpeed = 1f;
    public float minPitch = 0.8f;
    public float maxPitch = 1.2f;
    public float pitchSmoothTime = 0.1f;

    private float currentPitch;
    private float pitchVelocity;

    private AudioSource audioSource;

    [Header("Debug Settings")]
    public bool logMovements = true;

    [Header("Debugging Tracking")]
    public int currentFunctionIndex = -1;
    public int currentPointIndex = -1;
    public bool wasRunningBeforePause = false;
    public int completedCycles = 0;

    private Coroutine currentMovementCoroutine;
    private Coroutine automationCoroutine;
    private bool isExecutingFunction = false;
    private RobotFunction currentFunction;

    public event Action<string> OnFunctionStart;
    public event Action<string> OnFunctionComplete;
    public event Action<string, Vector3> OnPointReached;
    public event Action OnCycleComplete;

    private void Start()
    {
        ValidateConfiguration();
        if (robotBase == null)
        {
            robotBase = transform;
        }
        audioSource = GetComponent<AudioSource>();
        if (audioSource != null)
        {
            audioSource.pitch = audioPlaybackSpeed;
            currentPitch = audioPlaybackSpeed;
        }
        ResetFunctionTracking();
        StartAutomation();
    }

    private void ResetFunctionTracking()
    {
        currentFunctionIndex = -1;
        currentPointIndex = -1;
        wasRunningBeforePause = false;
    }

    private void ValidateConfiguration()
    {
        foreach (var function in robotFunctions)
        {
            if (function.points.Count == 0)
            {
                Debug.LogWarning($"Function '{function.name}' has no points assigned.");
            }
            if (string.IsNullOrEmpty(function.name))
            {
                Debug.LogError("Found function with empty name!");
            }
            for (int i = 0; i < function.points.Count; i++)
            {
                if (function.points[i] == null)
                {
                    Debug.LogError($"Null point found in function '{function.name}' at index {i}");
                }
            }
        }
    }

    public void StartAutomation()
    {
        if (automationCoroutine != null)
        {
            StopCoroutine(automationCoroutine);
        }
        automationCoroutine = StartCoroutine(AutomateRobotFunctions());
    }

    public void StopAutomation()
    {
        if (automationCoroutine != null)
        {
            StopCoroutine(automationCoroutine);
            automationCoroutine = null;
        }
    }

    private IEnumerator AutomateRobotFunctions()
    {
        while (true)
        {
            if (!pauseAutomation)
            {
                if (wasRunningBeforePause)
                {
                    Debug.Log($"Resuming from Function {currentFunctionIndex}, Point {currentPointIndex}");
                    wasRunningBeforePause = false;

                    if (currentFunctionIndex >= 0 && currentFunctionIndex < robotFunctions.Count)
                    {
                        var function = robotFunctions[currentFunctionIndex];
                        ExecuteFunctionFromPoint(function, currentPointIndex + 1);
                    }
                }
                else
                {
                    // Check if we need to start from beginning or continue
                    int startingIndex = (currentFunctionIndex + 1);

                    // If we've completed all functions and need to loop
                    if (startingIndex >= robotFunctions.Count && loopAllFunctions)
                    {
                        // Log cycle completion
                        completedCycles++;
                        if (logMovements) Debug.Log($"Completed full automation cycle #{completedCycles}");

                        // Invoke cycle completion event
                        OnCycleComplete?.Invoke();

                        // Wait before restarting if cooldown is specified
                        if (cycleCooldown > 0)
                        {
                            yield return new WaitForSeconds(cycleCooldown);
                        }

                        // Reset to first function
                        startingIndex = 0;
                        currentFunctionIndex = -1;
                    }

                    // Execute functions
                    for (int i = startingIndex; i < robotFunctions.Count; i++)
                    {
                        var function = robotFunctions[i];

                        if (!function.activateManually && !isExecutingFunction)
                        {
                            yield return new WaitForSeconds(functionDelay);

                            if (!pauseAutomation)
                            {
                                currentFunctionIndex = i;
                                ExecuteFunction(function);
                                yield return new WaitUntil(() => !isExecutingFunction);
                            }
                        }
                    }
                }
            }
            yield return null;
        }
    }

    private void ExecuteFunctionFromPoint(RobotFunction function, int startPointIndex)
    {
        if (currentMovementCoroutine != null)
        {
            StopCoroutine(currentMovementCoroutine);
        }

        currentFunction = function;
        isExecutingFunction = true;

        if (function.seamlessMovement && function.points.Count > 1)
        {
            currentMovementCoroutine = StartCoroutine(MoveSeamlesslyFromIndex(function, startPointIndex));
        }
        else
        {
            currentMovementCoroutine = StartCoroutine(MoveToNextPointFromIndex(function, startPointIndex));
        }
    }

    private void ExecuteFunction(RobotFunction function)
    {
        if (currentMovementCoroutine != null)
        {
            StopCoroutine(currentMovementCoroutine);
        }

        currentFunction = function;
        isExecutingFunction = true;

        if (function.seamlessMovement && function.points.Count > 1)
        {
            currentMovementCoroutine = StartCoroutine(MoveSeamlessly(function));
        }
        else
        {
            currentMovementCoroutine = StartCoroutine(MoveToNextPoint(function));
        }
    }

    public void PauseCurrentFunction()
    {
        if (currentMovementCoroutine != null)
        {
            StopCoroutine(currentMovementCoroutine);
            currentMovementCoroutine = null;

            isExecutingFunction = false;
            wasRunningBeforePause = true;
            pauseAutomation = true;

            Debug.Log($"Function Paused - Function: {currentFunctionIndex}, Point: {currentPointIndex}");
        }
    }

    public void ResetAndStartFromBeginning()
    {
        // Stop current execution
        if (currentMovementCoroutine != null)
        {
            StopCoroutine(currentMovementCoroutine);
            currentMovementCoroutine = null;
        }

        // Reset tracking variables
        ResetFunctionTracking();
        isExecutingFunction = false;
        pauseAutomation = false;

        // Restart automation
        StartAutomation();

        if (logMovements) Debug.Log("Robot reset to start from the beginning");
    }

    private IEnumerator MoveToNextPoint(RobotFunction function)
    {
        if (logMovements) Debug.Log($"Starting function: {function.name}");
        OnFunctionStart?.Invoke(function.name);

        bool useLocal = function.useLocalSpace || useLocalSpace;

        do
        {
            for (int i = 0; i < function.points.Count; i++)
            {
                currentPointIndex = i;
                Transform targetPoint = function.points[i];
                if (targetPoint == null) continue;

                yield return MoveToPoint(targetPoint, useLocal, function.pointDelay);
            }
        } while (function.isLooping && !pauseAutomation);

        if (logMovements) Debug.Log($"Completed function: {function.name}");
        OnFunctionComplete?.Invoke(function.name);
        isExecutingFunction = false;
    }

    private IEnumerator MoveToNextPointFromIndex(RobotFunction function, int startIndex)
    {
        bool useLocal = function.useLocalSpace || useLocalSpace;

        do
        {
            for (int i = startIndex; i < function.points.Count; i++)
            {
                currentPointIndex = i;
                Transform targetPoint = function.points[i];
                if (targetPoint == null) continue;

                yield return MoveToPoint(targetPoint, useLocal, function.pointDelay);
            }
            startIndex = 0;
        } while (function.isLooping && !pauseAutomation);

        if (logMovements) Debug.Log($"Completed function: {function.name}");
        OnFunctionComplete?.Invoke(function.name);
        isExecutingFunction = false;
    }

    // New method for seamless movement through all points in the function
    private IEnumerator MoveSeamlessly(RobotFunction function)
    {
        if (logMovements) Debug.Log($"Starting seamless function: {function.name}");
        OnFunctionStart?.Invoke(function.name);

        bool useLocal = function.useLocalSpace || useLocalSpace;

        do
        {
            if (function.points.Count > 0)
            {
                yield return MoveSeamlesslyThroughPoints(function.points, useLocal);

                // If there's a point delay specified, wait after completing the full path
                if (function.pointDelay > 0)
                {
                    yield return new WaitForSeconds(function.pointDelay);
                }
            }
        } while (function.isLooping && !pauseAutomation);

        if (logMovements) Debug.Log($"Completed seamless function: {function.name}");
        OnFunctionComplete?.Invoke(function.name);
        isExecutingFunction = false;
    }

    // New method for seamless movement starting from a specific index
    private IEnumerator MoveSeamlesslyFromIndex(RobotFunction function, int startIndex)
    {
        bool useLocal = function.useLocalSpace || useLocalSpace;

        // Create a sublist of points starting from startIndex
        List<Transform> remainingPoints = function.points.GetRange(startIndex, function.points.Count - startIndex);

        do
        {
            if (remainingPoints.Count > 0)
            {
                yield return MoveSeamlesslyThroughPoints(remainingPoints, useLocal);

                // For subsequent loops, use all points
                remainingPoints = function.points;

                // If there's a point delay specified, wait after completing the full path
                if (function.pointDelay > 0)
                {
                    yield return new WaitForSeconds(function.pointDelay);
                }
            }
        } while (function.isLooping && !pauseAutomation);

        if (logMovements) Debug.Log($"Completed seamless function: {function.name}");
        OnFunctionComplete?.Invoke(function.name);
        isExecutingFunction = false;
    }

    // Core method that handles the actual seamless movement through a list of points
    private IEnumerator MoveSeamlesslyThroughPoints(List<Transform> points, bool useLocal)
    {
        if (points.Count == 0) yield break;

        // Play audio at the start of movement
        if (audioSource != null)
        {
            audioSource.loop = true;
            audioSource.pitch = audioPlaybackSpeed;
            audioSource.Play();
        }

        for (int i = 0; i < points.Count; i++)
        {
            currentPointIndex = i;
            Transform currentTarget = points[i];
            if (currentTarget == null) continue;

            Vector3 targetPosition = useLocal
                ? robotBase.TransformPoint(currentTarget.localPosition)
                : currentTarget.position;

            Quaternion targetRotation = useLocal
                ? robotBase.rotation * currentTarget.localRotation
                : currentTarget.rotation;

            // For the last point, we want to reach it exactly
            bool isLastPoint = (i == points.Count - 1);

            if (isLastPoint)
            {
                // For the last point, ensure we reach it exactly
                while (Vector3.Distance(transform.position, targetPosition) > positionThreshold ||
                      (useRotation && Quaternion.Angle(transform.rotation, targetRotation) > rotationThreshold))
                {
                    // Move toward final position
                    transform.position = Vector3.MoveTowards(transform.position, targetPosition, baseSpeed * Time.deltaTime);

                    // Rotate toward final rotation if needed
                    if (useRotation)
                    {
                        transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation,
                                                                     rotationSpeed * Time.deltaTime);
                    }

                    // Update audio pitch based on movement speed
                    UpdateAudioPitch();

                    yield return null;
                }

                // Ensure exact position and rotation
                transform.position = targetPosition;
                if (useRotation) transform.rotation = targetRotation;

                OnPointReached?.Invoke(currentFunction.name, targetPosition);
            }
            else
            {
                // For points in the middle of the path, move toward them but don't stop
                // Calculate the next position to determine how much to turn
                Transform nextTarget = points[i + 1];
                Vector3 nextPosition = useLocal
                    ? robotBase.TransformPoint(nextTarget.localPosition)
                    : nextTarget.position;

                // Move until we're close enough to start considering the next point
                while (Vector3.Distance(transform.position, targetPosition) > positionThreshold)
                {
                    // Move toward the current target
                    transform.position = Vector3.MoveTowards(transform.position, targetPosition, baseSpeed * Time.deltaTime);

                    // If using rotation, smoothly adjust heading toward the next point
                    if (useRotation)
                    {
                        // Calculate the look-ahead position - blend between current target and next target
                        float distanceToTarget = Vector3.Distance(transform.position, targetPosition);
                        float blendFactor = Mathf.Clamp01(1.0f - (distanceToTarget / baseSpeed));
                        Vector3 lookAheadPosition = Vector3.Lerp(targetPosition, nextPosition, blendFactor);

                        // Create rotation toward the look-ahead position
                        Quaternion lookRotation = Quaternion.LookRotation(lookAheadPosition - transform.position);
                        transform.rotation = Quaternion.RotateTowards(transform.rotation, lookRotation,
                                                                     rotationSpeed * Time.deltaTime);
                    }

                    // Update audio pitch based on movement speed
                    UpdateAudioPitch();

                    yield return null;
                }

                OnPointReached?.Invoke(currentFunction.name, targetPosition);
            }
        }

        // Stop audio when the movement is complete
        if (audioSource != null)
        {
            audioSource.Stop();
        }
    }

    // Helper method to update audio pitch based on movement speed
    private void UpdateAudioPitch()
    {
        if (audioSource != null && audioSource.isPlaying)
        {
            float speedFactor = baseSpeed / 5f; // Normalize speed to a factor
            float targetPitch = Mathf.Clamp(audioPlaybackSpeed * speedFactor, minPitch, maxPitch);

            // Smooth the pitch transition
            currentPitch = Mathf.SmoothDamp(currentPitch, targetPitch, ref pitchVelocity, pitchSmoothTime);
            audioSource.pitch = currentPitch;
        }
    }

    public void SetAudioSpeed(float speed)
    {
        audioPlaybackSpeed = Mathf.Clamp(speed, 0.1f, 3f);
        if (audioSource != null)
        {
            audioSource.pitch = audioPlaybackSpeed;
        }
    }

    private Vector3 GetCurrentPosition(bool useLocal)
    {
        if (useLocal)
        {
            return robotBase.InverseTransformPoint(transform.position);
        }
        return transform.position;
    }

    // Original method for non-seamless movement to a single point
    private IEnumerator MoveToPoint(Transform targetPoint, bool useLocal, float delay)
    {
        Vector3 targetPosition = useLocal ? robotBase.TransformPoint(targetPoint.localPosition) : targetPoint.position;
        Quaternion targetRotation = useLocal ? robotBase.rotation * targetPoint.localRotation : targetPoint.rotation;

        // Play audio at the start of movement
        if (audioSource != null)
        {
            audioSource.loop = true;
            audioSource.pitch = audioPlaybackSpeed;
            audioSource.Play();
        }

        while (true)
        {
            float distance = Vector3.Distance(transform.position, targetPosition);
            if (distance <= positionThreshold &&
                (!useRotation || Quaternion.Angle(transform.rotation, targetRotation) <= rotationThreshold))
            {
                break;
            }

            transform.position = Vector3.MoveTowards(transform.position, targetPosition, baseSpeed * Time.deltaTime);

            // Update audio pitch based on movement speed
            UpdateAudioPitch();

            if (useRotation)
            {
                transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
            }

            yield return null;
        }

        // Stop audio when the movement is complete
        if (audioSource != null)
        {
            audioSource.Stop();
        }

        OnPointReached?.Invoke(currentFunction.name, targetPoint.position);
        yield return new WaitForSeconds(delay);
    }
}