using UnityEngine;
using UnityEngine.Splines;

namespace nickmaltbie.ScrollingShader
{
    public class ConveyerBelt : MonoBehaviour
    {
        public enum BeltForceMode { Push, Pull }
        public enum RelativeDirection { Up, Down, Left, Right, Forward, Backward, Spline }

        [SerializeField] private float velocity = 0f;
        [SerializeField] private RelativeDirection direction = RelativeDirection.Down;
        [SerializeField] private BeltForceMode beltMode = BeltForceMode.Push;
        [SerializeField] private Renderer beltRenderer;
        [SerializeField] private string scrollSpeedProperty = "_ScrollSpeedX";
        [SerializeField] private AudioSource conveyorAudio;
        [SerializeField] private SplineContainer conveyorSpline; // Assign a Spline when using Spline mode

        private Rigidbody body;
        private Vector3 pos;
        private Material beltMaterial;

        public void Awake()
        {
            body = GetComponent<Rigidbody>();
            pos = transform.position;
            if (beltRenderer != null)
            {
                beltMaterial = beltRenderer.material;
            }
            if (conveyorAudio != null)
            {
                conveyorAudio.loop = true;
                conveyorAudio.Play();
                conveyorAudio.Pause();
            }
        }

        public void FixedUpdate()
        {
            if (body != null && beltMode == BeltForceMode.Pull && direction != RelativeDirection.Spline)
            {
                Vector3 movement = velocity * GetDirection() * Time.fixedDeltaTime;
                transform.position = pos - movement;
                body.MovePosition(pos);
            }
            UpdateScrollingShader();
            UpdateAudio();
        }

        public void OnCollisionStay(Collision other)
        {
            if (other.rigidbody != null && !other.rigidbody.isKinematic)
            {
                if (direction == RelativeDirection.Spline && conveyorSpline != null)
                {
                    MoveAlongSpline(other.rigidbody);
                }
                else if (beltMode == BeltForceMode.Push)
                {
                    Vector3 movement = velocity * GetDirection() * Time.deltaTime;
                    other.rigidbody.MovePosition(other.transform.position + movement);
                }
            }
        }

        private void MoveAlongSpline(Rigidbody obj)
        {
            if (conveyorSpline == null) return;

            float progress = Mathf.Repeat(Time.time * velocity, 1f); // Moves object along the spline
            Vector3 newPosition = conveyorSpline.EvaluatePosition(progress);
            obj.MovePosition(newPosition);
        }

        private void UpdateScrollingShader()
        {
            if (beltMaterial != null)
            {
                beltMaterial.SetFloat(scrollSpeedProperty, velocity);
            }
        }

        private void UpdateAudio()
        {
            if (conveyorAudio != null)
            {
                if (velocity > 0)
                {
                    if (!conveyorAudio.isPlaying)
                    {
                        conveyorAudio.UnPause();
                    }
                }
                else
                {
                    conveyorAudio.Pause();
                }
            }
        }

        public Vector3 GetDirection()
        {
            return direction switch
            {
                RelativeDirection.Up => transform.up,
                RelativeDirection.Down => -transform.up,
                RelativeDirection.Left => -transform.right,
                RelativeDirection.Right => transform.right,
                RelativeDirection.Forward => transform.forward,
                RelativeDirection.Backward => -transform.forward,
                _ => transform.forward,
            };
        }

        // ------- SIGNAL RECEIVER CONTROL METHODS -------

        /// <summary>
        /// Set the conveyor belt speed dynamically (for Signal Receiver)
        /// </summary>
        /// <param name="speed">New belt speed</param>
        public void SetBeltSpeed(float speed)
        {
            velocity = speed;
            UpdateScrollingShader();
            UpdateAudio();
        }

        /// <summary>
        /// Stops the conveyor belt (for Signal Receiver)
        /// </summary>
        public void StopBelt()
        {
            SetBeltSpeed(0);
        }

        /// <summary>
        /// Change the conveyor belt direction dynamically (for Signal Receiver)
        /// </summary>
        /// <param name="dirIndex">Index of the direction (0=Up, 1=Down, etc.)</param>
        public void SetDirection(int dirIndex)
        {
            if (dirIndex >= 0 && dirIndex < System.Enum.GetValues(typeof(RelativeDirection)).Length)
            {
                direction = (RelativeDirection)dirIndex;
            }
        }

        /// <summary>
        /// Assigns a new spline dynamically (for Signal Receiver)
        /// </summary>
        /// <param name="spline">SplineContainer to assign</param>
        public void SetSpline(SplineContainer spline)
        {
            conveyorSpline = spline;
        }
    }
}
