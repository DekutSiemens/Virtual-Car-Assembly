using System;
using UnityEngine;
using UnityEngine.InputSystem;

public class WalkingAnimationController : MonoBehaviour
{
    [SerializeField] private InputActionReference move;
    [SerializeField] private Animator animator;


    private void OnEnable()
    {
        move.action.started += AnimateLegs;
        move.action.canceled += StopAnimation;

    }
    private void OnDisable()
    {
        move.action.started -= AnimateLegs;
        move.action.canceled += StopAnimation;
    }

    private void AnimateLegs(InputAction.CallbackContext obj)
    {
       bool isMovingForward = move.action.ReadValue<Vector2>().y > 0;
        if (isMovingForward)
        {
            animator.SetBool("isWalking", true);
            animator.SetFloat("animSpeed", 1);
        }
        else
        {
            animator.SetBool("isWalking", true);
            animator.SetFloat("animSpeed", -1);
        }
        

    }

    private void StopAnimation(InputAction.CallbackContext obj)
    {
        animator.SetBool("isWalking", false);
        animator.SetFloat("animSpeed", 0);
    }
}
