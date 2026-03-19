using System.Collections.Generic;
using UnityEngine;

public class LaneManager : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────────────
    // SCENE REFERENCES — assign in Inspector, then click Generate All
    // ─────────────────────────────────────────────────────────────────────
    [Header("Scene Setup")]
    [Tooltip("All TrafficRoad components in the scene.")]
    public List<TrafficRoad>           roads                  = new List<TrafficRoad>();
    [Tooltip("All IntersectionConnector components in the scene.")]
    public List<IntersectionConnector> intersectionConnectors = new List<IntersectionConnector>();
    [Tooltip("All TrafficSignal components in the scene.")]
    public List<TrafficSignal>         trafficSignals         = new List<TrafficSignal>();

    // ─────────────────────────────────────────────────────────────────────
    // GENERATE ALL
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Master generator. Safe to click multiple times — each step destroys
    /// and rebuilds from scratch, no duplicate GameObjects are created.
    ///
    /// Order is important:
    ///   1. Roads    — destroys old lane/waypoint children, rebuilds them
    ///   2. Arcs     — destroys old arc children, clears nextPaths, rebuilds arcs
    ///   3. Signals  — clears groups, rebuilds from intersection node roads
    ///   4. Adjacency — clears and rebuilds left/right neighbour maps
    /// </summary>
    [ContextMenu("Generate All")]
    public void GenerateAll()
    {
        // Step 1 — Roads (each road clears its own children before rebuilding)
        int roadCount = 0;
        foreach (var road in roads)
        {
            if (road == null) continue;
            road.GenerateRoad();
            roadCount++;
        }
        Debug.Log($"[LaneManager] Step 1 complete — {roadCount} road(s) generated.");

        // Step 1b — Joint / Cross / T lane wiring
        // Walks every TrafficRoadNode with nodeType Joint, Cross, or T.
        // For each, finds the two roads that meet at that node, classifies
        // lanes as arriving or departing using waypoint distances (no
        // forwardDirection assumption), then wires nextLanes with direct
        // index pairing (same direction of travel, no flip = no reversal needed).
        int jointCount = WireJointNodes();
        Debug.Log($"[LaneManager] Step 1b complete — {jointCount} joint/cross/T node(s) wired.");

        // Step 2 — Intersection arcs (connector clears old arcs + nextPaths before rebuilding)
        int intersectionCount = 0;
        foreach (var ic in intersectionConnectors)
        {
            if (ic == null) continue;
            ic.AutoGenerate();
            intersectionCount++;
        }
        Debug.Log($"[LaneManager] Step 2 complete — {intersectionCount} intersection(s) generated.");

        // Step 3 — Signal lane groups (signal clears groups before rebuilding)
        int signalCount = 0;
        foreach (var sig in trafficSignals)
        {
            if (sig == null) continue;
            sig.AutoAssignLanesFromNode();
            signalCount++;
        }
        Debug.Log($"[LaneManager] Step 3 complete — {signalCount} signal(s) assigned.");

        // Step 4 — Lane adjacency (clears and rebuilds neighbour maps)
        AutoRegisterAllRoads();
        Debug.Log("[LaneManager] Step 4 complete — adjacency registered.");

        Debug.Log("[LaneManager] ✓ Generate All complete.");
    }

    // ─────────────────────────────────────────────────────────────────────
    // VEHICLE TRACKING
    // ─────────────────────────────────────────────────────────────────────

    private Dictionary<TrafficLane, TrafficLane>          leftNeighbour     = new();
    private Dictionary<TrafficLane, TrafficLane>          rightNeighbour    = new();
    private Dictionary<TrafficVehicle, TrafficLane>       lastKnownLane     = new();
    private List<TrafficVehicle>                          registeredVehicles = new();
    private Dictionary<TrafficLane, List<TrafficVehicle>> laneVehicles      = new();

    private float       updateTimer    = 0f;
    private const float updateInterval = 0.2f;

    void Start()
    {
        AutoRegisterAllRoads();
    }

    public void RegisterVehicle(TrafficVehicle vehicle)
    {
        if (vehicle == null) return;
        if (!registeredVehicles.Contains(vehicle))
            registeredVehicles.Add(vehicle);
        lastKnownLane[vehicle] = vehicle.currentLane;
        InsertIntoLane(vehicle, vehicle.currentLane);
        ApplyAdjacency(vehicle);
    }

    public void RefreshVehicle(TrafficVehicle vehicle)
    {
        if (vehicle == null) return;
        TrafficLane oldLane = lastKnownLane.ContainsKey(vehicle) ? lastKnownLane[vehicle] : null;
        TrafficLane newLane = vehicle.currentLane;
        if (oldLane != newLane)
        {
            RemoveFromLane(vehicle, oldLane);
            InsertIntoLane(vehicle, newLane);
            lastKnownLane[vehicle] = newLane;
        }
        ApplyAdjacency(vehicle);
    }

    void InsertIntoLane(TrafficVehicle v, TrafficLane lane)
    {
        if (lane == null) return;
        if (!laneVehicles.ContainsKey(lane))
            laneVehicles[lane] = new List<TrafficVehicle>();
        if (!laneVehicles[lane].Contains(v))
            laneVehicles[lane].Add(v);
    }

    void RemoveFromLane(TrafficVehicle v, TrafficLane lane)
    {
        if (lane == null) return;
        if (laneVehicles.TryGetValue(lane, out var list))
            list.Remove(v);
    }

    // ─────────────────────────────────────────────────────────────────────
    // UPDATE — order + leader assignment
    // ─────────────────────────────────────────────────────────────────────

    void Update()
    {
        updateTimer += Time.deltaTime;
        if (updateTimer < updateInterval) return;
        updateTimer = 0f;

        registeredVehicles.RemoveAll(v => v == null);

        foreach (var vehicle in registeredVehicles)
        {
            if (vehicle == null) continue;
            TrafficLane last = lastKnownLane.ContainsKey(vehicle) ? lastKnownLane[vehicle] : null;
            if (vehicle.currentLane != last)
            {
                RemoveFromLane(vehicle, last);
                InsertIntoLane(vehicle, vehicle.currentLane);
                lastKnownLane[vehicle] = vehicle.currentLane;
            }
        }

        UpdateLaneOrdering();
    }

    void UpdateLaneOrdering()
    {
        foreach (var kvp in laneVehicles)
        {
            var list = kvp.Value;
            list.RemoveAll(v => v == null);
            list.Sort((a, b) => a.DistanceTravelled.CompareTo(b.DistanceTravelled));

            for (int i = 0; i < list.Count; i++)
            {
                var v      = list[i];
                var leader = (i < list.Count - 1) ? list[i + 1] : null;

                if (v.CurrentSignalState == TrafficVehicle.SignalStateEx.Released)
                {
                    bool isFront = v.negotiator?.QueueRipple?.IsFrontOfQueue ?? true;
                    v.leaderVehicle = isFront ? null : leader;
                }
                else
                {
                    v.leaderVehicle = leader;
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // ADJACENCY
    // ─────────────────────────────────────────────────────────────────────

    public void RegisterRoad(TrafficRoad road)
    {
        if (road == null) return;

        Vector3 roadRight, roadOrigin;

        if (road.startNode != null && road.endNode != null)
        {
            Vector3 dir = (road.endNode.transform.position -
                           road.startNode.transform.position).normalized;
            roadRight  = Vector3.Cross(Vector3.up, dir).normalized;
            roadOrigin = road.startNode.transform.position;
        }
        else
        {
            roadRight  = road.transform.right;
            roadOrigin = road.transform.position;
        }

        var fwd = new List<TrafficLane>();
        var bwd = new List<TrafficLane>();

        foreach (var lane in road.lanes)
        {
            if (lane.forwardDirection) fwd.Add(lane);
            else                       bwd.Add(lane);
        }

        BuildAdjacency(fwd, roadRight, roadOrigin);
        BuildAdjacency(bwd, roadRight, roadOrigin);
    }

    void BuildAdjacency(List<TrafficLane> lanes, Vector3 roadRight, Vector3 roadOrigin)
    {
        if (lanes.Count < 2) return;

        lanes.Sort((a, b) =>
        {
            float da = Vector3.Dot(a.transform.position - roadOrigin, roadRight);
            float db = Vector3.Dot(b.transform.position - roadOrigin, roadRight);
            return da.CompareTo(db);
        });

        for (int i = 0; i < lanes.Count; i++)
        {
            leftNeighbour[lanes[i]]  = i > 0               ? lanes[i - 1] : null;
            rightNeighbour[lanes[i]] = i < lanes.Count - 1 ? lanes[i + 1] : null;
        }
    }

    void ApplyAdjacency(TrafficVehicle vehicle)
    {
        TrafficLane lane = vehicle.currentLane;
        if (lane == null) return;
        vehicle.leftLane  = leftNeighbour.ContainsKey(lane)  ? leftNeighbour[lane]  : null;
        vehicle.rightLane = rightNeighbour.ContainsKey(lane) ? rightNeighbour[lane] : null;
    }

    void SortByCentrelineDistance(List<TrafficLane> lanes, TrafficRoad road)
    {
        Vector3 centre = road.transform.position;
        Vector3 roadDir;
        if (road.startNode != null && road.endNode != null)
            roadDir = (road.endNode.transform.position -
                       road.startNode.transform.position).normalized;
        else
            roadDir = road.transform.forward;

        Vector3 right = Vector3.Cross(Vector3.up, roadDir).normalized;

        lanes.Sort((a, b) =>
        {
            float da = Mathf.Abs(Vector3.Dot(a.transform.position - centre, right));
            float db = Mathf.Abs(Vector3.Dot(b.transform.position - centre, right));
            return da.CompareTo(db);
        });
    }

    [ContextMenu("Auto Register All Roads")]
    public void AutoRegisterAllRoads()
    {
        leftNeighbour.Clear();
        rightNeighbour.Clear();

        foreach (var road in FindObjectsOfType<TrafficRoad>())
            RegisterRoad(road);
    }

    // ─────────────────────────────────────────────────────────────────────
    // JOINT / CROSS / T NODE WIRING
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks every TrafficRoadNode with nodeType Joint, Cross, or T.
    /// For each node, wires arriving lanes from one road to departing lanes
    /// of the other road via nextLanes (no arc — straight continuation).
    ///
    /// Direction detection is position-based:
    ///   Arriving  = waypoints[last] closer to node position
    ///   Departing = waypoints[0]    closer to node position
    ///
    /// Lane pairing uses direct index (not reversed) because travel direction
    /// does NOT flip at a joint — the car continues in the same direction.
    /// Lanes are sorted by a shared world axis so index 0 = leftmost on both sides.
    ///
    /// Left-hand traffic is maintained:
    ///   arriving[0]  (leftmost) → departing[0]  (leftmost)  ✓
    ///   arriving[1]  (middle)   → departing[1]  (middle)    ✓
    ///   arriving[2]  (rightmost)→ departing[2]  (rightmost) ✓
    /// </summary>
    int WireJointNodes()
    {
        int count = 0;
        var allNodes = FindObjectsOfType<TrafficRoadNode>();

        foreach (var node in allNodes)
        {
            if (node == null) continue;
            if (node.nodeType != TrafficRoadNode.NodeType.Joint &&
                node.nodeType != TrafficRoadNode.NodeType.Cross &&
                node.nodeType != TrafficRoadNode.NodeType.T) continue;

            if (node.connectedRoads.Count < 2)
            {
                Debug.LogWarning($"[LaneManager] Joint node '{node.name}' has only " +
                                 $"{node.connectedRoads.Count} road(s). Need at least 2 to wire.");
                continue;
            }

            WireNodeConnections(node);
            count++;
        }
        return count;
    }

    void WireNodeConnections(TrafficRoadNode node)
    {
        Vector3 nodePos = node.transform.position;
        var nodeRoads   = node.connectedRoads;

        // Clear existing nextLanes on all lanes of all roads at this node
        // so re-generating doesn't stack duplicate connections
        foreach (var road in nodeRoads)
        {
            if (road == null) continue;
            foreach (var lane in road.lanes)
                if (lane != null) lane.nextLanes.Clear();
        }

        // For each ordered pair of roads at this node,
        // wire arriving lanes of roadA → departing lanes of roadB
        for (int i = 0; i < nodeRoads.Count; i++)
        {
            for (int j = 0; j < nodeRoads.Count; j++)
            {
                if (i == j) continue;

                TrafficRoad roadA = nodeRoads[i];
                TrafficRoad roadB = nodeRoads[j];
                if (roadA == null || roadB == null) continue;

                var arriving  = GetArrivingLanesAtNode(roadA,  nodePos);
                var departing = GetDepartingLanesAtNode(roadB, nodePos);

                if (arriving.Count == 0 || departing.Count == 0) continue;

                // Sort both sets by laneIndex ascending — RoadGenerator creation order
                arriving.Sort((a, b)  => a.laneIndex.CompareTo(b.laneIndex));
                departing.Sort((a, b) => a.laneIndex.CompareTo(b.laneIndex));

                // Bool 1 — flipArriving:  reverse arriving set order
                // Bool 2 — flipDeparting: reverse departing set order
                // Both driven by forwardDirection of their respective set's first lane.
                bool flipArr = arriving.Count  > 0 && arriving[0].forwardDirection;
                bool flipDep = departing.Count > 0 && departing[0].forwardDirection;

                int pairCount = Mathf.Min(arriving.Count, departing.Count);
                for (int k = 0; k < pairCount; k++)
                {
                    int arrIdx = flipArr ? arriving.Count  - 1 - k : k;
                    int depIdx = flipDep ? departing.Count - 1 - k : k;
                    var dep    = departing[depIdx];
                    if (!arriving[arrIdx].nextLanes.Contains(dep))
                        arriving[arrIdx].nextLanes.Add(dep);
                }

                if (arriving.Count != departing.Count)
                    Debug.LogWarning($"[LaneManager] Node '{node.name}': " +
                                     $"road '{roadA.name}' has {arriving.Count} arriving lane(s) " +
                                     $"but road '{roadB.name}' has {departing.Count} departing lane(s). " +
                                     $"Wired {Mathf.Min(arriving.Count, departing.Count)} pair(s).");
            }
        }
    }

    /// <summary>
    /// Arriving lanes at a node = lanes whose waypoints[last] is closer to node.
    /// These are heading INTO the node.
    /// </summary>
    List<TrafficLane> GetArrivingLanesAtNode(TrafficRoad road, Vector3 nodePos)
    {
        var result = new List<TrafficLane>();
        foreach (var lane in road.lanes)
        {
            if (lane?.path == null || lane.path.waypoints == null ||
                lane.path.waypoints.Count < 2) continue;

            Vector3 wp0   = lane.path.waypoints[0].position;
            Vector3 wpEnd = lane.path.waypoints[lane.path.waypoints.Count - 1].position;

            if (Vector3.Distance(wpEnd, nodePos) < Vector3.Distance(wp0, nodePos))
                result.Add(lane);
        }
        return result;
    }

    /// <summary>
    /// Departing lanes at a node = lanes whose waypoints[0] is closer to node.
    /// These are heading OUT OF the node.
    /// </summary>
    List<TrafficLane> GetDepartingLanesAtNode(TrafficRoad road, Vector3 nodePos)
    {
        var result = new List<TrafficLane>();
        foreach (var lane in road.lanes)
        {
            if (lane?.path == null || lane.path.waypoints == null ||
                lane.path.waypoints.Count < 2) continue;

            Vector3 wp0   = lane.path.waypoints[0].position;
            Vector3 wpEnd = lane.path.waypoints[lane.path.waypoints.Count - 1].position;

            if (Vector3.Distance(wp0, nodePos) < Vector3.Distance(wpEnd, nodePos))
                result.Add(lane);
        }
        return result;
    }

}