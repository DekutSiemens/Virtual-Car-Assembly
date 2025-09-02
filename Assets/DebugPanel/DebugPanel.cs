using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;

namespace LudicWorlds
{
    /// <summary>
    /// Enhanced Debug Panel with inspector-assignable UI components and improved functionality
    /// Features: FPS monitoring, debug message display, status updates, and billboard behavior
    /// </summary>
    public class DebugPanel : MonoBehaviour
    {
        [Header("UI Components")]
        [SerializeField] private Canvas canvas;
        [SerializeField] private Text debugText;
        [SerializeField] private Text fpsText;
        [SerializeField] private Text statusText;

        [Header("Display Settings")]
        [SerializeField] private int maxLines = 23;
        [SerializeField] private bool enableBillboard = true;
        [SerializeField] private bool rotateYAxisOnly = true;
        [SerializeField] private float fpsUpdateInterval = 0.5f;

        [Header("Advanced Options")]
        [SerializeField] private bool captureLogMessages = true;
        [SerializeField] private LogType minimumLogLevel = LogType.Log;
        [SerializeField] private bool showTimestamps = false;
        [SerializeField] private string timestampFormat = "HH:mm:ss";

        // Static references for external access
        private static DebugPanel _instance;
        private static Canvas _canvas;
        private static Text _debugText;
        private static Text _fpsText;
        private static Text _statusText;

        // FPS calculation
        private float _elapsedTime;
        private uint _fpsSamples;
        private float _sumFps;
        private float _lastFpsUpdate;

        // Message queue
        private Queue<string> _queuedMessages;

        // Billboard functionality
        private Transform _cameraTransform;
        private Vector3 _dirToPlayer = Vector3.zero;

        // Color coding for different log types
        private readonly Dictionary<LogType, string> _logColors = new Dictionary<LogType, string>
        {
            { LogType.Log, "#FFFFFF" },
            { LogType.Warning, "#FFFF00" },
            { LogType.Error, "#FF0000" },
            { LogType.Assert, "#FF8000" },
            { LogType.Exception, "#FF0000" }
        };

