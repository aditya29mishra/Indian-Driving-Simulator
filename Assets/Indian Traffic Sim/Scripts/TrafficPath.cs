using UnityEngine;
using System.Collections.Generic;

[ExecuteAlways]
public class TrafficPath : MonoBehaviour
{
    public TrafficRoad road;
    public TrafficLane lane;

    public List<Transform> waypoints = new List<Transform>();

    public int splineResolution = 10;

    private List<Vector3> splinePoints = new List<Vector3>();

    public List<Vector3> GetSplinePoints()
    {
        GenerateSpline();
        return splinePoints;
    }

    void OnDrawGizmos()
    {
        if (waypoints.Count < 2)
            return;

        GenerateSpline();

        Gizmos.color = lane != null && lane.forwardDirection ? Color.green : Color.red;

        for (int i = 0; i < splinePoints.Count - 1; i++)
        {
            Gizmos.DrawLine(splinePoints[i], splinePoints[i + 1]);
        }
    }

    void GenerateSpline()
    {
        splinePoints.Clear();

        if (waypoints.Count < 2)
            return;

        for (int i = 0; i < waypoints.Count - 1; i++)
        {
            Vector3 p0 = waypoints[Mathf.Max(i - 1, 0)].position;
            Vector3 p1 = waypoints[i].position;
            Vector3 p2 = waypoints[i + 1].position;
            Vector3 p3 = waypoints[Mathf.Min(i + 2, waypoints.Count - 1)].position;

            for (int j = 0; j < splineResolution; j++)
            {
                float t = j / (float)splineResolution;

                Vector3 point =
                    0.5f * (
                        (2f * p1) +
                        (-p0 + p2) * t +
                        (2f * p0 - 5f * p1 + 4f * p2 - p3) * t * t +
                        (-p0 + 3f * p1 - 3f * p2 + p3) * t * t * t
                    );

                splinePoints.Add(point);
            }
        }

        splinePoints.Add(waypoints[waypoints.Count - 1].position);
    }

    public Vector3 GetPoint(float t)
    {
        GenerateSpline();

        if (splinePoints.Count == 0)
            return transform.position;

        int index = Mathf.Clamp(Mathf.RoundToInt(t * (splinePoints.Count - 1)), 0, splinePoints.Count - 1);

        return splinePoints[index];
    }
}