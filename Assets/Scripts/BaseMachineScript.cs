using UnityEngine;

public abstract class BaseMachineScript : MonoBehaviour
{
    public abstract void TurnOn();
    public abstract void TurnOff();
    
    public abstract float GetSpeed();
    public abstract bool IsRunning(); // Add IsRunning to the base class as well
}
