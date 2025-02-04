using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneTransitionManager : MonoBehaviour
{
    public FadeScreen fadeScreen; // Reference to the FadeScreen component
    public static SceneTransitionManager singleton; // Singleton instance

    private void Awake()
    {
        // Ensure only one instance of SceneTransitionManager exists
        if (singleton != null && singleton != this)
        {
            Destroy(gameObject); // Destroy duplicate instances
            return;
        }

        singleton = this;
        DontDestroyOnLoad(gameObject); // Persist across scene loads
    }

    // Method to transition to a scene by index
    public void GoToScene(int sceneIndex)
    {
        StartCoroutine(GoToSceneRoutine(sceneIndex));
    }

    // Coroutine for scene transition with fade
    private IEnumerator GoToSceneRoutine(int sceneIndex)
    {
        // Fade out the screen
        fadeScreen.FadeOut();
        yield return new WaitForSeconds(fadeScreen.fadeDuration);

        // Load the new scene
        SceneManager.LoadScene(sceneIndex);
    }

    // Method to transition to a scene asynchronously by index
    public void GoToSceneAsync(int sceneIndex)
    {
        StartCoroutine(GoToSceneAsyncRoutine(sceneIndex));
    }

    // Coroutine for asynchronous scene transition with fade
    private IEnumerator GoToSceneAsyncRoutine(int sceneIndex)
    {
        // Fade out the screen
        fadeScreen.FadeOut();
        yield return new WaitForSeconds(fadeScreen.fadeDuration);

        // Load the new scene asynchronously
        AsyncOperation operation = SceneManager.LoadSceneAsync(sceneIndex);
        operation.allowSceneActivation = false; // Prevent automatic scene activation

        // Wait for the fade duration and the async operation to complete
        float timer = 0;
        while (timer <= fadeScreen.fadeDuration || operation.progress < 0.9f)
        {
            timer += Time.deltaTime;
            yield return null;
        }

        // Activate the new scene
        operation.allowSceneActivation = true;
    }
}