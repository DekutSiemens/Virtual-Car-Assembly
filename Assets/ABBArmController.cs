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
    }

    [Header("Robot Configuration")]
    public List<RobotFunction> robotFunctions = new List<RobotFunction>();
    public float baseSpeed = 5f;
    public float functionDelay = 2f;
    public bool pauseAutomation = false;

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
    public float minPitch = 0.8f;         // Minimum pitch even at slowest speed
    public float maxPitch = 1.2f;         // Maximum pitch even at highest speed
    public float pitchSmoothTime = 0.1f;  // How quickly pitch changes

    private float currentPitch;
    private float pitchVelocity;  // Used for SmoothDamp

    private AudioSource audioSource;

    [Header("Debug Settings")]
    public bool logMovements = true;

    [Header("Debugging Tracking")]
    public int currentFunctionIndex = -1;
    public int currentPointIndex = -1;
    public bool wasRunningBeforePause = false;

    private Coroutine currentMovementCoroutine;
    private Coroutine automationCoroutine;
    private bool isExecutingFunction = false;
    private RobotFunction currentFunction;

    public event Action<string> OnFunctionStart;
    public event Action<string> OnFunctionComplete;
    public event Action<string, Vector3> OnPointReached;

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
            audioSource.pitch = audioPlaybackSpeed;  // Set initial pitch
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
                        ExecuteFunctionFromPoint(function, currentPointIndex + 1); // Resume from the next point
                    }
                }
                else
                {
                    for (int i = (currentFunctionIndex + 1); i < robotFunctions.Count; i++)
                    {
                        var function = robotFunctions[i];

                        if (!function.activateManually && !isExecutingFunction)
                        {
                            yield return new WaitForSeconds(functionDelay);

                            if (!pauseAutomation)
                            {
                                currentFunctionIndex = i; // Properly track the current function
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
        currentMovementCoroutine = StartCoroutine(MoveToNextPointFromIndex(function, startPointIndex));
    }

    private void ExecuteFunction(RobotFunction function)
    {
        if (currentMovementCoroutine != null)
        {
            StopCoroutine(currentMovementCoroutine);
        }
        currentFunction = function;
        isExecutingFunction = true;
        currentMovementCoroutine = StartCoroutine(MoveToNextPoint(function));
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
        currentFunctionIndex++;
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
            startIndex = 0; // Reset startIndex for loops
        } while (function.isLooping && !pauseAutomation);

        if (logMovements) Debug.Log($"Completed function: {function.name}");
        OnFunctionComplete?.Invoke(function.name);
        isExecutingFunction = false;
        currentFunctionIndex++;
    }

    public void SetAudioSpeed(float speed)
    {
        audioPlaybackSpeed = Mathf.Clamp(speed, 0.1f, 3f);  // Clamp between 0.1x and 3x speed
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

            // Adjust audio pitch based on movement speed
            if (audioSource != null)
            {
                float speedFactor = baseSpeed / 5f; // Normalize speed to a factor (adjust 5f as needed)
                audioSource.pitch = Mathf.Clamp(audioPlaybackSpeed * speedFactor, minPitch, maxPitch);
            }

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