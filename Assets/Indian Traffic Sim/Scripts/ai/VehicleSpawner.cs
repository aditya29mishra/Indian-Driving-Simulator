using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// VehicleSpawner — vehicle instantiation, driver profile randomisation.
//
// Spawn lane discovery: driven by LaneManager.roads + TrafficRoadNode.NodeType.
//   No manual spawnRoads list needed.
//   At Start(), walks every road registered in LaneManager. For each road,
//   finds which endpoint is NodeType.End. Forward lanes whose waypoints[0]
//   is closest to that End node are the spawn lanes. Works regardless of
//   whether startNode or endNode is the End node.
//
// Destination pool: same walk — the End node of every road that is NOT
//   the spawn lane's road is a valid destination.
//
// Talks to: LaneManager (road list), TrafficVehicle, TrafficSignal, TrafficRoadNode
// ─────────────────────────────────────────────────────────────────────────────

[System.Serializable]
public class DriverProfileDistribution
{
    [Range(0f, 1f)] public float cautiousWeight   = 0.30f;
    [Range(0f, 1f)] public float normalWeight     = 0.50f;
    [Range(0f, 1f)] public float aggressiveWeight = 0.20f;
}

[System.Serializable]
public class SpeedDistribution
{
    [Header("Cautious")]   public float cautiousMin   = 7f;  public float cautiousMax   = 11f;
    [Header("Normal")]     public float normalMin     = 11f; public float normalMax     = 16f;
    [Header("Aggressive")] public float aggressiveMin = 15f; public float aggressiveMax = 22f;
}

[System.Serializable]
public class BehaviourDistribution
{
    [Header("Acceleration m/s²")]
    public float cautiousAccel    = 1.5f;
    public float normalAccel      = 2.5f;
    public float aggressiveAccel  = 4.5f;
    public float accelVariance    = 0.2f;
    [Header("Braking m/s²")]
    public float cautiousbraking   = 4f;
    public float normalBraking     = 6f;
    public float aggressiveBraking = 9f;
    public float brakingVariance   = 0.15f;
    [Header("Time Headway s")]
    public float cautiousHeadway   = 2.2f;
    public float normalHeadway     = 1.5f;
    public float aggressiveHeadway = 0.8f;
    public float headwayVariance   = 0.3f;
    [Header("Min Gap m")]
    public float cautiousGap   = 4f;
    public float normalGap     = 2f;
    public float aggressiveGap = 1f;
    [Header("Lateral offset m")]
    public float lateralVariance = 0.3f;
}

public class VehicleSpawner : MonoBehaviour
{
    [Header("Systems")]
    [Tooltip("LaneManager that holds the full road list. Auto-found if left null.")]
    public LaneManager laneManager;
    public List<TrafficSignal> trafficSignals = new List<TrafficSignal>();

    [Header("Vehicle Types")]
    public GameObject[] vehiclePrefabs;
    [Tooltip("Spawn probability weight for each prefab")]
    public float[] vehicleWeights;

    [Header("Traffic Limits")]
    public int   maxVehiclesTotal   = 40;
    public int   maxVehiclesPerLane = 8;

    [Header("Spawn Timing")]
    public float spawnInterval = 1.5f;
    private float spawnTimer;

    [Header("Safety")]
    public float spawnClearDistance = 15f;

    [Header("Driver Distribution")]
    public DriverProfileDistribution profileDistribution   = new DriverProfileDistribution();
    public SpeedDistribution         speedDistribution     = new SpeedDistribution();
    public BehaviourDistribution     behaviourDistribution = new BehaviourDistribution();

    [Header("Vehicle Physics")]
    public float vehicleLength        = 4.5f;
    public float stopLineDistanceBase = 14f;

    // Internal
    private Dictionary<TrafficLane, int>            laneOccupancy = new Dictionary<TrafficLane, int>();
    private Dictionary<TrafficLane, TrafficVehicle> lastSpawned   = new Dictionary<TrafficLane, TrafficVehicle>();

    // Spawn lanes — forward lanes whose waypoints[0] sits at a RoadEnd node
    private List<TrafficLane> spawnLanes = new List<TrafficLane>();

