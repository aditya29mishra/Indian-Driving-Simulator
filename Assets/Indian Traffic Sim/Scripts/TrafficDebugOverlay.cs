using UnityEngine;

public class TrafficDebugOverlay : MonoBehaviour
{
    private TrafficVehicle[] vehicles = new TrafficVehicle[0];
    private float cacheTimer;
    private bool overlayVisible = true;

    void Update()
    {
        cacheTimer += Time.deltaTime;
        if (cacheTimer >= 0.5f)
        {
            cacheTimer = 0f;
            vehicles = Object.FindObjectsOfType<TrafficVehicle>();
        }
        if (Input.GetKeyDown(KeyCode.D))
            overlayVisible = !overlayVisible;
    }

    void OnGUI()
    {
        if (!overlayVisible) return;
        if (Camera.main == null) return;

        foreach (var v in vehicles)
        {
            if (v == null) continue;

            Vector3 worldPos = v.transform.position + Vector3.up * 3f;
            Vector3 screen = Camera.main.WorldToScreenPoint(worldPos);
            if (screen.z <= 0f) continue;

            float y = Screen.height - screen.y;
            string leaderStr = v.leaderVehicle != null ? v.leaderVehicle.name : "free";
            string signalStr = v.currentSignal != null && v.currentLane != null
                ? v.currentSignal.GetStateForLane(v.currentLane).ToString()
                : "no signal";
            string lcStr = v.IsChangingLane ? " (lane change)" : "";

            string text = v.name + " " + v.CurrentSpeed.ToString("F1") + " " + v.driverProfile
                + " " + leaderStr + " " + signalStr + lcStr;

            bool stoppedForSignal = v.currentSignal != null && v.currentLane != null
                && v.currentSignal.GetStateForLane(v.currentLane) != TrafficSignal.SignalState.Green
                && v.CurrentSpeed < 0.1f;
            bool following = v.leaderVehicle != null;

            Color c = stoppedForSignal ? Color.red : (following ? Color.yellow : Color.green);
            var prev = GUI.color;
            GUI.color = c;
            GUI.Label(new Rect(screen.x, y, 400f, 24f), text);
            GUI.color = prev;
        }
    }
}
