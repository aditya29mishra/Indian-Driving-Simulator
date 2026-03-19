using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficNode — junction anchor, connected roads list
//
// Represents a junction point where roads connect. Maintains list of
// connected roads for navigation and intersection logic.
//
// Talks to: TrafficRoad (connected roads)
// ─────────────────────────────────────────────────────────────────────────────

[ExecuteAlways]
public class TrafficNode : MonoBehaviour
{
    /// <summary>Types of junctions this node represents.</summary>
    public enum NodeType
    {
        RoadEnd,
        TJunction,
        CrossRoad,
        Roundabout
    }

    [Header("Node Settings")]
    /// <summary>Type of this junction node.</summary>
    public NodeType nodeType = NodeType.CrossRoad;

    [Tooltip("Roads connected to this node")]
    /// <summary>List of roads connected to this node.</summary>
    public List<TrafficRoad> connectedRoads = new List<TrafficRoad>();

    [Header("Debug")]
    /// <summary>Size of the gizmo sphere.</summary>
    public float gizmoSize = 1.2f;
    /// <summary>Color of the gizmo.</summary>
    public Color gizmoColor = Color.yellow;

    /// <summary>Gets the world position of this node.</summary>
    public Vector3 Position => transform.position;

    private void OnDrawGizmos()
    {
        Gizmos.color = gizmoColor;
        Gizmos.DrawSphere(transform.position, gizmoSize);

        #if UNITY_EDITOR
        UnityEditor.Handles.Label(transform.position + Vector3.up * 2f, name);
        #endif
    }

    /// <summary>Registers a road as connected to this node.</summary>
    /// <param name="road">The road to register.</param>
    public void RegisterRoad(TrafficRoad road)
    {
        if (!connectedRoads.Contains(road))
        {
            connectedRoads.Add(road);
        }
    }

    /// <summary>Removes a road from the connected roads list.</summary>
    /// <param name="road">The road to remove.</param>
    public void RemoveRoad(TrafficRoad road)
    {
        if (connectedRoads.Contains(road))
        {
            connectedRoads.Remove(road);
        }
    }
}