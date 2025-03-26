using UnityEngine;

public class RobotArmSoundController : MonoBehaviour
{
    public AudioSource audioSource; // Reference to the Audio Source component
    public Transform roboticArm; // Reference to the robotic arm transform

    public float pitchMultiplier = 1.0f; // Controls pitch scaling
    public float minPitch = 0.8f; // Minimum pitch value
    public float maxPitch = 2.0f; // Maximum pitch value

    private Vector3 lastPosition;

    void Start()
    {
        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
        }

        if (roboticArm == null)
        {
            roboticArm = transform;
        }

        lastPosition = roboticArm.position;
        audioSource.loop = true;
        audioSource.Play();
    }

    void Update()
    {
        float speed = (roboticArm.position - lastPosition).magnitude / Time.deltaTime;

        if (speed > 0.01f) // Robot is moving
        {
            if (!audioSource.isPlaying)
                audioSource.Play();

            float newPitch = Mathf.Clamp(1 + (speed * pitchMultiplier), minPitch, maxPitch);
            audioSource.pitch = newPitch;
        }
        else // Robot is not moving
        {
            audioSource.Pause();
        }

        lastPosition = roboticArm.position;
    }
}
