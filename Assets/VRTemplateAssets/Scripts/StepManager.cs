using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace Unity.VRTemplate
{
    /// <summary>
    /// Controls the steps in the in coaching card.
    /// </summary>
    public class StepManager : MonoBehaviour
    {
        [System.Serializable]
        public class Step
        {
            [SerializeField]
            public GameObject stepObject; // The GameObject associated with this step

            [SerializeField]
            public string buttonText; // The text to display on the button for this step

            [SerializeField]
            public UnityEvent onStepActivated; // Custom action to execute when this step is activated
        }

        [SerializeField]
        private TextMeshProUGUI m_StepButtonTextField; // Reference to the button's text field

        [SerializeField]
        private List<Step> m_StepList = new List<Step>(); // List of steps

        private int m_CurrentStepIndex = 0; // Index of the current step

        /// <summary>
        /// Moves to the next step in the sequence.
        /// </summary>
        public void Next()
        {
            // Deactivate the current step
            m_StepList[m_CurrentStepIndex].stepObject.SetActive(false);

            // Move to the next step
            m_CurrentStepIndex = (m_CurrentStepIndex + 1) % m_StepList.Count;

            // Activate the next step
            m_StepList[m_CurrentStepIndex].stepObject.SetActive(true);

            // Update the button text
            m_StepButtonTextField.text = m_StepList[m_CurrentStepIndex].buttonText;

            // Execute the custom action for the next step
            m_StepList[m_CurrentStepIndex].onStepActivated?.Invoke();
        }

        /// <summary>
        /// Resets the step sequence to the first step.
        /// </summary>
        public void Reset()
        {
            // Deactivate all steps except the first one
            for (int i = 1; i < m_StepList.Count; i++)
            {
                m_StepList[i].stepObject.SetActive(false);
            }

            // Reset the current step index to the first step
            m_CurrentStepIndex = 0;

            // Activate the first step
            m_StepList[m_CurrentStepIndex].stepObject.SetActive(true);

            // Update the button text to the first step's text
            m_StepButtonTextField.text = m_StepList[m_CurrentStepIndex].buttonText;

            // Execute the custom action for the first step
            m_StepList[m_CurrentStepIndex].onStepActivated?.Invoke();
        }
    }
}