using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficRoadNode — sits at the endpoint of a road segment.
//
// Role: marks what kind of point this is on the road network.
//   End   — dead end, terminal. Vehicles despawn here. Valid destination.
//   Cross — mid-road pedestrian / level crossing point.
//   T     — mid-road T-branch point (not a full intersection centre).
//   Joint — two road segments join end-to-end. Vehicle continues straight through.
//
// Road registration: TrafficRoad calls RegisterRoad / RemoveRoad on its
// startNode and endNode (which are now TrafficRoadNode).
//
// Gizmo colours (always visible, not just on selection):
//   End   = cyan
//   Cross = red
//   T     = orange
//   Joint = grey
//
// Talks to: TrafficRoad (registers itself), VehicleSpawner (destination pool),
//           DestinationSystem (arrival check)
// ─────────────────────────────────────────────────────────────────────────────

[ExecuteAlways]
public class TrafficRoadNode : MonoBehaviour
{
    public enum NodeType
    {
        End,    // Terminal dead-end — destination, despawn point
        Cross,  // Mid-road crossing (pedestrian, level crossing)
        T,      // Mid-road T-branch
        Joint   // Segment join — road continues, no branching
    }

    [Header("Node Settings")]
    public NodeType nodeType = NodeType.End;

    [Tooltip("Road segments connected to this node. Auto-populated by TrafficRoad.")]
    public List<TrafficRoad> connectedRoads = new List<TrafficRoad>();

    [Header("Gizmo")]
    public float gizmoSize = 1.0f;

    public Vector3 Position => transform.position;

    // ─────────────────────────────────────────────────────────────────────
    // Road registration — called by TrafficRoad.OnEnable / OnDisable
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
    // Validation
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// True if this node's connectedRoads count matches what its NodeType expects.
    /// End and Joint expect 1 road. Cross and T can have 1–2.
    /// Used by editor tools to warn on misconfigured nodes.
    /// </summary>
    public bool IsTopologyValid()
    {
        switch (nodeType)
        {
            case NodeType.End:   return connectedRoads.Count == 1;
            case NodeType.Joint: return connectedRoads.Count == 2;
            case NodeType.Cross: return connectedRoads.Count >= 1;
            case NodeType.T:     return connectedRoads.Count >= 1;
            default:             return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Gizmo
    // ─────────────────────────────────────────────────────────────────────

    public static Color GizmoColorForType(NodeType type)
    {
        switch (type)
        {
            case NodeType.End:   return new Color(0.0f, 0.9f, 1.0f, 1f); // cyan
            case NodeType.Cross: return new Color(1.0f, 0.2f, 0.2f, 1f); // red
            case NodeType.T:     return new Color(1.0f, 0.6f, 0.0f, 1f); // orange
            case NodeType.Joint: return new Color(0.6f, 0.6f, 0.6f, 1f); // grey
            default:             return Color.white;
        }
    }

    void OnDrawGizmos()
    {
        Gizmos.color = GizmoColorForType(nodeType);
        Gizmos.DrawSphere(transform.position, gizmoSize);

#if UNITY_EDITOR
        string label = $"{name} ({nodeType}, {connectedRoads.Count})";
        UnityEditor.Handles.Label(transform.position + Vector3.up * (gizmoSize + 0.8f), label);
#endif
    }
}
