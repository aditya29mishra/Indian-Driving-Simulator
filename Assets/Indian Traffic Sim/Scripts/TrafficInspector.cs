using UnityEngine;
using System.Collections.Generic;

public class TrafficInspector : MonoBehaviour
{
    private TrafficVehicle selectedVehicle;
    private readonly GUIStyle labelStyle = new GUIStyle();
    private const float PanelW = 300f;
    private const float PanelH = 400f;

    void Start()
    {
        labelStyle.fontSize = 12;
        labelStyle.wordWrap = true;
    }

    void Update()
    {
        if (Input.GetMouseButtonDown(0) && Camera.main != null)
        {
            Ray r = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(r, out RaycastHit hit))
            {
                var v = hit.collider.GetComponentInParent<TrafficVehicle>();
                selectedVehicle = v != null ? v : selectedVehicle;
            }
        }
    }

    void OnGUI()
    {
        float x = Screen.width - PanelW - 10f;
        float y = Screen.height - PanelH - 10f;

        GUI.Box(new Rect(x, y, PanelW, PanelH), "");

        float row = y + 8f;
        float lineH = 18f;

        if (selectedVehicle == null)
        {
            GUI.Label(new Rect(x + 8f, row, PanelW - 16f, 40f), "Click a vehicle to inspect.", labelStyle);
            return;
        }

        TrafficVehicle v = selectedVehicle;
        string laneName = v.currentLane != null ? v.currentLane.name : "-";
        string leftName = v.leftLane != null ? v.leftLane.name : "-";
        string rightName = v.rightLane != null ? v.rightLane.name : "-";
        string leaderName = v.leaderVehicle != null ? v.leaderVehicle.name : "-";
        float leaderDist = v.leaderVehicle != null ? Vector3.Distance(v.transform.position, v.leaderVehicle.transform.position) : 0f;
        string signalStr = v.currentSignal != null && v.currentLane != null
            ? v.currentSignal.GetStateForLane(v.currentLane).ToString()
            : "no signal";
        string pathName = v.CurrentPath != null ? v.CurrentPath.name : "-";

        DrawLine(ref row, x, lineH, "Name", v.name);
        DrawLine(ref row, x, lineH, "Position", v.transform.position.ToString("F1"));
        DrawLine(ref row, x, lineH, "Speed", v.CurrentSpeed.ToString("F1"));
        DrawLine(ref row, x, lineH, "Desired", v.DesiredSpeed.ToString("F1"));
        DrawLine(ref row, x, lineH, "Max", v.MaxSpeed.ToString("F1"));
        DrawLine(ref row, x, lineH, "Profile", v.driverProfile.ToString());
        DrawLine(ref row, x, lineH, "Lane", laneName);
        DrawLine(ref row, x, lineH, "Left", leftName);
        DrawLine(ref row, x, lineH, "Right", rightName);
        DrawLine(ref row, x, lineH, "Leader", leaderName + " " + leaderDist.ToString("F1"));
        DrawLine(ref row, x, lineH, "Signal", signalStr);
        DrawLine(ref row, x, lineH, "Dist", v.DistanceTravelled.ToString("F1"));
        DrawLine(ref row, x, lineH, "Path", pathName);
        DrawLine(ref row, x, lineH, "LaneChange", v.IsChangingLane.ToString());
        DrawLine(ref row, x, lineH, "LC Progress", v.LaneChangeProgress.ToString("F2"));

        row += 8f;
        GUI.Label(new Rect(x + 8f, row, PanelW - 16f, lineH), "Last 10 log entries:", labelStyle);
        row += lineH;

        if (TrafficEventLogger.Instance != null)
        {
            List<string> logs = TrafficEventLogger.Instance.GetRecentEntriesForVehicle(v.name, 10);
            foreach (string log in logs)
            {
                GUI.Label(new Rect(x + 8f, row, PanelW - 16f, 36f), log, labelStyle);
                row += 36f;
            }
        }

        if (GUI.Button(new Rect(x + PanelW - 48f, y + 4f, 40f, 24f), "X"))
            selectedVehicle = null;
    }

    void DrawLine(ref float row, float x, float lineH, string label, string value)
    {
        GUI.Label(new Rect(x + 8f, row, 80f, lineH), label + ":", labelStyle);
        GUI.Label(new Rect(x + 88f, row, PanelW - 96f, lineH), value, labelStyle);
        row += lineH;
    }
}