using System;
using System.Collections.Generic;
using UnityEngine;

public class LaneManager : MonoBehaviour
{
    // ─────────────────────────────────────────────
    // EXISTING (unchanged)
    // ─────────────────────────────────────────────
    private Dictionary<TrafficLane, TrafficLane> leftNeighbour = new();
    private Dictionary<TrafficLane, TrafficLane> rightNeighbour = new();

    private Dictionary<TrafficVehicle, TrafficLane> lastKnownLane = new();
    private List<TrafficVehicle> registeredVehicles = new();

    // ─────────────────────────────────────────────
    // NEW: Lane vehicle lists
    // ─────────────────────────────────────────────
    private Dictionary<TrafficLane, List<TrafficVehicle>> laneVehicles = new();

    private float updateTimer = 0f;
    private const float updateInterval = 0.2f;

    void Start()
    {
        AutoRegisterAllRoads();
    }

    // ─────────────────────────────────────────────
    // REGISTER VEHICLE
    // ─────────────────────────────────────────────
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

    // ─────────────────────────────────────────────
    // INSERT / REMOVE
    // ─────────────────────────────────────────────
    void InsertIntoLane(TrafficVehicle v, TrafficLane lane)
    {
        if (lane == null) return;

        if (!laneVehicles.ContainsKey(lane))
            laneVehicles[lane] = new List<TrafficVehicle>();

        var list = laneVehicles[lane];

        if (!list.Contains(v))
            list.Add(v);
    }

    void RemoveFromLane(TrafficVehicle v, TrafficLane lane)
    {
        if (lane == null) return;

        if (laneVehicles.TryGetValue(lane, out var list))
            list.Remove(v);
    }

    // ─────────────────────────────────────────────
    // MAIN UPDATE (ORDER + LEADER)
    // ─────────────────────────────────────────────
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

    // ─────────────────────────────────────────────
    // SORT + ASSIGN LEADERS
    // ─────────────────────────────────────────────
    void UpdateLaneOrdering()
    {
        foreach (var kvp in laneVehicles)
        {
            var list = kvp.Value;

            // remove nulls
            list.RemoveAll(v => v == null);

            // 🔥 SORT by distance
            list.Sort((a, b) =>
                a.DistanceTravelled.CompareTo(b.DistanceTravelled));

            // 🔥 ASSIGN LEADERS
            for (int i = 0; i < list.Count; i++)
            {
                var v = list[i];

                TrafficVehicle leader = (i < list.Count - 1) ? list[i + 1] : null;

                // Suppress leader while vehicle is in its green-light restart phase
                // so it can accelerate freely out of the queue without IDM braking.
                if (v.CurrentSignalState == TrafficVehicle.SignalStateEx.Released)
                {
                    v.leaderVehicle = null;
                }
                else
                {
                    v.leaderVehicle = leader;
                }
            }
        }
    }

    // ─────────────────────────────────────────────
    // ADJACENCY (UNCHANGED)
    // ─────────────────────────────────────────────
    public void RegisterRoad(TrafficRoad road)
    {
        if (road == null) return;

        Vector3 roadRight;
        Vector3 roadOrigin;

        if (road.startNode != null && road.endNode != null)
        {
            Vector3 dir = (road.endNode.transform.position - road.startNode.transform.position).normalized;
            roadRight = Vector3.Cross(Vector3.up, dir).normalized;
            roadOrigin = road.startNode.transform.position;
        }
        else
        {
            roadRight = road.transform.right;
            roadOrigin = road.transform.position;
        }

        List<TrafficLane> fwd = new();
        List<TrafficLane> bwd = new();

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
            leftNeighbour[lanes[i]] = i > 0 ? lanes[i - 1] : null;
            rightNeighbour[lanes[i]] = i < lanes.Count - 1 ? lanes[i + 1] : null;
        }
    }

    void ApplyAdjacency(TrafficVehicle vehicle)
    {
        TrafficLane lane = vehicle.currentLane;
        if (lane == null) return;

        vehicle.leftLane = leftNeighbour.ContainsKey(lane) ? leftNeighbour[lane] : null;
        vehicle.rightLane = rightNeighbour.ContainsKey(lane) ? rightNeighbour[lane] : null;
    }

    [ContextMenu("Auto Register All Roads")]
    public void AutoRegisterAllRoads()
    {
        leftNeighbour.Clear();
        rightNeighbour.Clear();

        TrafficRoad[] allRoads = FindObjectsOfType<TrafficRoad>();

        foreach (var road in allRoads)
            RegisterRoad(road);
    }
}