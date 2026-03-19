using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficIntersectionNode — sits at the centre of a road junction.
//
// Role: marks how many road arms meet here and drives:
//   — IntersectionConnector arc generation
//   — TrafficSignal phase grouping
//
// Types:
//   FourWay  — 4 roads meet (standard crossroads)
//   ThreeWay — 3 roads meet (T-junction)
//   TwoWay   — 2 roads meet (straight-through or slight bend, no turning decision)
//
// Road registration: TrafficRoad calls RegisterRoad / RemoveRoad when its
// startNode or endNode is a TrafficIntersectionNode.
// NOTE: TrafficRoad.startNode / endNode are typed as TrafficRoadNode.
//       For the centre node, a separate field intersectionNode is used on
//       IntersectionConnector and TrafficSignal. TrafficRoad does NOT connect
//       to the centre node — only the 4 arm roads connect to it via
//       IntersectionConnector reading connectedRoads directly.
//
// Gizmo colours:
//   FourWay  = yellow
//   ThreeWay = orange  
//   TwoWay   = light green
//
// Talks to: IntersectionConnector, TrafficSignal
// ─────────────────────────────────────────────────────────────────────────────

[ExecuteAlways]
public class TrafficIntersectionNode : MonoBehaviour
{
    public enum IntersectionType
    {
        FourWay,   // 4 road arms
        ThreeWay,  // 3 road arms
        TwoWay     // 2 road arms (bend or continuation)
    }

    [Header("Intersection Settings")]
    public IntersectionType intersectionType = IntersectionType.FourWay;

    [Tooltip("The road segments that feed into this intersection. " +
             "Drag each approach road here, or use Auto Collect Roads.")]
    public List<TrafficRoad> connectedRoads = new List<TrafficRoad>();

    [Header("Gizmo")]
    public float gizmoSize = 1.4f;

    // ─────────────────────────────────────────────────────────────────────
    // Road registration — arm roads register here directly
    // (not via TrafficRoad.OnEnable, since TrafficRoad uses TrafficRoadNode
    //  for its startNode/endNode. Arm roads are registered manually or via
    //  AutoCollectRoads below.)
    // ─────────────────────────────────────────────────────────────────────

    public void RegisterRoad(TrafficRoad road)
    {
        if (!connectedRoads.Contains(road))
            connectedRoads.Add(road);
    }

    public void RemoveRoad(TrafficRoad road)
    {
        connectedRoads.Remove(road);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Auto collect — scans scene for roads whose RoadNode endpoint is close
    // to this intersection centre. Useful when setting up a new scene.
    // ─────────────────────────────────────────────────────────────────────

    [ContextMenu("Auto Collect Nearby Roads")]
    public void AutoCollectRoads()
    {
        connectedRoads.Clear();

        float searchRadius = 5f; // metres — adjust if roads don't register
        var allRoads = FindObjectsOfType<TrafficRoad>();

        foreach (var road in allRoads)
        {
            if (road == null) continue;

            // A road is an arm of this intersection if either of its road nodes
            // is within searchRadius of this intersection centre
            bool startClose = road.startNode != null &&
                Vector3.Distance(road.startNode.transform.position, transform.position) < searchRadius;
            bool endClose   = road.endNode != null &&
                Vector3.Distance(road.endNode.transform.position, transform.position) < searchRadius;

            if (startClose || endClose)
                connectedRoads.Add(road);
        }

        int expected = ExpectedRoadCount();
        Debug.Log($"[TrafficIntersectionNode] '{name}' collected {connectedRoads.Count} roads " +
                  $"(expected {expected} for {intersectionType}).");

        if (connectedRoads.Count != expected && expected > 0)
            Debug.LogWarning($"[TrafficIntersectionNode] Count mismatch on '{name}'. " +
                             $"Check road endpoint positions or increase searchRadius ({searchRadius}m).");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Validation
    // ─────────────────────────────────────────────────────────────────────

    public bool IsTopologyValid()
    {
        return connectedRoads.Count == ExpectedRoadCount();
    }

    public int ExpectedRoadCount()
    {
        switch (intersectionType)
        {
            case IntersectionType.FourWay:  return 4;
            case IntersectionType.ThreeWay: return 3;
            case IntersectionType.TwoWay:   return 2;
            default:                        return -1;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Gizmo
    // ─────────────────────────────────────────────────────────────────────

    public static Color GizmoColorForType(IntersectionType type)
    {
        switch (type)
        {
            case IntersectionType.FourWay:  return new Color(1.0f, 0.9f, 0.0f, 1f); // yellow
            case IntersectionType.ThreeWay: return new Color(1.0f, 0.6f, 0.0f, 1f); // orange
            case IntersectionType.TwoWay:   return new Color(0.5f, 1.0f, 0.5f, 1f); // light green
            default:                        return Color.white;
        }
    }

    void OnDrawGizmos()
    {
        Vector3 pos = transform.position;

        // Draw centre sphere
        Gizmos.color = GizmoColorForType(intersectionType);
        Gizmos.DrawSphere(pos + Vector3.up * 0.3f, gizmoSize);

        // Draw lines to each connected road arm midpoint
        foreach (var road in connectedRoads)
        {
            if (road == null) continue;
            Vector3 armMid = road.transform.position + Vector3.up * 0.3f;
            Gizmos.color = new Color(1f, 1f, 1f, 0.4f);
            Gizmos.DrawLine(pos + Vector3.up * 0.3f, armMid);
            Gizmos.DrawWireSphere(armMid, 0.35f);
        }

#if UNITY_EDITOR
        bool valid = IsTopologyValid();
        string label = $"{name} ({intersectionType}, {connectedRoads.Count}" +
                       (valid ? ")" : $" — expected {ExpectedRoadCount()})");
        UnityEditor.Handles.Label(pos + Vector3.up * (gizmoSize + 1.2f), label);
#endif
    }
}
