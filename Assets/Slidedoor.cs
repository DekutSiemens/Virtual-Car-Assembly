using UnityEngine;

public class Slidedoor : MonoBehaviour
{
    public Animator rightDoor;
    public Animator leftDoor;
    public AudioSource doorSound;

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            rightDoor.SetTrigger("open");
            leftDoor.SetTrigger("open");

            if (doorSound != null)
            {
                doorSound.Play();
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            leftDoor.SetTrigger("close");
            rightDoor.SetTrigger("close");

            if (doorSound != null)
            {
                doorSound.Play();
            }
        }
    }
}