using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficRoad — road data container + spline-based lane generator.
//
// Spline system:
//   Road centre = Catmull-Rom through startNode → controlPoints → endNode.
//   Same algorithm as TrafficPath. Smooth, passes through every point exactly.
//   No handles — just drag the yellow spheres.
//
//   LIVE REGENERATION is handled by TrafficRoadEditor (Editor/ folder).
//   The editor polls control point positions every SceneView frame and calls
//   GenerateRoad() the moment any point moves. This is the correct Unity pattern
//   — ExecuteAlways alone cannot detect child transform changes.
//
// Zero control points → straight road (original behaviour, nothing breaks).
//
// Talks to: TrafficRoadNode, TrafficLane, TrafficPath, LaneManager
// ─────────────────────────────────────────────────────────────────────────────

[ExecuteAlways]
public class TrafficRoad : MonoBehaviour
{
    // ── Road nodes ────────────────────────────────────────────────────────
    [Header("Road Nodes")]
    public TrafficRoadNode         startNode;
    public TrafficRoadNode         endNode;
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

    // ── Spline ────────────────────────────────────────────────────────────
    [Header("Road Spline")]
    [Tooltip("Right-click → Add Control Point. Drag yellow spheres to curve the road. " +
             "The road regenerates live as you drag.")]
    [HideInInspector]
    public List<Transform> controlPoints = new List<Transform>();

    [Tooltip("Spline samples per segment between knots. 12 is smooth for most roads.")]
    public int splineSamplesPerSegment = 12;

    // ── Guide visualisation ───────────────────────────────────────────────
    [Header("Guide Visualisation")]
    public int guideCount = 3;

    [HideInInspector] public List<Vector3> startGuides = new List<Vector3>();
    [HideInInspector] public List<Vector3> endGuides   = new List<Vector3>();

    // ── Lanes (auto-populated) ────────────────────────────────────────────
    [Header("Lanes (auto-populated)")]
    public List<TrafficLane> lanes = new List<TrafficLane>();

    // ─────────────────────────────────────────────────────────────────────
    // Node registration
    // ─────────────────────────────────────────────────────────────────────

    void OnEnable()  => RegisterWithNodes();
    void OnDisable() => UnregisterFromNodes();

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
    // Control point management
    // ─────────────────────────────────────────────────────────────────────

    [ContextMenu("Add Control Point")]
    public void AddControlPoint()
    {
        if (startNode == null || endNode == null)
        {
            Debug.LogWarning("[TrafficRoad] Assign startNode and endNode first.");
            return;
        }

        // Place at midpoint of current spline so existing shape is not disturbed
        var spline  = BuildCentreSpline();
        Vector3 pos = spline.Count > 1
            ? spline[spline.Count / 2]
            : (startNode.transform.position + endNode.transform.position) * 0.5f;

        var go               = new GameObject($"CP_{name}_{controlPoints.Count}");
        go.transform.position = pos;
        go.transform.parent  = transform;

        var cp  = go.AddComponent<RoadControlPoint>();
        cp.road = this;

        controlPoints.Add(go.transform);

        Debug.Log($"[TrafficRoad] Control point added. Drag the yellow sphere to curve the road.");
    }

    [ContextMenu("Remove Last Control Point")]
    public void RemoveLastControlPoint()
    {
        if (controlPoints.Count == 0) return;
        var last = controlPoints[controlPoints.Count - 1];
        controlPoints.RemoveAt(controlPoints.Count - 1);
        if (last != null) DestroyImmediate(last.gameObject);
    }

