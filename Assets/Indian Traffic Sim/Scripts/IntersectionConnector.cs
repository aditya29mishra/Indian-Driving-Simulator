using System.Collections.Generic;
using UnityEngine;

public class IntersectionConnector : MonoBehaviour
{
    public TrafficNode nodeW;
    public TrafficNode nodeS;
    public TrafficNode nodeE;
    public TrafficNode nodeN;

    public float turnRadius = 6f;
    public int resolution = 12;

    [ContextMenu("Generate Intersection")]
    public void Generate()
    {
        ClearOld();

        Connect(nodeW, nodeN); // right
        Connect(nodeW, nodeE); // straight
        Connect(nodeW, nodeS); // left
        Connect(nodeW, nodeW); // u-turn

        Connect(nodeS, nodeE);
        Connect(nodeS, nodeN);
        Connect(nodeS, nodeW);
        Connect(nodeS, nodeS);

        Connect(nodeE, nodeS);
        Connect(nodeE, nodeW);
        Connect(nodeE, nodeN);
        Connect(nodeE, nodeE);

        Connect(nodeN, nodeW);
        Connect(nodeN, nodeS);
        Connect(nodeN, nodeE);
        Connect(nodeN, nodeN);
    }

    void Connect(TrafficNode fromNode, TrafficNode toNode)
    {
        if (fromNode.connectedRoads.Count == 0 || toNode.connectedRoads.Count == 0)
            return;

        TrafficRoad inRoad = fromNode.connectedRoads[0];
        TrafficRoad outRoad = toNode.connectedRoads[0];

        List<TrafficLane> inLanes = GetForwardLanes(inRoad);
        List<TrafficLane> outLanes = GetBackwardLanes(outRoad);

        int laneCount = Mathf.Min(inLanes.Count, outLanes.Count);

        for (int i = 0; i < laneCount; i++)
        {
            TrafficLane inLane = inLanes[i];

            int outIndex = i;

            if (IsReverseTurn(fromNode, toNode))
            {
                outIndex = outLanes.Count - 1 - i;
            }

            TrafficLane outLane = outLanes[outIndex];

            CreateCurve(inLane, outLane);
        }
    }

    List<TrafficLane> GetForwardLanes(TrafficRoad road)
    {
        List<TrafficLane> lanes = new List<TrafficLane>();

        foreach (var l in road.lanes)
            if (l.forwardDirection)
                lanes.Add(l);

        lanes.Sort((a, b) =>
        {
            float da = Vector3.Dot(a.transform.position - road.transform.position, road.transform.right);
            float db = Vector3.Dot(b.transform.position - road.transform.position, road.transform.right);
            return da.CompareTo(db);
        });

        return lanes;
    }

    List<TrafficLane> GetBackwardLanes(TrafficRoad road)
    {
        List<TrafficLane> lanes = new List<TrafficLane>();

        foreach (var l in road.lanes)
            if (!l.forwardDirection)
                lanes.Add(l);

        lanes.Sort((a, b) =>
        {
            float da = Vector3.Dot(a.transform.position - road.transform.position, road.transform.right);
            float db = Vector3.Dot(b.transform.position - road.transform.position, road.transform.right);
            return da.CompareTo(db);
        });

        return lanes;
    }

    bool IsReverseTurn(TrafficNode fromNode, TrafficNode toNode)
    {
        // STRAIGHT
        if (fromNode == nodeW && toNode == nodeE) return true;
        if (fromNode == nodeE && toNode == nodeW) return true;
        if (fromNode == nodeN && toNode == nodeS) return true;
        if (fromNode == nodeS && toNode == nodeN) return true;

        // RIGHT
        if (fromNode == nodeW && toNode == nodeN) return true;
        if (fromNode == nodeN && toNode == nodeE) return true;
        if (fromNode == nodeE && toNode == nodeS) return true;
        if (fromNode == nodeS && toNode == nodeW) return true;

        // LEFT
        if (fromNode == nodeW && toNode == nodeS) return true;
        if (fromNode == nodeS && toNode == nodeE) return true;
        if (fromNode == nodeE && toNode == nodeN) return true;
        if (fromNode == nodeN && toNode == nodeW) return true;

        // UTURN
        if (fromNode == toNode) return true;

        return false;
    }

    void CreateCurve(TrafficLane fromLane, TrafficLane toLane)
    {
        Vector3 start = fromLane.path.waypoints[fromLane.path.waypoints.Count - 1].position;
        Vector3 end = toLane.path.waypoints[0].position;

        Vector3 dirStart = fromLane.transform.forward;
        Vector3 dirEnd = toLane.transform.forward;

        Vector3 p0 = start;
        Vector3 p1 = start + dirStart * turnRadius;
        Vector3 p2 = end - dirEnd * turnRadius;
        Vector3 p3 = end;

        GameObject obj = new GameObject("TurnPath");
        obj.transform.parent = transform;

        TrafficPath path = obj.AddComponent<TrafficPath>();

        for (int i = 0; i <= resolution; i++)
        {
            float t = i / (float)resolution;

            Vector3 point =
                Mathf.Pow(1 - t, 3) * p0 +
                3 * Mathf.Pow(1 - t, 2) * t * p1 +
                3 * (1 - t) * t * t * p2 +
                Mathf.Pow(t, 3) * p3;

            GameObject wp = new GameObject("WP_" + i);
            wp.transform.position = point;
            wp.transform.parent = obj.transform;

            path.waypoints.Add(wp.transform);
        }

        fromLane.nextLanes.Add(toLane);
    }

    void ClearOld()
    {
        foreach (Transform child in transform)
        {
            DestroyImmediate(child.gameObject);
        }
    }
}