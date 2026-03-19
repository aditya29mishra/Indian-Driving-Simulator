using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// IntersectionConnector — Bezier turn arc generation between approach lanes.
//
// Lane direction detection: position-based.
//   Arriving lane  = waypoints[last] closer to intersection centre
//   Departing lane = waypoints[0]    closer to intersection centre
//
// Lane pairing: purely index-based.
//   Lanes in each set are sorted by laneIndex ascending (RoadGenerator order).
//   Two booleans control the pairing:
//     flipArriving  — reverses arriving set order  (driven by arriving forwardDirection)
//     flipDeparting — reverses departing set order  (driven by departing forwardDirection)
//   Final: arriving[effectiveI] → departing[effectiveJ]
//
// Talks to: TrafficIntersectionNode, TrafficRoad, TrafficLane, TrafficPath
// ─────────────────────────────────────────────────────────────────────────────

public class IntersectionConnector : MonoBehaviour
{
    [Header("Intersection Node")]
    [Tooltip("The TrafficIntersectionNode at the centre of this junction.")]
    public TrafficIntersectionNode intersectionNode;

    [Header("Arc Settings")]
    public float turnRadius = 6f;
    public int   resolution = 12;

    // ─────────────────────────────────────────────────────────────────────
    // Generation
    // ─────────────────────────────────────────────────────────────────────

