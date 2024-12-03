using UnityEngine;

public class TriggerCollisionDetector : MonoBehaviour
{
    [Header("Particle Systems")]
    [SerializeField] private ParticleSystem particleSystem1;
    [SerializeField] private ParticleSystem particleSystem2;
    [SerializeField] private ParticleSystem particleSystem3;

    [Header("Audio")]
    [SerializeField] private AudioSource triggerSound;
    [SerializeField] private bool loopAudio = true;

    // Optional: Tag to filter specific objects
    public string targetTag = "";

    // Flags to track states
    private bool isParticle1Playing = false;
    private bool isParticle2Playing = false;
    private bool isParticle3Playing = false;
    private bool isSoundPlaying = false;

    private void Start()
    {
        // Validate Particle System references
        if (particleSystem1 == null || particleSystem2 == null || particleSystem3 == null)
        {
            Debug.LogWarning("One or more Particle Systems not assigned to TriggerCollisionDetector!");
        }

        // Validate Audio Source
        if (triggerSound == null)
        {
            Debug.LogWarning("Audio Source not assigned to TriggerCollisionDetector!");
        }
        else
        {
            // Configure audio source
            triggerSound.loop = loopAudio;
            triggerSound.playOnAwake = false;
            triggerSound.Stop();
        }

        // Initialize all particle systems
        InitializeParticleSystem(particleSystem1);
        InitializeParticleSystem(particleSystem2);
        InitializeParticleSystem(particleSystem3);
    }

    private void InitializeParticleSystem(ParticleSystem ps)
    {
        if (ps != null)
        {
            ps.Stop(true);
            var emission = ps.emission;
            emission.enabled = false;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (string.IsNullOrEmpty(targetTag) || other.CompareTag(targetTag))
        {
            Debug.Log($"Trigger Enter: {other.gameObject.name}");
            HandleTriggerEnter(other.gameObject);
        }
    }

    private void OnTriggerStay(Collider other)
    {
        if (string.IsNullOrEmpty(targetTag) || other.CompareTag(targetTag))
        {
            Debug.Log($"Trigger Stay: {other.gameObject.name}");
            HandleTriggerStay(other.gameObject);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (string.IsNullOrEmpty(targetTag) || other.CompareTag(targetTag))
        {
            Debug.Log($"Trigger Exit: {other.gameObject.name}");
            HandleTriggerExit(other.gameObject);
        }
    }

    protected virtual void HandleTriggerEnter(GameObject other)
    {
        // Add your custom enter logic here
    }

    protected virtual void HandleTriggerStay(GameObject other)
    {
        // Play first particle system
        if (particleSystem1 != null && !isParticle1Playing)
        {
            var emission1 = particleSystem1.emission;
            emission1.enabled = true;
            particleSystem1.Play(true);
            isParticle1Playing = true;
        }

        // Play second particle system
        if (particleSystem2 != null && !isParticle2Playing)
        {
            var emission2 = particleSystem2.emission;
            emission2.enabled = true;
            particleSystem2.Play(true);
            isParticle2Playing = true;
        }

        // Play third particle system
        if (particleSystem3 != null && !isParticle3Playing)
        {
            var emission3 = particleSystem3.emission;
            emission3.enabled = true;
            particleSystem3.Play(true);
            isParticle3Playing = true;
        }

        // Play sound
        if (triggerSound != null && !isSoundPlaying)
        {
            triggerSound.Play();
            isSoundPlaying = true;
        }
    }

    protected virtual void HandleTriggerExit(GameObject other)
    {
        // Stop first particle system
        if (particleSystem1 != null)
        {
            var emission1 = particleSystem1.emission;
            emission1.enabled = false;
            particleSystem1.Stop(true);
            isParticle1Playing = false;
        }

        // Stop second particle system
        if (particleSystem2 != null)
        {
            var emission2 = particleSystem2.emission;
            emission2.enabled = false;
            particleSystem2.Stop(true);
            isParticle2Playing = false;
        }

        // Stop third particle system
        if (particleSystem3 != null)
        {
            var emission3 = particleSystem3.emission;
            emission3.enabled = false;
            particleSystem3.Stop(true);
            isParticle3Playing = false;
        }

        // Stop sound
        if (triggerSound != null)
        {
            triggerSound.Stop();
            isSoundPlaying = false;
        }
    }
}