using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficRoad — road data container + procedural lane/waypoint generator.
//
// Merged from TrafficRoad + RoadGenerator. One component does both:
//   — Holds road configuration (nodes, speed limit, lanes list)
//   — Generates child lane and waypoint GameObjects on demand
//
// Usage:
//   1. Add this component to a GameObject.
//   2. Assign startNode and endNode (TrafficRoadNode).
//   3. Set forwardLanes, backwardLanes, laneWidth, waypointSpacing.
//   4. Right-click → Generate Road  (or LaneManager → Generate All).
//
// Talks to: TrafficRoadNode (start/end), TrafficLane, TrafficPath, LaneManager
// ─────────────────────────────────────────────────────────────────────────────

[ExecuteAlways]
public class TrafficRoad : MonoBehaviour
{
    // ── Road nodes ────────────────────────────────────────────────────────
    [Header("Road Nodes")]
    [Tooltip("TrafficRoadNode at the start of this road segment.")]
    public TrafficRoadNode startNode;
    [Tooltip("TrafficRoadNode at the end of this road segment.")]
    public TrafficRoadNode endNode;
    [Tooltip("The TrafficIntersectionNode this road feeds into. " +
             "Used by TrafficGraph to build road network connectivity.")]
    public TrafficIntersectionNode intersectionNode;

    // ── Road properties ───────────────────────────────────────────────────
    [Header("Road Properties")]
    public float roadWidth  = 8f;
    public float speedLimit = 50f;

    // ── Lane generation ───────────────────────────────────────────────────
    [Header("Lane Generation")]
    public int   forwardLanes    = 3;
    public int   backwardLanes   = 3;
    public float laneWidth       = 3.5f;
    public float waypointSpacing = 8f;

    // ── Guide visualisation ───────────────────────────────────────────────
    [Header("Guide Visualisation")]
    public int guideCount = 3;

    [HideInInspector] public List<Vector3> startGuides = new List<Vector3>();
    [HideInInspector] public List<Vector3> endGuides   = new List<Vector3>();

    // ── Lanes — auto-populated by GenerateRoad ────────────────────────────
    [Header("Lanes (auto-populated)")]
    public List<TrafficLane> lanes = new List<TrafficLane>();

    // ─────────────────────────────────────────────────────────────────────
    // Node registration
    // ─────────────────────────────────────────────────────────────────────

    private void OnEnable()  { RegisterWithNodes();   }
    private void OnDisable() { UnregisterFromNodes(); }

    void RegisterWithNodes()
    {
        if (startNode != null) startNode.RegisterRoad(this);
        if (endNode   != null) endNode.RegisterRoad(this);
    }

    void UnregisterFromNodes()
    {
        if (startNode != null) startNode.RemoveRoad(this);
        if (endNode   != null) endNode.RemoveRoad(this);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Road generation
    // ─────────────────────────────────────────────────────────────────────

    [ContextMenu("Generate Road")]
    public void GenerateRoad()
    {
        if (startNode == null || endNode == null)
        {
            Debug.LogError($"[TrafficRoad] '{name}': startNode or endNode not assigned.");
            return;
        }

        string roadId = $"R{GetInstanceID()}";

        // Clear existing child lane / path / waypoint GameObjects
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);

        lanes.Clear();

        Vector3 start     = startNode.transform.position;
        Vector3 end       = endNode.transform.position;
        Vector3 direction = (end - start).normalized;
        Vector3 right     = Vector3.Cross(Vector3.up, direction);

        int totalLanes = forwardLanes + backwardLanes;

        for (int i = 0; i < totalLanes; i++)
        {
            bool isForward  = i < forwardLanes;
            int  groupIndex = isForward ? i : i - forwardLanes;
            int  groupSize  = isForward ? forwardLanes : backwardLanes;

            float offsetFromCenter = (groupIndex - (groupSize - 1) * 0.5f) * laneWidth;
            float groupShift       = (isForward ? -1 : 1) * (laneWidth * groupSize * 0.5f);
            float finalOffset      = offsetFromCenter + groupShift;

            Vector3 laneStart = start + right * finalOffset;
            Vector3 laneEnd   = end   + right * finalOffset;

            if (!isForward)
            {
                Vector3 tmp = laneStart;
                laneStart   = laneEnd;
                laneEnd     = tmp;
            }

            // Lane — named by road name + lane index (no F/B direction tag)
            var laneObj              = new GameObject($"Lane_{name}_{i}");
            laneObj.transform.parent = transform;
            var lane                 = laneObj.AddComponent<TrafficLane>();
            lane.laneIndex           = i;
            lane.forwardDirection    = isForward;
            lane.road                = this;
            lanes.Add(lane);

            // Path
            var pathObj              = new GameObject($"Path_{name}_{i}");
            pathObj.transform.parent = laneObj.transform;
            var path                 = pathObj.AddComponent<TrafficPath>();
            path.road                = this;
            path.lane                = lane;
            lane.path                = path;

            // Waypoints
            float distance      = Vector3.Distance(laneStart, laneEnd);
            int   waypointCount = Mathf.CeilToInt(distance / waypointSpacing);

            for (int w = 0; w <= waypointCount; w++)
            {
                float   t   = w / (float)waypointCount;
                Vector3 pos = Vector3.Lerp(laneStart, laneEnd, t);

                var wp               = new GameObject($"WP_{name}_{i}_{w}");
                wp.transform.position = pos;
                wp.transform.parent  = pathObj.transform;
                path.waypoints.Add(wp.transform);
            }
        }

        Debug.Log($"[TrafficRoad] '{name}': generated {totalLanes} lanes " +
                  $"({forwardLanes}F + {backwardLanes}B).");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Guide points — visualisation only
    // ─────────────────────────────────────────────────────────────────────

    public void GenerateGuides()
    {
        startGuides.Clear();
        endGuides.Clear();

        if (startNode == null || endNode == null) return;

        Vector3 start = startNode.transform.position;
        Vector3 end   = endNode.transform.position;
        Vector3 dir   = (end - start).normalized;
        Vector3 right = Vector3.Cross(Vector3.up, dir);

        float step = guideCount > 1 ? roadWidth / (guideCount - 1) : 0f;

        for (int i = 0; i < guideCount; i++)
        {
            float offset = -roadWidth * 0.5f + step * i;
            startGuides.Add(start + right * offset);
            endGuides.Add(end     + right * offset);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Gizmos
    // ─────────────────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (startNode == null || endNode == null) return;

        Vector3 start = startNode.transform.position;
        Vector3 end   = endNode.transform.position;
        Vector3 dir   = (end - start).normalized;
        Vector3 right = Vector3.Cross(Vector3.up, dir);

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(start, end);

        Gizmos.color = Color.blue;
        Gizmos.DrawLine(start - right * roadWidth * 0.5f, end - right * roadWidth * 0.5f);
        Gizmos.DrawLine(start + right * roadWidth * 0.5f, end + right * roadWidth * 0.5f);

        GenerateGuides();
        Gizmos.color = Color.yellow;
        for (int i = 0; i < startGuides.Count; i++)
        {
            Gizmos.DrawSphere(startGuides[i], 0.25f);
            Gizmos.DrawSphere(endGuides[i],   0.25f);
            Gizmos.DrawLine(startGuides[i], endGuides[i]);
        }
    }
}