    [ContextMenu("Resync Control Points From Children")]
    public void ResyncControlPoints()
    {
        controlPoints.Clear();
        foreach (Transform child in transform)
            if (child.GetComponent<RoadControlPoint>() != null)
                controlPoints.Add(child);
        Debug.Log($"[TrafficRoad] Resynced — {controlPoints.Count} control point(s).");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Centre spline
    // Catmull-Rom: startNode → control points → endNode
    // Same formula as TrafficPath for consistency.
    // ─────────────────────────────────────────────────────────────────────

    public List<Vector3> BuildCentreSpline()
    {
        var result = new List<Vector3>();
        if (startNode == null || endNode == null) return result;

        var knots = new List<Vector3>();
        knots.Add(startNode.transform.position);
        foreach (var cp in controlPoints)
            if (cp != null) knots.Add(cp.position);
        knots.Add(endNode.transform.position);

        if (knots.Count < 2) return result;

        // Straight line when no control points
        if (knots.Count == 2)
        {
            int n = Mathf.Max(2, splineSamplesPerSegment);
            for (int i = 0; i <= n; i++)
                result.Add(Vector3.Lerp(knots[0], knots[1], i / (float)n));
            return result;
        }

        // Catmull-Rom per segment, ghost-points at both ends
        for (int seg = 0; seg < knots.Count - 1; seg++)
        {
            Vector3 p0 = seg == 0
                ? knots[0] * 2f - knots[1]
                : knots[seg - 1];

            Vector3 p1 = knots[seg];
            Vector3 p2 = knots[seg + 1];

            Vector3 p3 = seg + 2 >= knots.Count
                ? knots[knots.Count - 1] * 2f - knots[knots.Count - 2]
                : knots[seg + 2];

            int n    = splineSamplesPerSegment;
            int endI = (seg == knots.Count - 2) ? n : n - 1;

            for (int i = 0; i <= endI; i++)
            {
                float t = i / (float)n;
                result.Add(CatmullRom(p0, p1, p2, p3, t));
            }
        }

        return result;
    }

    static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        return 0.5f * (
              2f * p1
            + (-p0 + p2) * t
            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * (t * t)
            + (-p0 + 3f * p1 - 3f * p2 + p3)     * (t * t * t));
    }

    public static Vector3 SplineTangent(List<Vector3> pts, int i)
    {
        if (pts.Count < 2) return Vector3.forward;
        Vector3 prev = pts[Mathf.Max(i - 1, 0)];
        Vector3 next = pts[Mathf.Min(i + 1, pts.Count - 1)];
        Vector3 dir  = next - prev;
        return dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.forward;
    }

    public static Vector3 RightAt(List<Vector3> pts, int i)
    {
        Vector3 t = SplineTangent(pts, i);
        return new Vector3(-t.z, 0f, t.x).normalized;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Road generation
    // ─────────────────────────────────────────────────────────────────────

    [ContextMenu("Generate Road")]
    public void GenerateRoad()
    {
        if (startNode == null || endNode == null)
        {
            Debug.LogError($"[TrafficRoad] '{name}': assign startNode and endNode first.");
            return;
        }

        // Destroy lane/path/waypoint children. Keep RoadControlPoint children.
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i);
            if (child.GetComponent<RoadControlPoint>() == null)
                DestroyImmediate(child.gameObject);
        }

        lanes.Clear();

        var centre = BuildCentreSpline();
        if (centre.Count < 2)
        {
            Debug.LogError($"[TrafficRoad] '{name}': spline produced < 2 points.");
            return;
        }

        int total = forwardLanes + backwardLanes;

        for (int i = 0; i < total; i++)
        {
            bool  isForward  = i < forwardLanes;
            int   groupIndex = isForward ? i : i - forwardLanes;
            int   groupSize  = isForward ? forwardLanes : backwardLanes;
            float offset     = (groupIndex - (groupSize - 1) * 0.5f) * laneWidth
                             + (isForward ? -1f : 1f) * laneWidth * groupSize * 0.5f;

            // Lane
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
            var pts = BuildLanePoints(centre, offset, isForward);
            for (int w = 0; w < pts.Count; w++)
            {
                var wp                = new GameObject($"WP_{name}_{i}_{w}");
                wp.transform.position = pts[w];
                wp.transform.parent   = pathObj.transform;
                path.waypoints.Add(wp.transform);
            }
        }

        Debug.Log($"[TrafficRoad] '{name}': {total} lanes ({forwardLanes}F+{backwardLanes}B), " +
                  $"{controlPoints.Count} CP(s).");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Lane point builder
    // Arc-length resamples the dense centre spline at waypointSpacing,
    // offsets each point by the perpendicular right vector.
    // ─────────────────────────────────────────────────────────────────────

    List<Vector3> BuildLanePoints(List<Vector3> centre, float lateralOffset, bool isForward)
    {
        // Cumulative arc-length table
        var arc = new float[centre.Count];
        arc[0] = 0f;
        for (int i = 1; i < centre.Count; i++)
            arc[i] = arc[i - 1] + Vector3.Distance(centre[i - 1], centre[i]);

        float totalLen = arc[arc.Length - 1];
        if (totalLen < 0.01f) return new List<Vector3>();

        int count  = Mathf.Max(2, Mathf.CeilToInt(totalLen / waypointSpacing));
        var result = new List<Vector3>(count + 1);

        for (int w = 0; w <= count; w++)
        {
            float target = (w / (float)count) * totalLen;

            // Binary search for segment
            int lo = 0, hi = centre.Count - 2;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (arc[mid + 1] < target) lo = mid + 1;
                else hi = mid;
            }

            float segLen = arc[lo + 1] - arc[lo];
            float t      = segLen > 0.0001f ? (target - arc[lo]) / segLen : 0f;

            Vector3 pt    = Vector3.Lerp(centre[lo], centre[lo + 1], t);
            Vector3 right = RightAt(centre, lo);

            result.Add(pt + right * lateralOffset);
        }

