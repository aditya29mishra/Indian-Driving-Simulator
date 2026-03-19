using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficLane — lane data container, nextPaths for intersection turns
//
// Holds lane configuration, path reference, and turn connections.
// Defines possible next lanes and paths for vehicle navigation.
//
// Talks to: TrafficPath (path), TrafficRoad (road), TrafficVehicle (assignment)
// ─────────────────────────────────────────────────────────────────────────────

public class TrafficLane : MonoBehaviour
{
    /// <summary>Represents a path and its target lane for turn navigation.</summary>
    [System.Serializable]
    public class LanePath
    {
        /// <summary>The path to follow for this turn.</summary>
        public TrafficPath path;
        /// <summary>The target lane after completing the turn.</summary>
        public TrafficLane targetLane;
    }

    /// <summary>The road this lane belongs to.</summary>
    public TrafficRoad road;

    /// <summary>Index of this lane within the road.</summary>
    public int laneIndex = 0;

    /// <summary>Width of the lane in meters.</summary>
    public float laneWidth = 3.5f;

    /// <summary>Whether traffic flows forward along the lane.</summary>
    public bool forwardDirection = true;

    /// <summary>The path defining the lane's geometry.</summary>
    public TrafficPath path;

    /// <summary>List of possible next lanes for straight continuation.</summary>
    public List<TrafficLane> nextLanes = new List<TrafficLane>();
    /// <summary>List of possible turn paths with target lanes.</summary>
    public List<LanePath> nextPaths = new List<LanePath>();
}