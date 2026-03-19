using UnityEngine;
using System.Collections.Generic;

public class LaneConnector : MonoBehaviour
{
    public TrafficRoad incomingRoad;
    public TrafficRoad outgoingRoad;

    [ContextMenu("Connect Straight Lanes")]
    public void ConnectStraight()
    {
        List<TrafficLane> incoming = new List<TrafficLane>();
        List<TrafficLane> outgoing = new List<TrafficLane>();

        // collect incoming lanes (green)
        foreach (var lane in incomingRoad.lanes)
        {
            if (lane.forwardDirection)
                incoming.Add(lane);
        }

        // collect outgoing lanes (red)
        foreach (var lane in outgoingRoad.lanes)
        {
            if (!lane.forwardDirection)
                outgoing.Add(lane);
        }

        int count = Mathf.Min(incoming.Count, outgoing.Count);

        for (int i = 0; i < count; i++)
        {
            incoming[i].nextLanes.Add(outgoing[i]);
        }

        Debug.Log("Straight connections created.");
    }
}