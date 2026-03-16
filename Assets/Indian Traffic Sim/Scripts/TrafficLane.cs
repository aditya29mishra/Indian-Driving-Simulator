using System.Collections.Generic;
using UnityEngine;

public class TrafficLane : MonoBehaviour
{
    [System.Serializable]
    public class LanePath
    {
        public TrafficPath path;
        public TrafficLane targetLane;
    }

    public TrafficRoad road;

    public int laneIndex = 0;

    public float laneWidth = 3.5f;

    public bool forwardDirection = true;

    public TrafficPath path;

    public List<TrafficLane> nextLanes = new List<TrafficLane>();
    public List<LanePath> nextPaths = new List<LanePath>();
}