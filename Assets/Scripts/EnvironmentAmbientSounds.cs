using UnityEngine;

public class EnvironmentAmbientSounds : MonoBehaviour
{
    [Header("Audio Settings")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private string playerTag = "Player";
    [SerializeField] private bool playOnEnter = true;
    [SerializeField] private bool stopOnExit = true;
    [SerializeField] private float fadeInTime = 0.5f;
    [SerializeField] private float fadeOutTime = 0.5f;

    private bool playerInside = false;
    private float currentVolumeVelocity = 0f;
    private float initialVolume;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        // Validate audio source
        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();

            if (audioSource == null)
            {
                Debug.LogError("No AudioSource found on " + gameObject.name + ". Please add an AudioSource component or assign one in the inspector.");
                enabled = false;
                return;
            }
        }

        // Store initial volume for fade effects
        initialVolume = audioSource.volume;

        // Make sure the audio source is not playing at start
        if (audioSource.playOnAwake)
        {
            audioSource.Stop();
        }

        // Set initial volume to 0
        audioSource.volume = 0f;
    }

    // Update is called once per frame
    void Update()
    {
        // Start playing if player entered and audio isn't playing
        if (playerInside && !audioSource.isPlaying && playOnEnter)
        {
            audioSource.Play();
        }

        // Smooth volume transition
        audioSource.volume = Mathf.SmoothDamp(
            audioSource.volume,
            playerInside ? initialVolume : 0f,
            ref currentVolumeVelocity,
            playerInside ? fadeInTime : fadeOutTime
        );

        // Stop audio if volume is near zero and player is outside
        if (!playerInside && audioSource.isPlaying && audioSource.volume < 0.01f && stopOnExit)
        {
            audioSource.Stop();
        }
    }

    // Called when another collider enters this object's trigger collider
    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag(playerTag))
        {
            playerInside = true;
        }
    }

    // Called when another collider exits this object's trigger collider
    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag(playerTag))
        {
            playerInside = false;
        }
    }
}