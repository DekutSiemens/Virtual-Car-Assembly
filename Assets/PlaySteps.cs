using UnityEngine;
using UnityEngine.Playables;
using System.Collections;
using System.Collections.Generic;

public class PlaySteps : MonoBehaviour
{
    PlayableDirector director;
    public List<Step> steps;


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        director = GetComponent<PlayableDirector>();
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
        Step step = steps[index];
        if(!step.hasPlayed )
        {
            step.hasPlayed = true;

            director.Stop();
            director.time =step.time;
            director.Play();
        }
    }
}
