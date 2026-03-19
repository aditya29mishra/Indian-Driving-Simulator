using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// VehicleSpawner — vehicle instantiation, driver profile randomisation, signal matching per lane
//
// Spawns vehicles with randomized driver profiles and physics configs.
// Assigns appropriate traffic signals to vehicles based on their lanes.
// Manages spawn limits and safety distances.
//
// Talks to: TrafficVehicle (spawns), TrafficLane (lanes), TrafficSignal (signals), LaneManager (registration)
// ─────────────────────────────────────────────────────────────────────────────

[System.Serializable]
public class DriverProfileDistribution
{
    [Range(0f, 1f)] public float cautiousWeight = 0.30f;
    [Range(0f, 1f)] public float normalWeight = 0.50f;
    [Range(0f, 1f)] public float aggressiveWeight = 0.20f;
}

[System.Serializable]
public class SpeedDistribution
{
    [Header("Cautious")]
    public float cautiousMin = 7f;
    public float cautiousMax = 11f;

    [Header("Normal")]
    public float normalMin = 11f;
    public float normalMax = 16f;

    [Header("Aggressive")]
    public float aggressiveMin = 15f;
    public float aggressiveMax = 22f;
}

[System.Serializable]
public class BehaviourDistribution
{
    [Header("Acceleration m/s²")]
    public float cautiousAccel = 1.5f;
    public float normalAccel = 2.5f;
    public float aggressiveAccel = 4.5f;
    public float accelVariance = 0.2f;   // ± fraction of base

    [Header("Braking m/s²")]
    public float cautiousbraking = 4f;
    public float normalBraking = 6f;
    public float aggressiveBraking = 9f;
    public float brakingVariance = 0.15f;

    [Header("Time Headway s")]
    public float cautiousHeadway = 2.2f;
    public float normalHeadway = 1.5f;
    public float aggressiveHeadway = 0.8f;
    public float headwayVariance = 0.3f;  // ± absolute seconds

    [Header("Min Gap m")]
    public float cautiousGap = 4f;
    public float normalGap = 2f;
    public float aggressiveGap = 1f;

    [Header("Lateral offset m (Indian lane drift)")]
    public float lateralVariance = 0.3f;
}

public class VehicleSpawner : MonoBehaviour
{
    [Header("Road")]
    public List<TrafficRoad> spawnRoads = new List<TrafficRoad>();

    [Header("Vehicle Types")]
    public GameObject[] vehiclePrefabs;
    [Tooltip("Spawn probability weight for each prefab")]
    public float[] vehicleWeights;

    [Header("Traffic Limits")]
    public int maxVehiclesTotal = 40;
    public int maxVehiclesPerLane = 8;

    [Header("Spawn Timing")]
    public float spawnInterval = 1.5f;
    private float spawnTimer;

    [Header("Safety")]
    public float spawnClearDistance = 15f;

    [Header("Systems")]
    public LaneManager laneManager;
    [Tooltip("Assign all TrafficSignal objects in the scene here — one per approach direction if using per-direction signals, or just the one shared signal for a standard 4-way. Each vehicle will be matched to the signal that contains its lane.")]
    public List<TrafficSignal> trafficSignals = new List<TrafficSignal>();

    [Header("Driver Distribution")]
    public DriverProfileDistribution profileDistribution = new DriverProfileDistribution();
    public SpeedDistribution speedDistribution = new SpeedDistribution();
    public BehaviourDistribution behaviourDistribution = new BehaviourDistribution();

    [Header("Vehicle Physics")]
    public float vehicleLength = 4.5f;
    public float stopLineDistanceBase = 14f;

    // Internal
    private Dictionary<TrafficLane, int> laneOccupancy = new Dictionary<TrafficLane, int>();
    private Dictionary<TrafficLane, TrafficVehicle> lastSpawned = new Dictionary<TrafficLane, TrafficVehicle>();

    private List<TrafficLane> lanes = new List<TrafficLane>();
    private TrafficNode[] cachedRoadEndNodes;

    void Start()
    {
        if (spawnRoads == null || spawnRoads.Count == 0) return;
        foreach (var road in spawnRoads)
        {
            if (road == null) continue;
            foreach (var lane in road.lanes)
            {
                if (!lane.forwardDirection) continue;
                lanes.Add(lane);
                laneOccupancy[lane] = 0;
                lastSpawned[lane] = null;
            }
        }

        // Auto-collect all signals in scene if none manually assigned
        if (trafficSignals == null || trafficSignals.Count == 0)
        {
            trafficSignals = new List<TrafficSignal>(FindObjectsOfType<TrafficSignal>());
        }
        if (laneManager == null) laneManager = FindObjectOfType<LaneManager>();
        // In Start() — no System.Linq needed:
        var allNodes = FindObjectsOfType<TrafficNode>();
        var endNodes = new List<TrafficNode>();
        foreach (var n in allNodes)
            if (n.nodeType == TrafficNode.NodeType.RoadEnd)
                endNodes.Add(n);
        cachedRoadEndNodes = endNodes.ToArray(); // List<T>.ToArray() is in System.Collections.Generic — no LINQ needed
    }

