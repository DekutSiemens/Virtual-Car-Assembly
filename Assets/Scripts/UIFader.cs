using UnityEngine;
using UnityEngine.UI;
using System.Collections;

public class UIFader : MonoBehaviour
{
    // Public variables to control fade behavior
    public float fadeDuration = 1f;
    public bool startHidden = false;

    // New public variable for target alpha
    [Range(0f, 1f)]
    public float targetFadeAlpha = 1f;

    // Reference to UI components
    private Graphic[] uiGraphics;

    void Start()
    {
        // Get all Graphic components (works for Image, Text, RawImage, etc.)
        uiGraphics = GetComponentsInChildren<Graphic>();

        // Validate graphics found
        if (uiGraphics.Length == 0)
        {
            Debug.LogError("No UI Graphics found on object or its children!");
            return;
        }

        // Start with initial visibility state
        if (startHidden)
        {
            SetAlpha(0f);
        }
    }

    // Set alpha value for all UI graphics
    void SetAlpha(float alpha)
    {
        if (uiGraphics == null) return;

        foreach (Graphic graphic in uiGraphics)
        {
            Color color = graphic.color;
            color.a = alpha;
            graphic.color = color;
        }
    }

    // Fade the UI element in to the target alpha
    public void FadeIn()
    {
        StartCoroutine(FadeRoutine(0f, targetFadeAlpha));
    }

    // Fade the UI element out
    public void FadeOut()
    {
        StartCoroutine(FadeRoutine(targetFadeAlpha, 0f));
    }

    // Coroutine to handle smooth fading
    IEnumerator FadeRoutine(float startAlpha, float endAlpha)
    {
        float elapsedTime = 0f;

        while (elapsedTime < fadeDuration)
        {
            // Calculate current alpha
            float currentAlpha = Mathf.Lerp(startAlpha, endAlpha, elapsedTime / fadeDuration);
            SetAlpha(currentAlpha);

            // Increment time
            elapsedTime += Time.deltaTime;
            yield return null;
        }

        // Ensure we end at the exact target alpha
        SetAlpha(endAlpha);

        // Optional: Disable gameobject when fully faded out
        if (endAlpha == 0f)
        {
            //gameObject.SetActive(false);
        }
    }

    // Additional method to fade in and make active
    public void ShowWithFade()
    {
        gameObject.SetActive(true);
        FadeIn();
    }

    // Additional method to fade out and disable
    public void HideWithFade()
    {
        FadeOut();
    }
}