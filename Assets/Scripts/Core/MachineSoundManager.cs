using UnityEngine;
using VME.IO;   // BoolIn / BoolOut wrappers

/// <summary>
/// Central manager that plays/stops machine sounds based on PLC IO.
/// - For each machine, you bind a BoolIn or BoolOut that means "machine is running"
/// - While that signal is TRUE → audio plays (looping)
/// - When FALSE → audio stops
/// </summary>
[DefaultExecutionOrder(0)]
public class MachineSoundManager : MonoBehaviour
{
    [System.Serializable]
    public class MachineSoundBinding
    {
        [Header("Identification")]
        public string MachineName;

        [Header("Run Condition (choose ONE signal)")]
        [Tooltip("Optional: PLC input that is HIGH while the machine is running (e.g. Busy, MotorOn).")]
        public BoolIn RunInput;      // uses PLCInputBool under the hood

        [Tooltip("Optional: PLC output that is HIGH while the machine is running (e.g. Motor_Fwd, Busy lamp).")]
        public BoolOut RunOutput;    // uses PLCOutputBool under the hood

        [Tooltip("If true, treat LOW as 'running' and HIGH as 'stopped'.")]
        public bool Invert;

        [Header("Audio")]
        [Tooltip("AudioSource located on/near this machine.")]
        public AudioSource Source;

        [Tooltip("Loop the clip while the machine is running.")]
        public bool LoopWhileRunning = true;

        [Range(0f, 1f)]
        public float Volume = 1f;

        [Tooltip("Small random pitch jitter at start so multiple machines don't sound identical.")]
        public float RandomPitchJitter = 0.03f;

        [HideInInspector] public bool IsRunning;   // internal state tracking
    }

    [Header("All Machine Bindings")]
    public MachineSoundBinding[] Machines;

    // ------------------------------------------------------

    void Awake()
    {
        // Configure AudioSources once
        if (Machines == null) return;

        foreach (var m in Machines)
        {
            if (m == null || m.Source == null) continue;

            m.Source.playOnAwake = false;
            m.Source.loop = m.LoopWhileRunning;
            m.Source.volume = m.Volume;
        }
    }

    void FixedUpdate()
    {
        if (Machines == null) return;

        foreach (var m in Machines)
        {
            if (m == null || m.Source == null) continue;

            // Sample input if present (safe even if tag is null)
            if (m.RunInput != null)
            {
                m.RunInput.Sample();
            }

            bool hasSignal = false;
            bool state = false;

            // Prefer RunInput if a tag is wired, otherwise fall back to RunOutput
            if (m.RunInput != null && m.RunInput.tag != null)
            {
                state = m.RunInput.v;
                hasSignal = true;
            }
            else if (m.RunOutput != null && m.RunOutput.tag != null)
            {
                state = m.RunOutput.Get();
                hasSignal = true;
            }

            if (!hasSignal) continue; // nothing wired for this machine

            if (m.Invert) state = !state;

            // Edge detection: stopped -> running
            if (state && !m.IsRunning)
            {
                StartMachineSound(m);
            }
            // Edge detection: running -> stopped
            else if (!state && m.IsRunning)
            {
                StopMachineSound(m);
            }

            m.IsRunning = state;
        }
    }

    void StartMachineSound(MachineSoundBinding m)
    {
        if (m.Source == null) return;

        // Small random pitch variation so multiple instances don't phase-lock
        if (m.RandomPitchJitter > 0f)
        {
            float basePitch = 1f;
            float d = m.RandomPitchJitter;
            m.Source.pitch = basePitch + Random.Range(-d, d);
        }

        if (!m.Source.isPlaying)
        {
            m.Source.loop = m.LoopWhileRunning;
            m.Source.volume = m.Volume;
            m.Source.Play();
        }
    }

    void StopMachineSound(MachineSoundBinding m)
    {
        if (m.Source == null) return;

        if (m.LoopWhileRunning)
        {
            // Hard stop for continuous loops
            m.Source.Stop();
        }
        else
        {
            // For one-shot clips you'd normally let them finish;
            // here we do nothing and they will end naturally.
        }
    }
}
