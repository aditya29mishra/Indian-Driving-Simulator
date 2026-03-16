using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(TrafficPath))]
public class TrafficPathEditor : Editor
{
    void OnSceneGUI()
    {
        TrafficPath path = (TrafficPath)target;

        if (path.waypoints == null)
            return;

        for (int i = 0; i < path.waypoints.Count; i++)
        {
            if (path.waypoints[i] == null)
                continue;

            EditorGUI.BeginChangeCheck();

            Vector3 newPos = Handles.PositionHandle(
                path.waypoints[i].position,
                Quaternion.identity
            );

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(path.waypoints[i], "Move Waypoint");
                path.waypoints[i].position = newPos;
            }

            Handles.Label(path.waypoints[i].position + Vector3.up * 0.5f, "WP " + i);
        }
    }
}