    [ContextMenu("Generate Intersection")]
    public void AutoGenerate()
    {
        if (intersectionNode == null)
        {
            Debug.LogWarning("[IntersectionConnector] intersectionNode not assigned.");
            return;
        }

        var roads = intersectionNode.connectedRoads;
        if (roads.Count < 2)
        {
            Debug.LogError($"[IntersectionConnector] '{intersectionNode.name}' has only " +
                           $"{roads.Count} road(s). Need at least 2.");
            return;
        }

        int expected = intersectionNode.ExpectedRoadCount();
        if (expected > 0 && roads.Count != expected)
            Debug.LogWarning($"[IntersectionConnector] '{intersectionNode.name}' is " +
                             $"{intersectionNode.intersectionType} (expects {expected} roads) " +
                             $"but has {roads.Count}. Generating anyway.");

        ValidateRoadOrientations(roads);
        ClearOld();
        GenerateFromRoads(roads);

        int arcCount = roads.Count * (roads.Count - 1);
        Debug.Log($"[IntersectionConnector] '{intersectionNode.name}' " +
                  $"({intersectionNode.intersectionType}) — " +
                  $"{roads.Count} arms, {arcCount} arcs generated.");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Validation
    // ─────────────────────────────────────────────────────────────────────

    void ValidateRoadOrientations(List<TrafficRoad> roads)
    {
        Vector3 centre = intersectionNode.transform.position;
        bool allOk = true;

        foreach (var road in roads)
        {
            if (road == null) continue;
            var arriving  = GetArrivingLanes(road,  centre);
            var departing = GetDepartingLanes(road, centre);

            if (arriving.Count == 0)
            {
                Debug.LogWarning($"[IntersectionConnector] '{road.name}' has NO arriving lanes " +
                                 $"at '{intersectionNode.name}'. Check road orientation.");
                allOk = false;
            }
            if (departing.Count == 0)
            {
                Debug.LogWarning($"[IntersectionConnector] '{road.name}' has NO departing lanes " +
                                 $"at '{intersectionNode.name}'. Check road orientation.");
                allOk = false;
            }
            if (arriving.Count > 0 && departing.Count > 0 &&
                arriving.Count != departing.Count)
            {
                Debug.LogWarning($"[IntersectionConnector] '{road.name}': " +
                                 $"{arriving.Count} arriving but {departing.Count} departing. " +
                                 $"Check forwardLanes == backwardLanes in TrafficRoad.");
                allOk = false;
            }
        }

        if (allOk)
            Debug.Log($"[IntersectionConnector] Validation passed at '{intersectionNode.name}'.");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Core generation
    // ─────────────────────────────────────────────────────────────────────

    void GenerateFromRoads(List<TrafficRoad> roads)
    {
        Vector3 centre = intersectionNode.transform.position;
        for (int i = 0; i < roads.Count; i++)
            for (int j = 0; j < roads.Count; j++)
            {
                if (i == j) continue;
                ConnectRoads(roads[i], roads[j], centre);
            }
    }

    void ConnectRoads(TrafficRoad inRoad, TrafficRoad outRoad, Vector3 centre)
    {
        if (inRoad == null || outRoad == null) return;

        var inLanes  = GetArrivingLanes(inRoad,  centre);
        var outLanes = GetDepartingLanes(outRoad, centre);
        if (inLanes.Count == 0 || outLanes.Count == 0) return;

        // Sort both sets by laneIndex ascending — consistent creation order from RoadGenerator
        inLanes.Sort((a, b)  => a.laneIndex.CompareTo(b.laneIndex));
        outLanes.Sort((a, b) => a.laneIndex.CompareTo(b.laneIndex));

        // Bool 1 — flipArriving:  reverse the arriving set order
        // Bool 2 — flipDeparting: reverse the departing set order
        // Both driven by forwardDirection of their respective set's first lane.
        bool flipArriving  = inLanes.Count  > 0 && inLanes[0].forwardDirection;
        bool flipDeparting = outLanes.Count > 0 && outLanes[0].forwardDirection;

        int count = Mathf.Min(inLanes.Count, outLanes.Count);
        for (int i = 0; i < count; i++)
        {
            int inIdx  = flipArriving  ? inLanes.Count  - 1 - i : i;
            int outIdx = flipDeparting ? outLanes.Count - 1 - i : i;
            CreateCurve(inLanes[inIdx], outLanes[outIdx]);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Position-based lane classification
    // ─────────────────────────────────────────────────────────────────────

    public List<TrafficLane> GetArrivingLanes(TrafficRoad road, Vector3 intersectionCentre)
    {
        var result = new List<TrafficLane>();
        foreach (var lane in road.lanes)
        {
            if (lane?.path == null || lane.path.waypoints == null ||
                lane.path.waypoints.Count < 2) continue;
            Vector3 wp0   = lane.path.waypoints[0].position;
            Vector3 wpEnd = lane.path.waypoints[lane.path.waypoints.Count - 1].position;
            if (Vector3.Distance(wpEnd, intersectionCentre) <
                Vector3.Distance(wp0,   intersectionCentre))
                result.Add(lane);
        }
        return result;
    }

    public List<TrafficLane> GetDepartingLanes(TrafficRoad road, Vector3 intersectionCentre)
    {
        var result = new List<TrafficLane>();
        foreach (var lane in road.lanes)
        {
            if (lane?.path == null || lane.path.waypoints == null ||
                lane.path.waypoints.Count < 2) continue;
            Vector3 wp0   = lane.path.waypoints[0].position;
            Vector3 wpEnd = lane.path.waypoints[lane.path.waypoints.Count - 1].position;
            if (Vector3.Distance(wp0,   intersectionCentre) <
                Vector3.Distance(wpEnd, intersectionCentre))
                result.Add(lane);
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Bezier arc creation
    // ─────────────────────────────────────────────────────────────────────

    void CreateCurve(TrafficLane fromLane, TrafficLane toLane)
    {
        if (fromLane?.path == null || fromLane.path.waypoints.Count == 0) return;
        if (toLane?.path   == null || toLane.path.waypoints.Count   == 0) return;

        Vector3 start    = fromLane.path.waypoints[fromLane.path.waypoints.Count - 1].position;
        Vector3 end      = toLane.path.waypoints[0].position;
        Vector3 dirStart = GetPathTipDirection(fromLane.path, atEnd: true);
        Vector3 dirEnd   = GetPathTipDirection(toLane.path,   atEnd: false);

        Vector3 p0 = start,  p1 = start + dirStart * turnRadius;
        Vector3 p3 = end,    p2 = end   - dirEnd   * turnRadius;

        var obj              = new GameObject($"Turn_{fromLane.name}_TO_{toLane.name}");
        obj.transform.parent = transform;

        var path  = obj.AddComponent<TrafficPath>();
        path.road = fromLane.road;
        path.lane = toLane;

        for (int i = 0; i <= resolution; i++)
        {
            float   t  = i / (float)resolution;
            float   t1 = 1f - t;
            Vector3 pt = t1*t1*t1*p0 + 3f*t1*t1*t*p1 + 3f*t1*t*t*p2 + t*t*t*p3;

            var wp               = new GameObject($"TurnWP_{i}");
            wp.transform.position = pt;
            wp.transform.parent  = obj.transform;
            path.waypoints.Add(wp.transform);
        }

        fromLane.nextPaths.Add(new TrafficLane.LanePath { path = path, targetLane = toLane });
    }

    Vector3 GetPathTipDirection(TrafficPath path, bool atEnd)
    {
        var wps = path.waypoints;
        if (wps.Count < 2) return Vector3.forward;
        return atEnd
            ? (wps[wps.Count - 1].position - wps[wps.Count - 2].position).normalized
            : (wps[1].position             - wps[0].position).normalized;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Cleanup
    // ─────────────────────────────────────────────────────────────────────

    void ClearOld()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);

        if (intersectionNode == null) return;
        foreach (var road in intersectionNode.connectedRoads)
        {
            if (road == null) continue;
            foreach (var lane in road.lanes)
                if (lane != null) lane.nextPaths.Clear();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Gizmo
    // ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (intersectionNode == null) return;

        Vector3 centre = intersectionNode.transform.position + Vector3.up * 0.5f;
        Gizmos.color   = new Color(1f, 0.9f, 0f, 0.9f);
        Gizmos.DrawWireSphere(centre, 1.0f);

        foreach (var road in intersectionNode.connectedRoads)
        {
            if (road == null) continue;
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.5f);
            Gizmos.DrawLine(centre, road.transform.position + Vector3.up * 0.5f);
            Gizmos.DrawWireSphere(road.transform.position + Vector3.up * 0.5f, 0.4f);
        }
    }
#endif
}