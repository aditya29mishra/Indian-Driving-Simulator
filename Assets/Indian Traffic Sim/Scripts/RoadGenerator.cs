using UnityEngine;

public class RoadGenerator : MonoBehaviour
{
    public TrafficNode startNode;
    public TrafficNode endNode;

    public int forwardLanes = 3;
    public int backwardLanes = 3;

    public float laneWidth = 3.5f;
    public float waypointSpacing = 8f;

    [ContextMenu("Generate Road")]
    public void GenerateRoad()
    {
        if (startNode == null || endNode == null)
        {
            Debug.LogError("StartNode or EndNode missing.");
            return;
        }

        TrafficRoad road = GetComponent<TrafficRoad>();
        if (road == null) { Debug.LogError("TrafficRoad component missing!"); return; }

        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);

        road.lanes.Clear();

        Vector3 start     = startNode.transform.position;
        Vector3 end       = endNode.transform.position;
        Vector3 direction = (end - start).normalized;
        Vector3 right     = Vector3.Cross(Vector3.up, direction);

        int totalLanes = forwardLanes + backwardLanes;

        for (int i = 0; i < totalLanes; i++)
        {
            bool isForward = i < forwardLanes;
            int groupIndex = isForward ? i : i - forwardLanes;
            int groupSize  = isForward ? forwardLanes : backwardLanes;

            float offsetFromCenter = (groupIndex - (groupSize - 1) * 0.5f) * laneWidth;
            float groupShift       = (isForward ? -1 : 1) * (laneWidth * groupSize * 0.5f);
            float finalOffset      = offsetFromCenter + groupShift;

            Vector3 laneStart = start + right * finalOffset;
            Vector3 laneEnd   = end   + right * finalOffset;

            if (!isForward) { Vector3 tmp = laneStart; laneStart = laneEnd; laneEnd = tmp; }

            GameObject laneObj = new GameObject("Lane_" + i);
            laneObj.transform.parent = transform;

            TrafficLane lane = laneObj.AddComponent<TrafficLane>();
            lane.laneIndex        = i;
            lane.forwardDirection = isForward;
            lane.road             = road;
            road.lanes.Add(lane);

            GameObject pathObj = new GameObject("Path");
            pathObj.transform.parent = laneObj.transform;

            TrafficPath path = pathObj.AddComponent<TrafficPath>();
            path.road  = road;
            path.lane  = lane;
            lane.path  = path;

            float distance     = Vector3.Distance(laneStart, laneEnd);
            int waypointCount  = Mathf.CeilToInt(distance / waypointSpacing);

            for (int w = 0; w <= waypointCount; w++)
            {
                float t   = w / (float)waypointCount;
                Vector3 pos = Vector3.Lerp(laneStart, laneEnd, t);
                GameObject wp = new GameObject("WP_" + w);
                wp.transform.position = pos;
                wp.transform.parent   = pathObj.transform;
                path.waypoints.Add(wp.transform);
            }
        }

        // Notify LaneManager
        LaneManager laneManager = FindObjectOfType<LaneManager>();
        if (laneManager != null)
            laneManager.RegisterRoad(road);
    }
}