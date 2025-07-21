using UnityEngine;
using UnityEngine.Playables;
using System.Collections.Generic;

[RequireComponent(typeof(PlayableDirector))] // Ensures PlayableDirector is attached
public class PlaySteps : MonoBehaviour
{
    private PlayableDirector director;
    public List<Step> steps;

    void Start()
    {
        director = GetComponent<PlayableDirector>();

        if (director == null)
        {
            Debug.LogError($"[PlaySteps] Add a PlayableDirector component to '{gameObject.name}'!", this);
        }
    }

    [System.Serializable]
    public class Step
    {
        public string name;
        public float time;
        public bool hasPlayed = false;
    }

    public void PLayStepIndex(int index)
    {
        if (index < 0 || index >= steps.Count)
        {
            Debug.LogWarning($"[PlaySteps] Invalid step index {index}!", this);
            return;
        }

        Step step = steps[index];
        if (!step.hasPlayed)
        {
            step.hasPlayed = true;

            if (director != null)
            {
                director.Stop();
                director.time = step.time;
                director.Play();
            }
            else
            {
                Debug.LogError($"[PlaySteps] PlayableDirector is missing on '{gameObject.name}'!", this);
            }
        }
    }

    public void ResetSteps()
    {
        foreach (Step step in steps)
        {
            step.hasPlayed = false;
        }

        Debug.Log("[PlaySteps] All steps have been reset.");
    }
}
