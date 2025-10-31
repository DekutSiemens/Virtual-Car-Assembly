using System;
using UnityEngine;
using realvirtual; // PLCInputBool / PLCOutputBool

/// Mirrors multiple PLCOutputBool → PLCInputBool.
/// Writes to Target.Status.ValueOverride (when Override=true).
[DefaultExecutionOrder(-200)]
public class SignalBridgeBoolMulti : MonoBehaviour
{
    [Serializable]
    public struct Map
    {
        [Tooltip("Producer (e.g., CutterController's CutComplete OUT)")]
        public PLCOutputBool Source;

        [Tooltip("Consumer (e.g., OutfeedController's CutComplete IN)")]
        public PLCInputBool Target;

        [Tooltip("Write NOT(Source) to Target when true.")]
        public bool Invert;
    }

    [Header("Mappings (Output → Input)")]
    public Map[] Mappings = Array.Empty<Map>();

    [Header("Options")]
    [Tooltip("Pause bridging without removing the component.")]
    public bool BridgeEnabled = true;

    void OnEnable()
    {
        // Put each target under override control and initialize its override value.
        for (int i = 0; i < Mappings.Length; i++)
        {
            var t = Mappings[i].Target;
            if (t == null) continue;

            t.Settings.Override = true;

            bool val = (Mappings[i].Source != null) ? Mappings[i].Source.Value : false;
            if (Mappings[i].Invert) val = !val;

            // IMPORTANT: drive the override slot, not Value
            t.Status.ValueOverride = val;
        }
    }

    void OnDisable()
    {
        // Release overrides cleanly (optional).
        for (int i = 0; i < Mappings.Length; i++)
        {
            var t = Mappings[i].Target;
            if (t != null) t.Settings.Override = false;
        }
    }

    void FixedUpdate()
    {
        if (!BridgeEnabled) return;

        for (int i = 0; i < Mappings.Length; i++)
        {
            var src = Mappings[i].Source;
            var dst = Mappings[i].Target;
            if (dst == null) continue;

            // Keep override asserted so we control the input
            if (!dst.Settings.Override) dst.Settings.Override = true;

            bool val = (src != null) ? src.Value : false;
            if (Mappings[i].Invert) val = !val;

            // Only write when changed to avoid churn
            if (dst.Status.ValueOverride != val)
                dst.Status.ValueOverride = val;
        }
    }
}
