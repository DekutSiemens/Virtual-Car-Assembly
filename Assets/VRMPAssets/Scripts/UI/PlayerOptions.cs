using UnityEngine;
using UnityEditor;
using UnityEngine.Audio;
using TMPro;
using System;
using Unity.Netcode;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;
using UnityEngine.Android;

namespace XRMultiplayer
{
    [DefaultExecutionOrder(100)]
    public class PlayerOptions : MonoBehaviour
    {
        [SerializeField] InputActionReference m_ToggleMenuAction;
        [SerializeField] AudioMixer m_Mixer;

        [Header("Panels")]
        [SerializeField] GameObject m_HostRoomPanel;
        [SerializeField] GameObject m_ClientRoomPanel;
        [SerializeField] GameObject[] m_OfflineWarningPanels;
        [SerializeField] GameObject[] m_OnlinePanels;
        [SerializeField] GameObject[] m_Panels;
        [SerializeField] Toggle[] m_PanelToggles;

        [Header("Text Components")]
        [SerializeField] TMP_Text m_SnapTurnText;
        [SerializeField] TMP_Text m_RoomCodeText;
        [SerializeField] TMP_Text m_TimeText;
        [SerializeField] TMP_Text[] m_RoomNameText;
        [SerializeField] TMP_InputField m_RoomNameInputField;
        [SerializeField] TMP_Text[] m_PlayerCountText;

        [Header("Voice Chat")]
        [SerializeField] Button m_MicPermsButton;
        [SerializeField] Slider m_InputVolumeSlider;
        [SerializeField] Slider m_OutputVolumeSlider;
        [SerializeField] Image m_LocalPlayerAudioVolume;
        [SerializeField] Image m_MutedIcon;
        [SerializeField] Image m_MicOnIcon;
        [SerializeField] TMP_Text m_VoiceChatStatus;

        [Header("Player Options")]
        [SerializeField] Vector2 m_MinMaxMoveSpeed = new Vector2(2.0f, 10.0f);
        [SerializeField] Vector2 m_MinMaxTurnAmount = new Vector2(15.0f, 180.0f);
        [SerializeField] float m_SnapTurnUpdateAmount = 15.0f;

        VoiceChatManager m_VoiceChatManager;
        DynamicMoveProvider m_MoveProvider;
        SnapTurnProvider m_TurnProvider;
        UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort.TunnelingVignetteController m_TunnelingVignetteController;

        PermissionCallbacks permCallbacks;

        private void Awake()
        {
            m_VoiceChatManager = FindFirstObjectByType<VoiceChatManager>();
            m_MoveProvider = FindFirstObjectByType<DynamicMoveProvider>();
            m_TurnProvider = FindFirstObjectByType<SnapTurnProvider>();
            m_TunnelingVignetteController = FindFirstObjectByType<UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort.TunnelingVignetteController>();

            XRINetworkGameManager.Connected.Subscribe(ConnectOnline);
            XRINetworkGameManager.ConnectedRoomName.Subscribe(UpdateRoomName);

            m_VoiceChatManager.selfMuted.Subscribe(MutedChanged);
            m_VoiceChatManager.connectionStatus.Subscribe(UpdateVoiceChatStatus);
            m_InputVolumeSlider.onValueChanged.AddListener(SetInputVolume);
            m_OutputVolumeSlider.onValueChanged.AddListener(SetOutputVolume);

            ConnectOnline(false);

            if (m_ToggleMenuAction != null)
                m_ToggleMenuAction.action.performed += ctx => ToggleMenu();
            else
                Utils.Log("No toggle menu action assigned to OptionsPanel", 1);

            permCallbacks = new PermissionCallbacks();
            permCallbacks.PermissionDenied += PermissionCallbacks_PermissionDenied;
            permCallbacks.PermissionGranted += PermissionCallbacks_PermissionGranted;
        }

        internal void PermissionCallbacks_PermissionGranted(string permissionName)
        {
            Utils.Log($"{permissionName} PermissionCallbacks_PermissionGranted");
            m_MicPermsButton.gameObject.SetActive(false);
        }

        internal void PermissionCallbacks_PermissionDenied(string permissionName)
        {
            Utils.Log($"{permissionName} PermissionCallbacks_PermissionDenied");
        }

        void OnEnable()
        {
            TogglePanel(0);

            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                m_MicPermsButton.gameObject.SetActive(true);
            }
            else
            {
                m_MicPermsButton.gameObject.SetActive(false);
            }
        }

        private void OnDestroy()
        {
            XRINetworkGameManager.Connected.Unsubscribe(ConnectOnline);
            XRINetworkGameManager.ConnectedRoomName.Unsubscribe(UpdateRoomName);
            m_VoiceChatManager.selfMuted.Unsubscribe(MutedChanged);

            m_VoiceChatManager.connectionStatus.Unsubscribe(UpdateVoiceChatStatus);
            m_InputVolumeSlider.onValueChanged.RemoveListener(SetInputVolume);
            m_OutputVolumeSlider.onValueChanged.RemoveListener(SetOutputVolume);
        }

        private void Update()
        {
            m_TimeText.text = $"{DateTime.Now:h:mm}<size=4><voffset=1em>{DateTime.Now:tt}</size></voffset>";
            if (XRINetworkGameManager.Connected.Value)
            {
                m_LocalPlayerAudioVolume.fillAmount = XRINetworkPlayer.LocalPlayer.playerVoiceAmp;
            }
            else
            {
                m_LocalPlayerAudioVolume.fillAmount = OfflinePlayerAvatar.voiceAmp.Value;
            }
        }