        void Awake()
        {
            // Singleton pattern
            if (_instance == null)
            {
                _instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else if (_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            InitializeComponents();
            InitializeVariables();

            if (captureLogMessages)
            {
                Application.logMessageReceived += OnMessageReceived;
            }
        }

        void Start()
        {
            if (enableBillboard && Camera.main != null)
            {
                _cameraTransform = Camera.main.transform;
            }
            else if (enableBillboard)
            {
                Debug.LogWarning("[DebugPanel] Billboard enabled but no main camera found!");
            }
        }

        void OnDestroy()
        {
            if (captureLogMessages)
            {
                Application.logMessageReceived -= OnMessageReceived;
            }

            if (_instance == this)
            {
                _instance = null;
                _canvas = null;
                _debugText = null;
                _fpsText = null;
                _statusText = null;
            }
        }

        void Update()
        {
            UpdateFPS();
            UpdateBillboard();
            ProcessQueuedMessages();
        }

        #region Initialization
        private void InitializeComponents()
        {
            // Auto-assign components if not set in inspector
            if (canvas == null)
                canvas = GetComponent<Canvas>();

            if (canvas == null)
            {
                Debug.LogError("[DebugPanel] No Canvas component found! Please assign one in the inspector.");
                return;
            }

            // Try to find UI components if not assigned
            Transform uiTransform = transform.Find("UI");
            if (uiTransform != null && (debugText == null || fpsText == null || statusText == null))
            {
                if (debugText == null)
                {
                    Transform debugTransform = uiTransform.Find("DebugText");
                    if (debugTransform != null)
                        debugText = debugTransform.GetComponent<Text>();
                }

                if (fpsText == null)
                {
                    Transform fpsTransform = uiTransform.Find("FpsText");
                    if (fpsTransform != null)
                        fpsText = fpsTransform.GetComponent<Text>();
                }

                if (statusText == null)
                {
                    Transform statusTransform = uiTransform.Find("StatusText");
                    if (statusTransform != null)
                        statusText = statusTransform.GetComponent<Text>();
                }
            }

            // Set static references
            _canvas = canvas;
            _debugText = debugText;
            _fpsText = fpsText;
            _statusText = statusText;

            // Validate components
            if (debugText == null) Debug.LogWarning("[DebugPanel] Debug Text not assigned!");
            if (fpsText == null) Debug.LogWarning("[DebugPanel] FPS Text not assigned!");
            if (statusText == null) Debug.LogWarning("[DebugPanel] Status Text not assigned!");
        }

        private void InitializeVariables()
        {
            _elapsedTime = 0;
            _fpsSamples = 0;
            _sumFps = 0;
            _lastFpsUpdate = Time.time;
            _queuedMessages = new Queue<string>();

            if (fpsText != null)
                fpsText.text = "0 FPS";
        }
        #endregion

        #region Update Methods
        private void UpdateFPS()
        {
            _elapsedTime += Time.deltaTime;
            _sumFps += (1.0f / Time.smoothDeltaTime);
            _fpsSamples++;

            if (_elapsedTime >= fpsUpdateInterval)
            {
                if (fpsText != null && _fpsSamples > 0)
                {
                    int avgFps = Mathf.RoundToInt(_sumFps / _fpsSamples);
                    fpsText.text = $"{avgFps} FPS";

                    // Color code FPS based on performance
                    if (avgFps >= 60)
                        fpsText.color = Color.green;
                    else if (avgFps >= 30)
                        fpsText.color = Color.yellow;
                    else
                        fpsText.color = Color.red;
                }

                _elapsedTime = 0f;
                _sumFps = 0f;
                _fpsSamples = 0;
            }
        }

        private void UpdateBillboard()
        {
            if (!enableBillboard || _cameraTransform == null) return;

            _dirToPlayer = (transform.position - _cameraTransform.position).normalized;

            if (rotateYAxisOnly)
            {
                _dirToPlayer.y = 0; // Only rotate around Y-axis
            }

            if (_dirToPlayer != Vector3.zero)
            {
                transform.rotation = Quaternion.LookRotation(_dirToPlayer);
            }
        }

        private void ProcessQueuedMessages()
        {
            if (_queuedMessages.Count == 0 || debugText == null) return;

            while (_queuedMessages.Count > 0)
            {
                debugText.text += _queuedMessages.Dequeue();
            }

            TrimText();
        }
        #endregion

        #region Message Handling
        void OnMessageReceived(string message, string stackTrace, LogType type)
        {
            // Filter based on minimum log level
            if ((int)type > (int)minimumLogLevel) return;

            string colorCode = _logColors.ContainsKey(type) ? _logColors[type] : "#FFFFFF";
            string timestamp = showTimestamps ? $"[{System.DateTime.Now.ToString(timestampFormat)}] " : "";
            string typePrefix = type == LogType.Log ? "" : $"[{type.ToString().ToUpper()}] ";

            string formattedMessage = $"{timestamp}{typePrefix}<color={colorCode}>{message}</color>\n";
            _queuedMessages.Enqueue(formattedMessage);
        }

        private static void TrimText()
        {
            if (_debugText == null) return;

            string[] lines = _debugText.text.Split('\n');
            DebugPanel instance = _instance;
            int maxLinesActual = instance != null ? instance.maxLines : 23;

            if (lines.Length > maxLinesActual)
            {
                _debugText.text = string.Join("\n", lines, lines.Length - maxLinesActual, maxLinesActual);
            }
        }
        #endregion

        #region Public Static Methods
        /// <summary>
        /// Clear all debug text
        /// </summary>
        public static void Clear()
        {
            if (_debugText != null)
                _debugText.text = "";
        }

        /// <summary>
        /// Show the debug panel
        /// </summary>
        public static void Show()
        {
            SetVisibility(true);
        }

        /// <summary>
        /// Hide the debug panel
        /// </summary>
        public static void Hide()
        {
            SetVisibility(false);
        }

        /// <summary>
        /// Set debug panel visibility
        /// </summary>
        /// <param name="visible">True to show, false to hide</param>
        public static void SetVisibility(bool visible)
        {
            if (_canvas != null)
                _canvas.enabled = visible;
        }

        /// <summary>
        /// Toggle debug panel visibility
        /// </summary>
        public static void ToggleVisibility()
        {
            if (_canvas != null)
                _canvas.enabled = !_canvas.enabled;
        }

        /// <summary>
        /// Set status text message
        /// </summary>
        /// <param name="message">Status message to display</param>
        public static void SetStatus(string message)
        {
            if (_statusText != null)
                _statusText.text = message;
        }

        /// <summary>
        /// Add a custom debug message with color
        /// </summary>
        /// <param name="message">Message to display</param>
        /// <param name="color">Color in hex format (e.g., "#FF0000" for red)</param>
        public static void LogColored(string message, string color = "#FFFFFF")
        {
            if (_instance?._queuedMessages != null)
            {
                string timestamp = _instance.showTimestamps ? $"[{System.DateTime.Now.ToString(_instance.timestampFormat)}] " : "";
                _instance._queuedMessages.Enqueue($"{timestamp}<color={color}>{message}</color>\n");
            }
        }

        /// <summary>
        /// Log a message with automatic timestamp
        /// </summary>
        /// <param name="message">Message to log</param>
        public static void Log(string message)
        {
            LogColored(message);
        }

        /// <summary>
        /// Get current FPS
        /// </summary>
        /// <returns>Current average FPS</returns>
        public static float GetCurrentFPS()
        {
            return _instance != null && _instance._fpsSamples > 0 ?
                   _instance._sumFps / _instance._fpsSamples : 0f;
        }
        #endregion

        #region Editor Helpers
#if UNITY_EDITOR
        [ContextMenu("Auto-Assign Components")]
        void AutoAssignComponents()
        {
            if (canvas == null)
                canvas = GetComponent<Canvas>();

            Transform uiTransform = transform.Find("UI");
            if (uiTransform != null)
            {
                Transform debugTransform = uiTransform.Find("DebugText");
                if (debugTransform != null && debugText == null)
                    debugText = debugTransform.GetComponent<Text>();

                Transform fpsTransform = uiTransform.Find("FpsText");
                if (fpsTransform != null && fpsText == null)
                    fpsText = fpsTransform.GetComponent<Text>();

                Transform statusTransform = uiTransform.Find("StatusText");
                if (statusTransform != null && statusText == null)
                    statusText = statusTransform.GetComponent<Text>();
            }

            UnityEditor.EditorUtility.SetDirty(this);
        }

        void OnValidate()
        {
            // Clamp values to reasonable ranges
            maxLines = Mathf.Clamp(maxLines, 1, 100);
            fpsUpdateInterval = Mathf.Clamp(fpsUpdateInterval, 0.1f, 5f);
        }
#endif
        #endregion
    }
}