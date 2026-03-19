using UnityEngine;
using System.Collections.Generic;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficPath — Catmull-Rom spline, closest distance, point-at-distance queries
//
// Generates a smooth spline from waypoint transforms. Provides methods to
// get points along the path by normalized parameter or distance, and find
// the closest point on the path to a world position.
//
// Talks to: TrafficLane (lane), TrafficRoad (road)
// ─────────────────────────────────────────────────────────────────────────────

[ExecuteAlways]
public class TrafficPath : MonoBehaviour
{
    public TrafficRoad road;
    public TrafficLane lane;

    public List<Transform> waypoints = new List<Transform>();

    public int splineResolution = 10;

    private List<Vector3> splinePoints = new List<Vector3>();
    private float totalLength = -1f;

    /// <summary>Gets the list of spline points generated from waypoints.</summary>
    /// <returns>List of Vector3 spline points.</returns>
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
        if (splinePoints.Count > 1)
        {
            DrawArrow(splinePoints[splinePoints.Count - 2], splinePoints[splinePoints.Count - 1]);
        }
    }
    void DrawArrow(Vector3 from, Vector3 to)
    {
        Vector3 dir = (to - from).normalized;

        Vector3 mid = Vector3.Lerp(from, to, 0.5f);

        Vector3 right = Quaternion.LookRotation(dir) * Quaternion.Euler(0, 160, 0) * Vector3.forward;
        Vector3 left = Quaternion.LookRotation(dir) * Quaternion.Euler(0, -160, 0) * Vector3.forward;

        Gizmos.DrawLine(mid, mid + right * 1.2f);
        Gizmos.DrawLine(mid, mid + left * 1.2f);
    }
    void GenerateSpline()
    {
        splinePoints.Clear();
        totalLength = -1f;

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

        RecalculateTotalLength();
    }

    /// <summary>Gets a point on the spline at normalized parameter t (0-1).</summary>
    /// <param name="t">Normalized parameter along the path.</param>
    /// <returns>Position on the spline.</returns>
    public Vector3 GetPoint(float t)
    {
        GenerateSpline();

        if (splinePoints.Count == 0)
            return transform.position;

        int index = Mathf.Clamp(Mathf.RoundToInt(t * (splinePoints.Count - 1)), 0, splinePoints.Count - 1);

        return splinePoints[index];
    }

    /// <summary>Gets the total length of the spline path.</summary>
    public float TotalLength
    {
        get
        {
            if (splinePoints.Count == 0)
            {
                GenerateSpline();
            }

            if (totalLength < 0f)
            {
                RecalculateTotalLength();
            }

            return totalLength;
        }
    }

    /// <summary>Gets the point on the spline at the specified distance along the path.</summary>
    /// <param name="distance">Distance along the path in meters.</param>
    /// <returns>Position at the distance.</returns>
    public Vector3 GetPointAtDistance(float distance)
    {
        if (splinePoints.Count == 0)
        {
            GenerateSpline();
        }

        if (splinePoints.Count == 0)
            return transform.position;

        if (totalLength < 0f)
        {
            RecalculateTotalLength();
        }

        if (totalLength <= 0f)
            return splinePoints[0];

        float d = Mathf.Clamp(distance, 0f, totalLength);

        for (int i = 0; i < splinePoints.Count - 1; i++)
        {
            Vector3 a = splinePoints[i];
            Vector3 b = splinePoints[i + 1];
            float segLen = Vector3.Distance(a, b);

            if (segLen <= Mathf.Epsilon)
                continue;

            if (d > segLen)
            {
                d -= segLen;
                continue;
            }

            float t = d / segLen;
            return Vector3.Lerp(a, b, t);
        }

        return splinePoints[splinePoints.Count - 1];
    }

    /// <summary>Finds the distance along the path closest to the given world position.</summary>
    /// <param name="worldPos">World position to find closest distance for.</param>
    /// <returns>Distance along the path in meters.</returns>
    public float GetClosestDistance(Vector3 worldPos)
    {
        if (splinePoints.Count == 0)
        {
            GenerateSpline();
        }

        if (splinePoints.Count == 0)
            return 0f;

        if (totalLength < 0f)
        {
            RecalculateTotalLength();
        }

        float bestDistSq = float.MaxValue;
        float bestDistanceAlong = 0f;

        float accumulated = 0f;

        for (int i = 0; i < splinePoints.Count - 1; i++)
        {
            Vector3 a = splinePoints[i];
            Vector3 b = splinePoints[i + 1];
            Vector3 ab = b - a;
            float segLen = ab.magnitude;

            if (segLen <= Mathf.Epsilon)
                continue;

            Vector3 ap = worldPos - a;
            float t = Vector3.Dot(ap, ab) / (segLen * segLen);
            t = Mathf.Clamp01(t);

            Vector3 closest = a + ab * t;
            float distSq = (worldPos - closest).sqrMagnitude;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestDistanceAlong = accumulated + segLen * t;
            }

            accumulated += segLen;
        }

        return Mathf.Clamp(bestDistanceAlong, 0f, totalLength > 0f ? totalLength : bestDistanceAlong);
    }

    private void RecalculateTotalLength()
    {
        totalLength = 0f;

        if (splinePoints.Count < 2)
            return;

        for (int i = 0; i < splinePoints.Count - 1; i++)
        {
            totalLength += Vector3.Distance(splinePoints[i], splinePoints[i + 1]);
        }
    }
}