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
        public float customDelay = 0f;
        public bool isLooping = false;
        public AnimationCurve speedCurve = AnimationCurve.Linear(0, 1, 1, 1);
        public bool useLocalSpace = false;
    }

    [Header("Robot Configuration")]
    public List<RobotFunction> robotFunctions = new List<RobotFunction>();
    public float baseSpeed = 5f;
    public float commonDelay = 2f;
    public bool pauseAutomation = false;

    [Header("Movement Settings")]
    public bool useRotation = true;
    public float rotationSpeed = 10f;
    public float positionThreshold = 0.01f;
    public float rotationThreshold = 0.1f;

    [Header("Transform Settings")]
    public bool useLocalSpace = false;
    public Transform robotBase;
    public bool applyBaseOffset = true;

    [Header("Audio Settings")]
    public float audioPlaybackSpeed = 1f;
    public float minPitch = 0.8f;         // Minimum pitch even at slowest speed
    public float maxPitch = 1.2f;         // Maximum pitch even at highest speed
    public float pitchSmoothTime = 0.1f;  // How quickly pitch changes

    private float currentPitch;
    private float pitchVelocity;  // Used for SmoothDamp

    private AudioSource audioSource;

    [Header("Debug Settings")]
    public bool showDebugGizmos = true;
    public bool logMovements = true;

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
        StartAutomation();
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
            // Validate points
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
                foreach (var function in robotFunctions)
                {
                    if (!function.activateManually && !isExecutingFunction)
                    {
                        float delay = function.customDelay > 0 ? function.customDelay : commonDelay;
                        yield return new WaitForSeconds(delay);

                        if (!pauseAutomation)
                        {
                            ExecuteFunction(function);
                            yield return new WaitUntil(() => !isExecutingFunction);
                        }
                    }
                }
            }
            yield return null;
        }
    }

    private void Update()
    {
        if (!isExecutingFunction)
        {
            foreach (var function in robotFunctions)
            {
                if (function.activateManually)
                {
                    ExecuteFunction(function);
                    break;
                }
            }
        }
    }

    private void ExecuteFunction(RobotFunction function)
    {
        if (currentMovementCoroutine != null)
        {
            StopCoroutine(currentMovementCoroutine);
        }
        currentFunction = function;
        function.activateManually = false;
        isExecutingFunction = true;
        currentMovementCoroutine = StartCoroutine(MoveToNextPoint(function));
    }

    private Vector3 GetTargetPosition(Transform targetPoint, bool useLocal)
    {
        if (useLocal)
        {
            return robotBase.InverseTransformPoint(targetPoint.position);
        }
        return targetPoint.position;
    }

    private Quaternion GetTargetRotation(Transform targetPoint, bool useLocal)
    {
        if (useLocal)
        {
            return Quaternion.Inverse(robotBase.rotation) * targetPoint.rotation;
        }
        return targetPoint.rotation;
    }

    private Vector3 GetCurrentPosition(bool useLocal)
    {
        if (useLocal)
        {
            return robotBase.InverseTransformPoint(transform.position);
        }
        return transform.position;
    }
    public void SetAudioSpeed(float speed)
    {
        audioPlaybackSpeed = Mathf.Clamp(speed, 0.1f, 3f);  // Clamp between 0.1x and 3x speed
        if (audioSource != null)
        {
            audioSource.pitch = audioPlaybackSpeed;
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
                Transform targetPoint = function.points[i];
                if (targetPoint == null) continue;

                Vector3 targetPosition = GetTargetPosition(targetPoint, useLocal);
                Quaternion targetRotation = GetTargetRotation(targetPoint, useLocal);

                float journeyLength = Vector3.Distance(GetCurrentPosition(useLocal), targetPosition);
                float startTime = Time.time;
                // Only play audio if there's actually movement to perform
                if (journeyLength > positionThreshold && audioSource != null)
                {
                    audioSource.loop = true;
                    audioSource.pitch = audioPlaybackSpeed;
                    audioSource.Play();
                }

                while (true)
                {
                    Vector3 currentPos = GetCurrentPosition(useLocal);
                    float remainingDistance = Vector3.Distance(currentPos, targetPosition);

                    if (remainingDistance <= positionThreshold &&
                        (!useRotation || Quaternion.Angle(transform.rotation, targetRotation) <= rotationThreshold))
                    {
                        audioSource.Stop();
                        break;
                    }

                    float distanceCovered = (Time.time - startTime) * baseSpeed;
                    float fractionOfJourney = distanceCovered / journeyLength;
                    float speedMultiplier = function.speedCurve.Evaluate(fractionOfJourney);

                    // Smoothly adjust pitch based on speed
                    if (audioSource != null && audioSource.isPlaying)
                    {
                        // Map speedMultiplier to our desired pitch range
                        float targetPitch = Mathf.Lerp(minPitch, maxPitch, speedMultiplier) * audioPlaybackSpeed;

                        // Smoothly transition to target pitch
                        currentPitch = Mathf.SmoothDamp(
                            currentPitch,
                            targetPitch,
                            ref pitchVelocity,
                            pitchSmoothTime
                        );

                        audioSource.pitch = currentPitch;
                    }

                    // Position movement
                    Vector3 newPosition;
                    if (useLocal)
                    {
                        newPosition = Vector3.MoveTowards(
                            currentPos,
                            targetPosition,
                            baseSpeed * speedMultiplier * Time.deltaTime
                        );
                        transform.position = robotBase.TransformPoint(newPosition);
                    }
                    else
                    {
                        transform.position = Vector3.MoveTowards(
                            transform.position,
                            targetPosition,
                            baseSpeed * speedMultiplier * Time.deltaTime
                        );
                    }

                    // Rotation movement
                    if (useRotation)
                    {
                        transform.rotation = Quaternion.RotateTowards(
                            transform.rotation,
                            targetRotation,
                            rotationSpeed * Time.deltaTime
                        );
                    }

                    yield return null;
                }

                // Ensure final position is exact
                if (useLocal)
                {
                    transform.position = robotBase.TransformPoint(targetPosition);
                }
                else
                {
                    transform.position = targetPosition;
                }

                if (useRotation)
                {
                    transform.rotation = targetRotation;
                }

                OnPointReached?.Invoke(function.name, targetPoint.position);
                yield return new WaitForSeconds(0.5f);
            }
        } while (function.isLooping && !pauseAutomation);

        if (logMovements) Debug.Log($"Completed function: {function.name}");
        OnFunctionComplete?.Invoke(function.name);
        isExecutingFunction = false;
        currentFunction = null;
    }

    public void CallFunction(string functionName)
    {
        RobotFunction function = robotFunctions.Find(f => f.name == functionName);
        if (function != null)
        {
            function.activateManually = true;
        }
        else
        {
            Debug.LogWarning($"Function '{functionName}' not found.");
        }
    }

    public void PauseCurrentFunction()
    {
        if (currentMovementCoroutine != null)
        {
            StopCoroutine(currentMovementCoroutine);
            isExecutingFunction = false;
        }
    }

    public void ResumeCurrentFunction()
    {
        if (currentFunction != null && !isExecutingFunction)
        {
            ExecuteFunction(currentFunction);
        }
    }

    public void SetSpeed(float newSpeed)
    {
        baseSpeed = Mathf.Max(0.1f, newSpeed);
    }

    public void SetRotationSpeed(float newSpeed)
    {
        rotationSpeed = Mathf.Max(0.1f, newSpeed);
    }

    private void OnDrawGizmos()
    {
        if (!showDebugGizmos) return;

        foreach (var function in robotFunctions)
        {
            if (function.points.Count > 0)
            {
                // Draw movement paths
                Gizmos.color = function.useLocalSpace ? Color.blue : Color.yellow;
                for (int i = 0; i < function.points.Count - 1; i++)
                {
                    if (function.points[i] != null && function.points[i + 1] != null)
                    {
                        Gizmos.DrawLine(function.points[i].position, function.points[i + 1].position);
                    }
                }

                // Draw points
                Gizmos.color = Color.green;
                foreach (var point in function.points)
                {
                    if (point != null)
                    {
                        Gizmos.DrawWireSphere(point.position, 0.05f);
                        if (useRotation)
                        {
                            // Draw forward direction for rotation reference
                            Gizmos.DrawRay(point.position, point.forward * 0.2f);
                        }
                    }
                }
            }
        }

        // Draw robot base reference frame
        if (robotBase != null)
        {
            float axisLength = 0.5f;
            Gizmos.color = Color.red;
            Gizmos.DrawRay(robotBase.position, robotBase.right * axisLength);
            Gizmos.color = Color.green;
            Gizmos.DrawRay(robotBase.position, robotBase.up * axisLength);
            Gizmos.color = Color.blue;
            Gizmos.DrawRay(robotBase.position, robotBase.forward * axisLength);
        }
    }
}