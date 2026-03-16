using UnityEngine;

public class TrafficSlowMo : MonoBehaviour
{
    private bool paused;
    private float previousTimeScale = 1f;

    void Update()
    {
        if (Input.GetKey(KeyCode.LeftBracket))
        {
            Time.timeScale = 0.1f;
            paused = false;
            return;
        }
        if (Input.GetKey(KeyCode.RightBracket))
        {
            Time.timeScale = 0.25f;
            paused = false;
            return;
        }
        if (Input.GetKeyDown(KeyCode.Space))
        {
            paused = !paused;
            if (paused)
            {
                previousTimeScale = Time.timeScale;
                Time.timeScale = 0f;
            }
            else
            {
                Time.timeScale = previousTimeScale > 0f ? previousTimeScale : 1f;
            }
            return;
        }
        if (paused && Input.GetKeyDown(KeyCode.F))
        {
            StartCoroutine(AdvanceOneFrame());
            return;
        }
        if (!paused)
        {
            Time.timeScale = 1f;
        }
    }

    System.Collections.IEnumerator AdvanceOneFrame()
    {
        Time.timeScale = 1f;
        yield return null;
        Time.timeScale = 0f;
    }

    void OnGUI()
    {
        GUI.Label(new Rect(10f, 10f, 200f, 24f), "Time scale: " + Time.timeScale.ToString("F2"));
    }
}
