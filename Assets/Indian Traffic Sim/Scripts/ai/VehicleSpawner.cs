using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// VehicleSpawner — vehicle instantiation, type distribution, driver profiles.
//
// Spawn lane discovery: driven by LaneManager.roads + TrafficRoadNode.NodeType.
//   No manual spawnRoads list needed.
//   At Start(), walks every road registered in LaneManager. For each road,
//   finds which endpoint is NodeType.End. Forward lanes whose waypoints[0]
//   is closest to that End node are the spawn lanes.
//
// Vehicle type system:
//   VehicleTypeDistribution picks a VehicleType each spawn.
//   VehicleProfileFactory builds the full profile for that type.
//   BuildConfigForType() applies personality variance on top of the profile.
//   Narrow lanes reject vehicles too wide to fit (buses can't spawn in bike lanes).
//
// Destination pool: End node of every road that is NOT the spawn lane's road.
//
// Talks to: LaneManager, TrafficVehicle, TrafficSignal, TrafficRoadNode,
//           VehicleProfile, VehicleTypeDistribution
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

    [Header("Vehicle Types — NEW")]
    [Tooltip("Type distribution and per-type prefab pools. " +
             "Default weights reflect Indian urban traffic composition.")]
    public VehicleTypeDistribution typeDistribution = new VehicleTypeDistribution();

    [Header("Vehicle Types — Legacy (used when typeDistribution prefab pools are empty)")]
    public GameObject[] vehiclePrefabs;
    [Tooltip("Spawn probability weight for each legacy prefab")]
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

    [Header("Vehicle Physics — Legacy fallback")]
    public float vehicleLength        = 4.5f;
    public float stopLineDistanceBase = 14f;

    // Internal
    private Dictionary<TrafficLane, int>            laneOccupancy = new Dictionary<TrafficLane, int>();
    private Dictionary<TrafficLane, TrafficVehicle> lastSpawned   = new Dictionary<TrafficLane, TrafficVehicle>();

    private List<TrafficLane>      spawnLanes           = new List<TrafficLane>();
    private List<TrafficRoadNode>  allDestinationNodes  = new List<TrafficRoadNode>();

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
        typeDistribution.Validate();
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
    // Spawn + destination discovery
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

            TrafficRoadNode endNode = GetRoadEndNode(road);
            if (endNode == null) continue;

            allDestinationNodes.Add(endNode);

            foreach (var lane in road.lanes)
            {
                if (lane == null) continue;
                if (lane.path == null || lane.path.waypoints == null ||
                    lane.path.waypoints.Count < 2) continue;

                Vector3 wp0    = lane.path.waypoints[0].position;
                Vector3 wpLast = lane.path.waypoints[lane.path.waypoints.Count - 1].position;
                Vector3 endPos = endNode.transform.position;

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

        // Pick vehicle type first — lane selection depends on vehicle width
        VehicleType    type    = typeDistribution.PickType();
        VehicleProfile profile = VehicleProfileFactory.Create(type);

        // Find a spawn lane this vehicle fits in
        TrafficLane lane = PickSpawnLane(profile);
        if (lane == null) return;

        SpawnVehicle(lane, type, profile);
    }

    public void ForceSpawn(int count)
    {
        if (spawnLanes.Count == 0) return;
        for (int k = 0; k < count; k++)
        {
            if (TotalVehicles() >= maxVehiclesTotal) return;

            VehicleType    type    = typeDistribution.PickType();
            VehicleProfile profile = VehicleProfileFactory.Create(type);
            TrafficLane    lane    = PickSpawnLane(profile);
            if (lane == null) continue;

            SpawnVehicle(lane, type, profile);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Lane selection — filters by vehicle width
    // ─────────────────────────────────────────────────────────────────────

    TrafficLane PickSpawnLane(VehicleProfile profile)
    {
        // Shuffle so we don't always pick index 0
        var candidates = new List<TrafficLane>(spawnLanes);
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j   = Random.Range(0, i + 1);
            var tmp = candidates[i]; candidates[i] = candidates[j]; candidates[j] = tmp;
        }

        // Minimum lane width = vehicle width + 0.3m clearance each side
        float minLaneWidth = profile.Physical.width + 0.3f;

        foreach (var lane in candidates)
        {
            if (lane == null || lane.path == null) continue;

            // Check lane width
            float laneW = lane.road != null ? lane.road.laneWidth : 3.5f;
            if (laneW < minLaneWidth) continue;

            // Check occupancy cap
            laneOccupancy.TryGetValue(lane, out int occ);
            if (occ >= maxVehiclesPerLane) continue;

            // Check last-spawned clearance
            if (lastSpawned.TryGetValue(lane, out var last) && last != null)
            {
                float dist = Vector3.Distance(
                    lane.path.waypoints[0].position, last.transform.position);
                if (dist < spawnClearDistance) continue;
            }

            return lane;
        }
        return null;
    }

    bool IsSpawnPointClear(TrafficLane lane)
    {
        if (lane.path == null || lane.path.waypoints.Count == 0) return false;
        var last = lastSpawned.ContainsKey(lane) ? lastSpawned[lane] : null;
        if (last == null) return true;
        return Vector3.Distance(last.transform.position,
                                lane.path.waypoints[0].position) >= spawnClearDistance;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Instantiation
    // ─────────────────────────────────────────────────────────────────────

    void SpawnVehicle(TrafficLane lane, VehicleType type, VehicleProfile profile)
    {
        // Pick prefab from type pool, fallback to legacy pool
        GameObject prefab = typeDistribution.PickPrefab(type);
        if (prefab == null) prefab = ChooseLegacyPrefab();
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

        var driverProfile = ChooseProfile();
        var cfg           = BuildConfigForType(type, profile, driverProfile, lane);

        vehicle.Initialize(cfg);
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
    // Config builder — type-aware
    // ─────────────────────────────────────────────────────────────────────

    TrafficVehicle.VehicleConfig BuildConfigForType(
        VehicleType type,
        VehicleProfile profile,
        TrafficVehicle.DriverProfile driverProfile,
        TrafficLane lane)
    {
        // Speed variance: driver personality shifts position within the type envelope
        float speedVariance = driverProfile == TrafficVehicle.DriverProfile.Aggressive
            ? Random.Range(1.05f, 1.20f)
            : driverProfile == TrafficVehicle.DriverProfile.Cautious
            ? Random.Range(0.70f, 0.88f)
            : Random.Range(0.88f, 1.02f);

        float desiredSpeedMs = profile.Agility.desiredSpeedMs * speedVariance;

        // Clamp to road speed limit if available
        if (lane?.road != null && lane.road.speedLimit > 0f)
        {
            float limitMs = lane.road.speedLimit / 3.6f;
            desiredSpeedMs = Mathf.Min(desiredSpeedMs, limitMs * speedVariance);
        }

        // Headway variance on top of type social baseline
        float headwayVariance = driverProfile == TrafficVehicle.DriverProfile.Aggressive
            ? Random.Range(0.70f, 0.90f)
            : driverProfile == TrafficVehicle.DriverProfile.Cautious
            ? Random.Range(1.20f, 1.50f)
            : Random.Range(0.90f, 1.10f);

        return new TrafficVehicle.VehicleConfig
        {
            profile          = driverProfile,
            vehicleProfile   = profile,
            maxSpeedKph      = profile.Agility.maxSpeedKph,
            desiredSpeedMs   = Mathf.Min(desiredSpeedMs, profile.Agility.maxSpeedKph / 3.6f),
            acceleration     = profile.Agility.acceleration * Random.Range(0.90f, 1.15f),
            braking          = profile.Agility.braking      * Random.Range(0.90f, 1.10f),
            timeHeadway      = profile.Social.timeHeadway   * headwayVariance,
            minimumGap       = profile.Social.minimumGap,
            vehicleLength    = profile.Physical.length,
            stopLineDistance = stopLineDistanceBase,
            lateralVariance  = profile.Agility.maxLateralOffsetM * profile.Agility.lateralAgility,
            destNode         = null   // assigned after Initialize()
        };
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
    // Profile selection
    // ─────────────────────────────────────────────────────────────────────

    TrafficVehicle.DriverProfile ChooseProfile()
    {
        var d     = profileDistribution;
        float total = d.cautiousWeight + d.normalWeight + d.aggressiveWeight;
        float r     = Random.value * total;
        if (r < d.cautiousWeight)                  return TrafficVehicle.DriverProfile.Cautious;
        if (r < d.cautiousWeight + d.normalWeight) return TrafficVehicle.DriverProfile.Normal;
        return TrafficVehicle.DriverProfile.Aggressive;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Legacy prefab fallback
    // ─────────────────────────────────────────────────────────────────────

    GameObject ChooseLegacyPrefab()
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
    // Gizmos
    // ─────────────────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (spawnLanes != null && spawnLanes.Count > 0)
        {
            DrawSpawnLaneGizmos(spawnLanes);
            return;
        }

        if (laneManager == null || laneManager.roads == null) return;

        var preview = new List<TrafficLane>();
        foreach (var road in laneManager.roads)
        {
            if (road == null) continue;
            var endNode = GetRoadEndNode(road);
            if (endNode == null)
            {
                Gizmos.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
                Gizmos.DrawWireSphere(road.transform.position + Vector3.up, 0.8f);
                continue;
            }

            bool foundSpawnLane = false;
            foreach (var lane in road.lanes)
            {
                if (lane == null) continue;
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

            Gizmos.color = new Color(0f, 0.9f, 1f, 0.9f);
            Gizmos.DrawSphere(origin, 0.5f);

            Vector3 tip   = origin + spawnDir * 3f;
            Vector3 right = Vector3.Cross(spawnDir, Vector3.up).normalized;
            Gizmos.color  = new Color(0f, 0.9f, 1f, 0.7f);
            Gizmos.DrawLine(origin, tip);
            Gizmos.DrawLine(tip, tip - spawnDir * 0.8f + right * 0.4f);
            Gizmos.DrawLine(tip, tip - spawnDir * 0.8f - right * 0.4f);

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