using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class autoOpen : MonoBehaviour
{
    public Animator doorAnim;

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            doorAnim.ResetTrigger("close");
            doorAnim.SetTrigger("open");
        }
    }
    void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            doorAnim.ResetTrigger("open");
            doorAnim.SetTrigger("close");
        }
    }
}
