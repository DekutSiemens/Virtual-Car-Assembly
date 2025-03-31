using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerTransformChange : MonoBehaviour
{
    public List<Transform> targetTransforms; // List of target positions
    public Transform player; // Reference to the player
    public FadeScreen fadeScreen; // Reference to fade screen

    public void MovePlayerTo(int index)
    {
        if (index < 0 || index >= targetTransforms.Count)
        {
            Debug.LogError("Invalid index: " + index);
            return;
        }

        StartCoroutine(TransitionPlayer(index));
    }

    private IEnumerator TransitionPlayer(int index)
    {
        // Fade out
        if (fadeScreen != null)
        {
            fadeScreen.FadeOut();
            yield return new WaitForSeconds(fadeScreen.fadeDuration);
        }

        // Move the player
        player.position = targetTransforms[index].position;
        player.rotation = targetTransforms[index].rotation;

        // Fade in
        if (fadeScreen != null)
        {
            fadeScreen.FadeIn();
        }
    }
}
