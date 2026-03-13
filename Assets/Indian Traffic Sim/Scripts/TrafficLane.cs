using System.Collections.Generic;
using UnityEngine;

public class TrafficLane : MonoBehaviour
{
    public TrafficRoad road;

    public int laneIndex = 0;

    public float laneWidth = 3.5f;

    public bool forwardDirection = true;

    public TrafficPath path;

    public List<TrafficLane> nextLanes = new List<TrafficLane>();

    private void OnDrawGizmos()
    {
        if (path == null || path.waypoints.Count == 0)
            return;

        Vector3 endPoint = path.waypoints[path.waypoints.Count - 1].position;

        Gizmos.color = new Color(1f,0.6f,0f); // orange

        foreach (var next in nextLanes)
        {
            if (next == null || next.path == null || next.path.waypoints.Count == 0)
                continue;

            Vector3 startNext = next.path.waypoints[0].position;

            Gizmos.DrawLine(endPoint, startNext);

            DrawArrow(endPoint, startNext);
        }
    }

    void DrawArrow(Vector3 from, Vector3 to)
    {
        Vector3 dir = (to - from).normalized;

        Vector3 mid = Vector3.Lerp(from, to, 0.5f);

        Vector3 right = Quaternion.LookRotation(dir) * Quaternion.Euler(0, 160, 0) * Vector3.forward;
        Vector3 left  = Quaternion.LookRotation(dir) * Quaternion.Euler(0, -160, 0) * Vector3.forward;

        Gizmos.DrawLine(mid, mid + right * 1.2f);
        Gizmos.DrawLine(mid, mid + left * 1.2f);
    }
}