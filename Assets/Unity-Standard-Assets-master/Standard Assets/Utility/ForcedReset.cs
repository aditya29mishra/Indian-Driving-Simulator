using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public class ForcedReset : MonoBehaviour
{
    private void Update()
    {
        // Check for a button press to reset the scene.
        // For example, using the 'R' key.
        if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
        {
            // Reload the current scene
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
        }
    }
}