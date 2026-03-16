using System;
using System.Collections.Generic;
using UnityEngine;

public class LaneManager : MonoBehaviour
{
    private Dictionary<TrafficLane, TrafficLane> leftNeighbour  = new Dictionary<TrafficLane, TrafficLane>();
    private Dictionary<TrafficLane, TrafficLane> rightNeighbour = new Dictionary<TrafficLane, TrafficLane>();
    private Dictionary<TrafficVehicle, TrafficLane> lastKnownLane = new Dictionary<TrafficVehicle, TrafficLane>();
    private List<TrafficVehicle> registeredVehicles = new List<TrafficVehicle>();

    void Start()
    {
        AutoRegisterAllRoads();
    }
    public void RegisterRoad(TrafficRoad road)
    {
        if (road == null) return;

        // Compute real road perpendicular from node positions, not transform.right
        Vector3 roadRight;
        Vector3 roadOrigin;

        if (road.startNode != null && road.endNode != null)
        {
            Vector3 dir = (road.endNode.transform.position - road.startNode.transform.position).normalized;
            roadRight  = Vector3.Cross(Vector3.up, dir).normalized;
            roadOrigin = road.startNode.transform.position;
        }
        else
        {
            roadRight  = road.transform.right;
            roadOrigin = road.transform.position;
        }

        List<TrafficLane> fwd = new List<TrafficLane>();
        List<TrafficLane> bwd = new List<TrafficLane>();

        foreach (var lane in road.lanes)
        {
            if (lane.forwardDirection) fwd.Add(lane);
            else bwd.Add(lane);
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

        // Debug
        for (int i = 0; i < lanes.Count; i++)
        {
            string left  = leftNeighbour[lanes[i]]  != null ? leftNeighbour[lanes[i]].name  : "none";
            string right = rightNeighbour[lanes[i]] != null ? rightNeighbour[lanes[i]].name : "none";
            Debug.Log($"[LaneManager] {lanes[i].name} | left={left} | right={right}");
        }
    }

    public void RegisterVehicle(TrafficVehicle vehicle)
    {
        if (vehicle == null) return;
        if (!registeredVehicles.Contains(vehicle))
            registeredVehicles.Add(vehicle);
        ApplyAdjacency(vehicle);
        lastKnownLane[vehicle] = vehicle.currentLane;
    }

    public void RefreshVehicle(TrafficVehicle vehicle)
    {
        if (vehicle == null) return;
        ApplyAdjacency(vehicle);
        lastKnownLane[vehicle] = vehicle.currentLane;
    }

    void ApplyAdjacency(TrafficVehicle vehicle)
    {
        TrafficLane lane = vehicle.currentLane;
        if (lane == null) return;
        vehicle.leftLane  = leftNeighbour.ContainsKey(lane)  ? leftNeighbour[lane]  : null;
        vehicle.rightLane = rightNeighbour.ContainsKey(lane) ? rightNeighbour[lane] : null;
    }

    void Update()
    {
        registeredVehicles.RemoveAll(v => v == null);

        foreach (var vehicle in registeredVehicles)
        {
            if (vehicle == null) continue;
            TrafficLane last = lastKnownLane.ContainsKey(vehicle) ? lastKnownLane[vehicle] : null;
            if (vehicle.currentLane != last)
            {
                ApplyAdjacency(vehicle);
                lastKnownLane[vehicle] = vehicle.currentLane;
            }
        }
    }

    [ContextMenu("Auto Register All Roads")]
    public void AutoRegisterAllRoads()
    {
        leftNeighbour.Clear();
        rightNeighbour.Clear();
        TrafficRoad[] allRoads = FindObjectsOfType<TrafficRoad>();
        foreach (var road in allRoads)
            RegisterRoad(road);
        Debug.Log($"LaneManager: registered {allRoads.Length} roads.");
    }
}