        void ConnectOnline(bool connected)
        {
            foreach (var go in m_OfflineWarningPanels)
            {
                go.SetActive(!connected);
            }

            foreach (var go in m_OnlinePanels)
            {
                go.SetActive(connected);
            }

            if (connected)
            {
                m_HostRoomPanel.SetActive(NetworkManager.Singleton.IsServer);
                m_ClientRoomPanel.SetActive(!NetworkManager.Singleton.IsServer);
                UpdateRoomName(XRINetworkGameManager.ConnectedRoomName.Value);
                m_MutedIcon.enabled = false;
                m_MicOnIcon.enabled = true;
                m_LocalPlayerAudioVolume.enabled = true;
            }
            else
            {
                ToggleMenu(false);
            }
        }

        public void TogglePanel(int panelID)
        {
            for (int i = 0; i < m_Panels.Length; i++)
            {
                m_PanelToggles[i].SetIsOnWithoutNotify(panelID == i);
                m_Panels[i].SetActive(i == panelID);
            }
        }

        /// <summary>
        /// Toggles the menu on or off.
        /// </summary>
        /// <param name="overrideToggle"></param>
        /// <param name="overrideValue"></param>
        public void ToggleMenu(bool overrideToggle = false, bool overrideValue = false)
        {
            if (overrideToggle)
            {
                gameObject.SetActive(overrideValue);
            }
            else
            {
                ToggleMenu();
            }
            TogglePanel(0);
        }

        public void ToggleMenu()
        {
            gameObject.SetActive(!gameObject.activeSelf);
        }

        public void LogOut()
        {
            XRINetworkGameManager.Instance.Disconnect();
        }

        public void QuickJoin()
        {
            XRINetworkGameManager.Instance.QuickJoinLobby();
        }

        public void QuitGame()
        {
#if UNITY_EDITOR
            EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        void UpdateVoiceChatStatus(string statusMessage)
        {
            m_VoiceChatStatus.text = $"<b>Voice Chat:</b> {statusMessage}";
        }
        public void SetVolumeLevel(float sliderValue)
        {
            m_Mixer.SetFloat("MainVolume", Mathf.Log10(sliderValue) * 20);
        }
        public void SetInputVolume(float volume)
        {
            float perc = Mathf.Lerp(-10, 10, volume);
            m_VoiceChatManager.SetInputVolume(perc);
        }

        public void SetOutputVolume(float volume)
        {
            float perc = Mathf.Lerp(-10, 10, volume);
            m_VoiceChatManager.SetOutputVolume(perc);
        }

        public void ToggleMute()
        {
            m_VoiceChatManager.ToggleSelfMute();
        }

        void MutedChanged(bool muted)
        {
            m_MutedIcon.enabled = muted;
            m_MicOnIcon.enabled = !muted;
            m_LocalPlayerAudioVolume.enabled = !muted;
            PlayerHudNotification.Instance.ShowText($"<b>Microphone: {(muted ? "OFF" : "ON")}</b>");
        }

        // Room Options
        public void UpdateRoomPrivacy(bool toggle)
        {
            XRINetworkGameManager.Instance.lobbyManager.UpdateRoomPrivacy(toggle);
        }

        public void SubmitNewRoomName(string text)
        {
            XRINetworkGameManager.Instance.lobbyManager.UpdateLobbyName(text);
        }

        void UpdateRoomName(string newValue)
        {
            m_RoomCodeText.text = $"Room Code: {XRINetworkGameManager.ConnectedRoomCode}";
            foreach (var t in m_RoomNameText)
            {
                t.text = XRINetworkGameManager.ConnectedRoomName.Value;
            }
            m_RoomNameInputField.text = XRINetworkGameManager.ConnectedRoomName.Value;
        }

        // Player Options
        public void SetHandOrientation(bool toggle)
        {
            if (toggle)
            {
                m_MoveProvider.leftHandMovementDirection = DynamicMoveProvider.MovementDirection.HandRelative;
            }
        }
        public void SetHeadOrientation(bool toggle)
        {
            if (toggle)
            {
                m_MoveProvider.leftHandMovementDirection = DynamicMoveProvider.MovementDirection.HeadRelative;
            }
        }
        public void SetMoveSpeed(float speedPercent)
        {
            m_MoveProvider.moveSpeed = Mathf.Lerp(m_MinMaxMoveSpeed.x, m_MinMaxMoveSpeed.y, speedPercent);
        }

        public void UpdateSnapTurn(int dir)
        {
            float newTurnAmount = Mathf.Clamp(m_TurnProvider.turnAmount + (m_SnapTurnUpdateAmount * dir), m_MinMaxTurnAmount.x, m_MinMaxTurnAmount.y);
            m_TurnProvider.turnAmount = newTurnAmount;
            m_SnapTurnText.text = $"{newTurnAmount}°";
        }

        public void ToggleTunnelingVignette(bool toggle)
        {
            m_TunnelingVignetteController.gameObject.SetActive(toggle);
        }

        public void ToggleFlight(bool toggle)
        {
            m_MoveProvider.useGravity = !toggle;
            m_MoveProvider.enableFly = toggle;
        }
    }
}