    void Update()
    {
        if (TotalVehicles() >= maxVehiclesTotal) return;
        spawnTimer += Time.deltaTime;
        if (spawnTimer < spawnInterval) return;
        spawnTimer = 0f;
        TrySpawnVehicle();
    }

    void TrySpawnVehicle()
    {
        if (lanes.Count == 0) return;
        TrafficLane lane = lanes[Random.Range(0, lanes.Count)];
        if (laneOccupancy[lane] >= maxVehiclesPerLane) return;
        if (!IsSpawnPointClear(lane)) return;
        SpawnVehicle(lane);
    }

    /// <summary>Forces the spawning of a specified number of vehicles.</summary>
    /// <param name="count">Number of vehicles to spawn.</param>
    public void ForceSpawn(int count)
    {
        if (lanes.Count == 0) return;
        for (int k = 0; k < count; k++)
        {
            if (TotalVehicles() >= maxVehiclesTotal) return;
            TrafficLane lane = lanes[k % lanes.Count];
            if (laneOccupancy[lane] >= maxVehiclesPerLane) continue;
            if (!IsSpawnPointClear(lane)) continue;
            SpawnVehicle(lane);
        }
    }

    bool IsSpawnPointClear(TrafficLane lane)
    {
        if (lane.path == null || lane.path.waypoints.Count == 0) return false;
        TrafficVehicle last = lastSpawned[lane];
        if (last == null) return true;
        return Vector3.Distance(last.transform.position, lane.path.waypoints[0].position) >= spawnClearDistance;
    }

    void SpawnVehicle(TrafficLane lane)
    {
        GameObject prefab = ChooseVehiclePrefab();
        if (prefab == null) return;

        Vector3 spawnPos = lane.path.waypoints[0].position;
        Quaternion rot = Quaternion.identity;
        if (lane.path.waypoints.Count > 1)
        {
            Vector3 dir = (lane.path.waypoints[1].position - spawnPos).normalized;
            if (dir != Vector3.zero) rot = Quaternion.LookRotation(dir);
        }

        GameObject vehicleGO = Instantiate(prefab, spawnPos, rot);
        TrafficVehicle vehicle = vehicleGO.GetComponent<TrafficVehicle>();
        if (vehicle == null) { Debug.LogWarning("Prefab missing TrafficVehicle."); Destroy(vehicleGO); return; }

        // Build and apply config — all randomness here
        var cfg = BuildConfig(lane);
        vehicle.Initialize(cfg);

        // Assign the signal that actually contains this vehicle's lane.
        // With a 4-way Indian intersection using per-direction signals, each
        // approach has its own TrafficSignal. Assigning the wrong one (or one
        // shared signal that doesn't contain the lane) makes the vehicle always
        // read Green and never stop.
        // After: vehicle.Initialize(cfg);

        // Assign a destination — a RoadEnd node that is NOT on the spawn road
        vehicle.context.DestNode = PickDestinationNode(lane);
        vehicle.currentSignal = FindSignalForLane(lane);
        vehicle.laneManager = laneManager;
        vehicle.AssignLane(lane, fromSpawn: true);

        if (laneManager != null) laneManager.RegisterVehicle(vehicle);

        laneOccupancy[lane]++;
        lastSpawned[lane] = vehicle;
        vehicleGO.AddComponent<SpawnTracker>().Init(this, lane);
    }

    // ─── Signal lookup — find the signal that owns this lane ─────────────────
    TrafficSignal FindSignalForLane(TrafficLane lane)
    {
        if (lane == null || trafficSignals == null) return null;
        foreach (var sig in trafficSignals)
        {
            if (sig == null) continue;
            // Check if any group in this signal contains the lane
            foreach (var group in sig.groups)
                foreach (var l in group.lanes)
                    if (l == lane) return sig;
        }
        // Lane not found in any signal — return first signal as fallback (will warn via GetStateForLane)
        return trafficSignals.Count > 0 ? trafficSignals[0] : null;
    }

    // ─── Config builder — all randomness lives here ───────────────────────

