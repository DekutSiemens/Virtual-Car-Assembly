using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;

public class SetTurnTypeFromPlayerPref : MonoBehaviour
{
    public SnapTurnProvider snapTurn;
    public ContinuousTurnProvider continuousTurn;

    void Start()
    {
        ApplyPlayerPref();
    }

    public void ApplyPlayerPref()
    {
        if (PlayerPrefs.HasKey("turn"))
        {
            int value = PlayerPrefs.GetInt("turn");
            if (value == 0)
            {
                // Enable snap turn
                snapTurn.enabled = true;
                continuousTurn.enabled = false;
            }
            else if (value == 1)
            {
                // Enable continuous turn
                snapTurn.enabled = false;
                continuousTurn.enabled = true;
            }
        }
    }
}