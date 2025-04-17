using UnityEngine;
using UnityEngine.InputSystem;

public class Controllerhandsmanager : MonoBehaviour
{

    public InputActionReference triggerActionRef;
    public InputActionReference gripActionRef;

    public Animator handAnimator;


    private void Awake()
    {
        handAnimator = GetComponent<Animator>();
        SetupInputActions();

    }
    public void SetupInputActions()
    {
        if (triggerActionRef != null && gripActionRef != null)
        {
            triggerActionRef.action.performed += ctx => UpdateHandAnimation("Trigger", ctx.ReadValue<float>());
            triggerActionRef.action.canceled += ctx => UpdateHandAnimation("Trigger", 0);

            gripActionRef.action.performed += ctx => UpdateHandAnimation("Grip", ctx.ReadValue<float>());
            gripActionRef.action.canceled += ctx => UpdateHandAnimation("Grip",0);
        }
        else 
        {
            Debug.LogWarning("Input Action Reference are not set in the inspector");
        
        }

    }

    private void UpdateHandAnimation(string parameterName, float value)
    {
        if (handAnimator != null)
        {
            handAnimator.SetFloat(parameterName, value);
        }

    }

    private void OnEnable()
    {
        triggerActionRef?.action.Enable();
        gripActionRef?.action.Enable();
    }

    private void OnDisable()
    {
        triggerActionRef?.action.Disable();
        gripActionRef?.action.Disable();
    }
    
}