    TrafficVehicle.VehicleConfig BuildConfig(TrafficLane lane)
    {
        var profile = ChooseProfile();
        var b = behaviourDistribution;
        var s = speedDistribution;

        float baseAccel, baseBraking, baseHeadway, minGap, baseMaxSpeed;

        switch (profile)
        {
            case TrafficVehicle.DriverProfile.Cautious:
                baseMaxSpeed = Random.Range(s.cautiousMin, s.cautiousMax);
                baseAccel = b.cautiousAccel;
                baseBraking = b.cautiousbraking;
                baseHeadway = b.cautiousHeadway;
                minGap = b.cautiousGap;
                break;
            case TrafficVehicle.DriverProfile.Aggressive:
                baseMaxSpeed = Random.Range(s.aggressiveMin, s.aggressiveMax);
                baseAccel = b.aggressiveAccel;
                baseBraking = b.aggressiveBraking;
                baseHeadway = b.aggressiveHeadway;
                minGap = b.aggressiveGap;
                break;
            default: // Normal
                baseMaxSpeed = Random.Range(s.normalMin, s.normalMax);
                baseAccel = b.normalAccel;
                baseBraking = b.normalBraking;
                baseHeadway = b.normalHeadway;
                minGap = b.normalGap;
                break;
        }

        // Apply per-vehicle variance
        float accel = baseAccel * Random.Range(1f - b.accelVariance, 1f + b.accelVariance);
        float braking = baseBraking * Random.Range(1f - b.brakingVariance, 1f + b.brakingVariance);
        float headway = baseHeadway + Random.Range(-b.headwayVariance, b.headwayVariance);

        // Clamp to safe minimums
        accel = Mathf.Max(accel, 0.5f);
        braking = Mathf.Max(braking, 2f);
        headway = Mathf.Max(headway, 0.4f);

        // Desired speed = max speed with small variation, respecting road limit
        float desiredSpeed = baseMaxSpeed * Random.Range(0.85f, 1.0f);
        if (lane?.road != null && lane.road.speedLimit > 0f)
        {
            float limitMs = lane.road.speedLimit / 3.6f;
            float variance = profile == TrafficVehicle.DriverProfile.Aggressive ? Random.Range(1.0f, 1.2f)
                           : profile == TrafficVehicle.DriverProfile.Normal ? Random.Range(0.9f, 1.05f)
                           : Random.Range(0.75f, 0.92f);
            desiredSpeed = Mathf.Min(baseMaxSpeed, limitMs * variance);
        }

        // Cap to realistic Indian urban speed: max 70 km/h
        float maxSpeedKph = Mathf.Min(baseMaxSpeed * 3.6f, 70f);
        float desiredMs = Mathf.Min(desiredSpeed, maxSpeedKph / 3.6f);

        return new TrafficVehicle.VehicleConfig
        {
            profile = profile,
            maxSpeedKph = maxSpeedKph,
            desiredSpeedMs = desiredMs,
            acceleration = accel,
            braking = braking,
            timeHeadway = headway,
            minimumGap = minGap,
            vehicleLength = vehicleLength,
            stopLineDistance = stopLineDistanceBase,
        };
    }

    TrafficVehicle.DriverProfile ChooseProfile()
    {
        var d = profileDistribution;
        float total = d.cautiousWeight + d.normalWeight + d.aggressiveWeight;
        float r = Random.value * total;
        if (r < d.cautiousWeight) return TrafficVehicle.DriverProfile.Cautious;
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
        int total = 0;
        foreach (var c in laneOccupancy.Values) total += c;
        return total;
    }

    /// <summary>Called when a vehicle is destroyed to update occupancy counts.</summary>
    /// <param name="lane">The lane the vehicle was on.</param>
    public void OnVehicleDestroyed(TrafficLane lane)
    {
        if (!laneOccupancy.ContainsKey(lane)) return;
        laneOccupancy[lane] = Mathf.Max(0, laneOccupancy[lane] - 1);
        lastSpawned[lane] = null;
    }
    TrafficNode PickDestinationNode(TrafficLane spawnLane)
    {
        // Collect all RoadEnd nodes in the scene
        var allNodes = FindObjectsOfType<TrafficNode>();
        var candidates = new System.Collections.Generic.List<TrafficNode>();

        TrafficRoad spawnRoad = spawnLane.road;

        foreach (var node in allNodes)
        {
            if (node.nodeType != TrafficNode.NodeType.RoadEnd) continue;
            // Don't pick a node that's on the spawn road itself
            if (spawnRoad != null &&
                (node == spawnRoad.startNode || node == spawnRoad.endNode)) continue;
            candidates.Add(node);
        }

        if (candidates.Count == 0) return null;
        return candidates[Random.Range(0, candidates.Count)];
    }
}

public class SpawnTracker : MonoBehaviour
{
    private VehicleSpawner spawner;
    private TrafficLane lane;
    public void Init(VehicleSpawner s, TrafficLane l) { spawner = s; lane = l; }
    void OnDestroy() { if (spawner != null) spawner.OnVehicleDestroyed(lane); }
}