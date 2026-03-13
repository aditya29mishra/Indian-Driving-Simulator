using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public class TrafficNode : MonoBehaviour
{
    public enum NodeType
    {
        RoadEnd,
        TJunction,
        CrossRoad,
        Roundabout
    }

    [Header("Node Settings")]
    public NodeType nodeType = NodeType.CrossRoad;

    [Tooltip("Roads connected to this node")]
    public List<TrafficRoad> connectedRoads = new List<TrafficRoad>();

    [Header("Debug")]
    public float gizmoSize = 1.2f;
    public Color gizmoColor = Color.yellow;

    public Vector3 Position => transform.position;

    private void OnDrawGizmos()
    {
        Gizmos.color = gizmoColor;
        Gizmos.DrawSphere(transform.position, gizmoSize);

        #if UNITY_EDITOR
        UnityEditor.Handles.Label(transform.position + Vector3.up * 2f, name);
        #endif
    }

    public void RegisterRoad(TrafficRoad road)
    {
        if (!connectedRoads.Contains(road))
        {
            connectedRoads.Add(road);
        }
    }

    public void RemoveRoad(TrafficRoad road)
    {
        if (connectedRoads.Contains(road))
        {
            connectedRoads.Remove(road);
        }
    }
}