        if (!isForward) result.Reverse();
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Guide points
    // ─────────────────────────────────────────────────────────────────────

    public void GenerateGuides()
    {
        startGuides.Clear();
        endGuides.Clear();
        if (startNode == null || endNode == null) return;

        var centre = BuildCentreSpline();
        if (centre.Count < 2) return;

        Vector3 sRight = RightAt(centre, 0);
        Vector3 eRight = RightAt(centre, centre.Count - 1);
        float step = guideCount > 1 ? roadWidth / (guideCount - 1) : 0f;

        for (int i = 0; i < guideCount; i++)
        {
            float off = -roadWidth * 0.5f + step * i;
            startGuides.Add(centre[0]                   + sRight * off);
            endGuides.Add(centre[centre.Count - 1]      + eRight * off);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Gizmos — live preview
    // White = centre spline  Blue = road edges
    // Green = forward lanes  Red = backward lanes  Yellow = control points
    // ─────────────────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (startNode == null || endNode == null) return;
        var centre = BuildCentreSpline();
        if (centre.Count < 2) return;

        // Centre
        Gizmos.color = new Color(1f, 1f, 1f, 0.5f);
        for (int i = 0; i < centre.Count - 1; i++)
            Gizmos.DrawLine(centre[i] + Vector3.up * 0.05f,
                            centre[i + 1] + Vector3.up * 0.05f);

        // Edges
        DrawOffsetLine(centre, -roadWidth * 0.5f, Color.blue);
        DrawOffsetLine(centre,  roadWidth * 0.5f, Color.blue);

        // Lane centres
        int total = forwardLanes + backwardLanes;
        for (int i = 0; i < total; i++)
        {
            bool isForward  = i < forwardLanes;
            int  gi = isForward ? i : i - forwardLanes;
            int  gs = isForward ? forwardLanes : backwardLanes;
            float off = (gi - (gs - 1) * 0.5f) * laneWidth
                      + (isForward ? -1f : 1f) * laneWidth * gs * 0.5f;
            DrawOffsetLine(centre, off,
                isForward ? new Color(0f, 1f, 0f, 0.2f) : new Color(1f, 0.3f, 0.3f, 0.2f));
        }

        // Control points
        for (int i = 0; i < controlPoints.Count; i++)
        {
            var cp = controlPoints[i];
            if (cp == null) continue;
            Vector3 pos = cp.position + Vector3.up * 0.3f;

            Gizmos.color = new Color(1f, 0.85f, 0f, 0.95f);
            Gizmos.DrawSphere(pos, 0.55f);

            // Chain lines: start ─ CP0 ─ CP1 ─ ... ─ end
            Vector3 prev = i == 0
                ? startNode.transform.position + Vector3.up * 0.3f
                : controlPoints[i - 1].position + Vector3.up * 0.3f;

            Gizmos.color = new Color(1f, 0.85f, 0f, 0.35f);
            Gizmos.DrawLine(prev, pos);

            if (i == controlPoints.Count - 1)
            {
                Gizmos.color = new Color(1f, 0.85f, 0f, 0.35f);
                Gizmos.DrawLine(pos, endNode.transform.position + Vector3.up * 0.3f);
            }

#if UNITY_EDITOR
            UnityEditor.Handles.Label(pos + Vector3.up * 1.0f, $"CP{i}");
#endif
        }
    }

    void DrawOffsetLine(List<Vector3> centre, float offset, Color col)
    {
        Gizmos.color = col;
        for (int i = 0; i < centre.Count - 1; i++)
            Gizmos.DrawLine(centre[i]     + RightAt(centre, i)     * offset,
                            centre[i + 1] + RightAt(centre, i + 1) * offset);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// RoadControlPoint — marker component on control point GameObjects.
// Tags the child so GenerateRoad() doesn't destroy it.
// Holds a back-reference so the editor script can find the parent road.
// ─────────────────────────────────────────────────────────────────────────────

[ExecuteAlways]
public class RoadControlPoint : MonoBehaviour
{
    [HideInInspector] public TrafficRoad road;

    void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.85f, 0f, 0.45f);
        Gizmos.DrawSphere(transform.position, 0.4f);
    }
}