    // Destination nodes — RoadEnd nodes, one per road, used by PickDestinationNode
    private List<TrafficRoadNode> allDestinationNodes = new List<TrafficRoadNode>();

    // ─────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────

    void Start()
    {
        if (laneManager == null)
            laneManager = FindObjectOfType<LaneManager>();

        if (laneManager == null)
        {
            Debug.LogError("[VehicleSpawner] No LaneManager found in scene.");
            return;
        }

        if (trafficSignals == null || trafficSignals.Count == 0)
            trafficSignals = new List<TrafficSignal>(FindObjectsOfType<TrafficSignal>());

        BuildSpawnAndDestinationLists();
    }

    void Update()
    {
        if (TotalVehicles() >= maxVehiclesTotal) return;
        spawnTimer += Time.deltaTime;
        if (spawnTimer < spawnInterval) return;
        spawnTimer = 0f;
        TrySpawnVehicle();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Spawn + destination discovery — driven by LaneManager.roads
    // ─────────────────────────────────────────────────────────────────────

    void BuildSpawnAndDestinationLists()
    {
        spawnLanes.Clear();
        allDestinationNodes.Clear();
        laneOccupancy.Clear();
        lastSpawned.Clear();

        if (laneManager.roads == null || laneManager.roads.Count == 0)
        {
            Debug.LogWarning("[VehicleSpawner] LaneManager.roads is empty. " +
                             "Run Generate All on LaneManager first.");
            return;
        }

        foreach (var road in laneManager.roads)
        {
            if (road == null) continue;

            // Find which endpoint of this road is the RoadEnd node
            TrafficRoadNode endNode = GetRoadEndNode(road);
            if (endNode == null) continue; // road has no End-type node — skip

            // This End node is a valid destination for vehicles coming from other roads
            allDestinationNodes.Add(endNode);

            // Find forward lanes whose waypoints[0] is the end closer to the RoadEnd node.
            // Instead of a fixed distance threshold (which fails for wide/offset roads),
            // compare both ends of the lane path — whichever end is closer to the End node
            // determines direction. If waypoints[0] is closer → lane starts at End node → spawn lane.
            foreach (var lane in road.lanes)
            {
                if (lane == null || !lane.forwardDirection) continue;
                if (lane.path == null || lane.path.waypoints == null ||
                    lane.path.waypoints.Count < 2) continue;

                Vector3 wp0   = lane.path.waypoints[0].position;
                Vector3 wpLast = lane.path.waypoints[lane.path.waypoints.Count - 1].position;
                Vector3 endPos = endNode.transform.position;

                // Spawn lane = waypoints[0] is closer to the End node than waypoints[last]
                if (Vector3.Distance(wp0, endPos) < Vector3.Distance(wpLast, endPos))
                {
                    spawnLanes.Add(lane);
                    laneOccupancy[lane] = 0;
                    lastSpawned[lane]   = null;
                }
            }
        }

        if (spawnLanes.Count == 0)
            Debug.LogWarning("[VehicleSpawner] No spawn lanes found. " +
                             "Ensure roads have NodeType.End nodes and lanes are generated.");
        else
            Debug.Log($"[VehicleSpawner] Ready — {spawnLanes.Count} spawn lane(s), " +
                      $"{allDestinationNodes.Count} destination node(s).");
    }

    /// <summary>
    /// Returns the TrafficRoadNode with NodeType.End on this road, or null if none.
    /// Works regardless of whether it is startNode or endNode.
    /// </summary>
    TrafficRoadNode GetRoadEndNode(TrafficRoad road)
    {
        if (road.startNode != null && road.startNode.nodeType == TrafficRoadNode.NodeType.End)
            return road.startNode;
        if (road.endNode   != null && road.endNode.nodeType   == TrafficRoadNode.NodeType.End)
            return road.endNode;
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Destination selection
    // ─────────────────────────────────────────────────────────────────────

    TrafficRoadNode PickDestinationNode(TrafficLane spawnLane)
    {
        if (allDestinationNodes.Count == 0) return null;

        TrafficRoad spawnRoad = spawnLane?.road;
        var candidates = new List<TrafficRoadNode>(allDestinationNodes.Count);

        foreach (var node in allDestinationNodes)
        {
            if (node == null) continue;
            // Exclude the End node of the spawn road — vehicle is already there
            if (spawnRoad != null &&
                (node == spawnRoad.startNode || node == spawnRoad.endNode)) continue;
            candidates.Add(node);
        }

        return candidates.Count == 0 ? null : candidates[Random.Range(0, candidates.Count)];
    }

    // ─────────────────────────────────────────────────────────────────────
    // Spawn
    // ─────────────────────────────────────────────────────────────────────

    void TrySpawnVehicle()
    {
        if (spawnLanes.Count == 0) return;
        var lane = spawnLanes[Random.Range(0, spawnLanes.Count)];
        if (!laneOccupancy.ContainsKey(lane)) return;
        if (laneOccupancy[lane] >= maxVehiclesPerLane) return;
        if (!IsSpawnPointClear(lane)) return;
        SpawnVehicle(lane);
    }

    public void ForceSpawn(int count)
    {
        if (spawnLanes.Count == 0) return;
        for (int k = 0; k < count; k++)
        {
            if (TotalVehicles() >= maxVehiclesTotal) return;
            var lane = spawnLanes[k % spawnLanes.Count];
            if (!laneOccupancy.ContainsKey(lane)) continue;
            if (laneOccupancy[lane] >= maxVehiclesPerLane) continue;
            if (!IsSpawnPointClear(lane)) continue;
            SpawnVehicle(lane);
        }
    }

    bool IsSpawnPointClear(TrafficLane lane)
    {
        if (lane.path == null || lane.path.waypoints.Count == 0) return false;
        var last = lastSpawned.ContainsKey(lane) ? lastSpawned[lane] : null;
        if (last == null) return true;
        return Vector3.Distance(last.transform.position,
                                lane.path.waypoints[0].position) >= spawnClearDistance;
    }

    void SpawnVehicle(TrafficLane lane)
    {
        var prefab = ChooseVehiclePrefab();
        if (prefab == null) return;

        Vector3    spawnPos = lane.path.waypoints[0].position;
        Quaternion rot      = Quaternion.identity;
        if (lane.path.waypoints.Count > 1)
        {
            var dir = (lane.path.waypoints[1].position - spawnPos).normalized;
            if (dir != Vector3.zero) rot = Quaternion.LookRotation(dir);
        }

        var vehicleGO = Instantiate(prefab, spawnPos, rot);
        var vehicle   = vehicleGO.GetComponent<TrafficVehicle>();
        if (vehicle == null)
        {
            Debug.LogWarning("[VehicleSpawner] Prefab missing TrafficVehicle.");
            Destroy(vehicleGO);
            return;
        }

        vehicle.Initialize(BuildConfig(lane));
        vehicle.context.DestNode = PickDestinationNode(lane);
        vehicle.currentSignal    = FindSignalForLane(lane);
        vehicle.laneManager      = laneManager;
        vehicle.AssignLane(lane, fromSpawn: true);

        if (laneManager != null) laneManager.RegisterVehicle(vehicle);

        laneOccupancy[lane]++;
        lastSpawned[lane] = vehicle;
        vehicleGO.AddComponent<SpawnTracker>().Init(this, lane);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Signal lookup
    // ─────────────────────────────────────────────────────────────────────

    TrafficSignal FindSignalForLane(TrafficLane lane)
    {
        if (lane == null || trafficSignals == null) return null;
        foreach (var sig in trafficSignals)
        {
            if (sig == null) continue;
            foreach (var group in sig.groups)
                foreach (var l in group.lanes)
                    if (l == lane) return sig;
        }
        return trafficSignals.Count > 0 ? trafficSignals[0] : null;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Config builder
    // ─────────────────────────────────────────────────────────────────────

    TrafficVehicle.VehicleConfig BuildConfig(TrafficLane lane)
    {
        var profile = ChooseProfile();
        var b = behaviourDistribution;
        var s = speedDistribution;

        float baseAccel, baseBraking, baseHeadway, minGap, baseMaxSpeed;

        switch (profile)
        {
            case TrafficVehicle.DriverProfile.Cautious:
                baseMaxSpeed = Random.Range(s.cautiousMin,   s.cautiousMax);
                baseAccel    = b.cautiousAccel;    baseBraking = b.cautiousbraking;
                baseHeadway  = b.cautiousHeadway;  minGap      = b.cautiousGap;   break;
            case TrafficVehicle.DriverProfile.Aggressive:
                baseMaxSpeed = Random.Range(s.aggressiveMin, s.aggressiveMax);
                baseAccel    = b.aggressiveAccel;  baseBraking = b.aggressiveBraking;
                baseHeadway  = b.aggressiveHeadway;minGap      = b.aggressiveGap; break;
            default:
                baseMaxSpeed = Random.Range(s.normalMin,     s.normalMax);
                baseAccel    = b.normalAccel;      baseBraking = b.normalBraking;
                baseHeadway  = b.normalHeadway;    minGap      = b.normalGap;     break;
        }

        float accel   = Mathf.Max(baseAccel   * Random.Range(1f - b.accelVariance,   1f + b.accelVariance), 0.5f);
        float braking = Mathf.Max(baseBraking * Random.Range(1f - b.brakingVariance, 1f + b.brakingVariance), 2f);
        float headway = Mathf.Max(baseHeadway + Random.Range(-b.headwayVariance,      b.headwayVariance), 0.4f);

        float desiredSpeed = baseMaxSpeed * Random.Range(0.85f, 1.0f);
        if (lane?.road != null && lane.road.speedLimit > 0f)
        {
            float limitMs  = lane.road.speedLimit / 3.6f;
            float variance = profile == TrafficVehicle.DriverProfile.Aggressive ? Random.Range(1.0f, 1.2f)
                           : profile == TrafficVehicle.DriverProfile.Normal      ? Random.Range(0.9f, 1.05f)
                           :                                                        Random.Range(0.75f, 0.92f);
            desiredSpeed = Mathf.Min(baseMaxSpeed, limitMs * variance);
        }

        float maxSpeedKph = Mathf.Min(baseMaxSpeed * 3.6f, 70f);

        return new TrafficVehicle.VehicleConfig
        {
            profile          = profile,
            maxSpeedKph      = maxSpeedKph,
            desiredSpeedMs   = Mathf.Min(desiredSpeed, maxSpeedKph / 3.6f),
            acceleration     = accel,
            braking          = braking,
            timeHeadway      = headway,
            minimumGap       = minGap,
            vehicleLength    = vehicleLength,
            stopLineDistance = stopLineDistanceBase,
        };
    }

    TrafficVehicle.DriverProfile ChooseProfile()
    {
        var d = profileDistribution;
        float total = d.cautiousWeight + d.normalWeight + d.aggressiveWeight;
        float r = Random.value * total;
        if (r < d.cautiousWeight)                  return TrafficVehicle.DriverProfile.Cautious;
        if (r < d.cautiousWeight + d.normalWeight) return TrafficVehicle.DriverProfile.Normal;
        return TrafficVehicle.DriverProfile.Aggressive;
    }

    GameObject ChooseVehiclePrefab()
    {
        if (vehiclePrefabs == null || vehiclePrefabs.Length == 0) return null;
        if (vehicleWeights == null || vehicleWeights.Length != vehiclePrefabs.Length)
            return vehiclePrefabs[Random.Range(0, vehiclePrefabs.Length)];
        float total = 0f;
        foreach (var w in vehicleWeights) total += w;
        float r = Random.value * total, acc = 0f;
        for (int i = 0; i < vehiclePrefabs.Length; i++)
        {
            acc += vehicleWeights[i];
            if (r <= acc) return vehiclePrefabs[i];
        }
        return vehiclePrefabs[vehiclePrefabs.Length - 1];
    }

    int TotalVehicles()
    {
        int t = 0;
        foreach (var c in laneOccupancy.Values) t += c;
        return t;
    }

    public void OnVehicleDestroyed(TrafficLane lane)
    {
        if (!laneOccupancy.ContainsKey(lane)) return;
        laneOccupancy[lane] = Mathf.Max(0, laneOccupancy[lane] - 1);
        if (lastSpawned.ContainsKey(lane)) lastSpawned[lane] = null;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Gizmos — always visible, shows spawn points before Play
    // ─────────────────────────────────────────────────────────────────────
    //   Cyan sphere = spawn point (waypoints[0] of each spawn lane)
    //   Cyan arrow  = spawn direction
    //   Cyan ring   = spawnClearDistance safety radius
    //   Red sphere  = road has a RoadEnd node but no matching spawn lanes

    void OnDrawGizmos()
    {
        // Draw from spawnLanes if already built (Play mode or after Start)
        if (spawnLanes != null && spawnLanes.Count > 0)
        {
            DrawSpawnLaneGizmos(spawnLanes);
            return;
        }

        // Editor mode — derive spawn lanes on the fly from LaneManager
        if (laneManager == null) return;
        if (laneManager.roads == null) return;

        var preview = new List<TrafficLane>();
        foreach (var road in laneManager.roads)
        {
            if (road == null) continue;
            var endNode = GetRoadEndNode(road);
            if (endNode == null)
            {
                // No End node — draw grey marker on road
                Gizmos.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
                Gizmos.DrawWireSphere(road.transform.position + Vector3.up, 0.8f);
                continue;
            }

            bool foundSpawnLane = false;
            foreach (var lane in road.lanes)
            {
                if (lane == null || !lane.forwardDirection) continue;
                if (lane.path == null || lane.path.waypoints == null ||
                    lane.path.waypoints.Count < 2) continue;

                Vector3 wp0    = lane.path.waypoints[0].position;
                Vector3 wpLast = lane.path.waypoints[lane.path.waypoints.Count - 1].position;
                Vector3 endPos = endNode.transform.position;

                if (Vector3.Distance(wp0, endPos) < Vector3.Distance(wpLast, endPos))
                {
                    preview.Add(lane);
                    foundSpawnLane = true;
                }
            }

            // Road has End node but no lanes generated yet
            if (!foundSpawnLane && road.lanes.Count == 0)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(endNode.transform.position + Vector3.up, 1.0f);
#if UNITY_EDITOR
                UnityEditor.Handles.Label(endNode.transform.position + Vector3.up * 2.2f,
                    $"{road.name}\n[no lanes — run Generate All]");
#endif
            }
        }

        DrawSpawnLaneGizmos(preview);
    }

    void DrawSpawnLaneGizmos(List<TrafficLane> lanes)
    {
        foreach (var lane in lanes)
        {
            if (lane?.path == null || lane.path.waypoints == null ||
                lane.path.waypoints.Count == 0) continue;

            Vector3 spawnPos = lane.path.waypoints[0].position;
            Vector3 spawnDir = Vector3.forward;
            if (lane.path.waypoints.Count > 1)
                spawnDir = (lane.path.waypoints[1].position - spawnPos).normalized;

            Vector3 origin = spawnPos + Vector3.up * 0.3f;

            // Sphere
            Gizmos.color = new Color(0f, 0.9f, 1f, 0.9f);
            Gizmos.DrawSphere(origin, 0.5f);

            // Arrow
            Vector3 tip   = origin + spawnDir * 3f;
            Vector3 right = Vector3.Cross(spawnDir, Vector3.up).normalized;
            Gizmos.color  = new Color(0f, 0.9f, 1f, 0.7f);
            Gizmos.DrawLine(origin, tip);
            Gizmos.DrawLine(tip, tip - spawnDir * 0.8f + right * 0.4f);
            Gizmos.DrawLine(tip, tip - spawnDir * 0.8f - right * 0.4f);

            // Clear radius ring
            Gizmos.color = new Color(0f, 0.9f, 1f, 0.1f);
            Gizmos.DrawWireSphere(origin, spawnClearDistance);

#if UNITY_EDITOR
            UnityEditor.Handles.Label(origin + Vector3.up * 1.2f,
                $"SPAWN\n{lane.name}");
#endif
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────

public class SpawnTracker : MonoBehaviour
{
    private VehicleSpawner spawner;
    private TrafficLane    lane;
    public void Init(VehicleSpawner s, TrafficLane l) { spawner = s; lane = l; }
    void OnDestroy() { if (spawner != null) spawner.OnVehicleDestroyed(lane); }
}