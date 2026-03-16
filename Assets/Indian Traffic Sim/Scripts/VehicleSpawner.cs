using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// All randomness lives here. TrafficVehicle fields are private.
// ─────────────────────────────────────────────────────────────────────────────

[System.Serializable]
public class DriverProfileDistribution
{
    [Range(0f, 1f)] public float cautiousWeight  = 0.30f;
    [Range(0f, 1f)] public float normalWeight    = 0.50f;
    [Range(0f, 1f)] public float aggressiveWeight = 0.20f;
}

[System.Serializable]
public class SpeedDistribution
{
    [Header("Cautious")]
    public float cautiousMin = 7f;
    public float cautiousMax = 11f;

    [Header("Normal")]
    public float normalMin   = 11f;
    public float normalMax   = 16f;

    [Header("Aggressive")]
    public float aggressiveMin = 15f;
    public float aggressiveMax = 22f;
}

[System.Serializable]
public class BehaviourDistribution
{
    [Header("Acceleration m/s²")]
    public float cautiousAccel    = 1.5f;
    public float normalAccel      = 2.5f;
    public float aggressiveAccel  = 4.5f;
    public float accelVariance    = 0.2f;   // ± fraction of base

    [Header("Braking m/s²")]
    public float cautiousbraking     = 4f;
    public float normalBraking       = 6f;
    public float aggressiveBraking   = 9f;
    public float brakingVariance     = 0.15f;

    [Header("Time Headway s")]
    public float cautiousHeadway    = 2.2f;
    public float normalHeadway      = 1.5f;
    public float aggressiveHeadway  = 0.8f;
    public float headwayVariance    = 0.3f;  // ± absolute seconds

    [Header("Min Gap m")]
    public float cautiousGap    = 4f;
    public float normalGap      = 2f;
    public float aggressiveGap  = 1f;

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
    public int maxVehiclesTotal  = 40;
    public int maxVehiclesPerLane = 8;

    [Header("Spawn Timing")]
    public float spawnInterval = 1.5f;
    private float spawnTimer;

    [Header("Safety")]
    public float spawnClearDistance = 15f;

    [Header("Systems")]
    public LaneManager   laneManager;
    public TrafficSignal trafficSignal;

    [Header("Driver Distribution")]
    public DriverProfileDistribution profileDistribution = new DriverProfileDistribution();
    public SpeedDistribution         speedDistribution   = new SpeedDistribution();
    public BehaviourDistribution     behaviourDistribution = new BehaviourDistribution();

    [Header("Vehicle Physics")]
    public float vehicleLength       = 4.5f;
    public float stopLineDistanceBase = 14f;

    // Internal
    private Dictionary<TrafficLane, int> laneOccupancy         = new Dictionary<TrafficLane, int>();
    private Dictionary<TrafficLane, TrafficVehicle> lastSpawned = new Dictionary<TrafficLane, TrafficVehicle>();
    private List<TrafficLane> lanes = new List<TrafficLane>();

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
                lastSpawned[lane]   = null;
            }
        }
        if (trafficSignal == null) trafficSignal = FindObjectOfType<TrafficSignal>();
        if (laneManager   == null) laneManager   = FindObjectOfType<LaneManager>();
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

        Vector3    spawnPos = lane.path.waypoints[0].position;
        Quaternion rot      = Quaternion.identity;
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

        // Assign system references
        vehicle.currentSignal = trafficSignal;
        vehicle.laneManager   = laneManager;
        vehicle.AssignLane(lane, fromSpawn: true);

        if (laneManager != null) laneManager.RegisterVehicle(vehicle);

        laneOccupancy[lane]++;
        lastSpawned[lane] = vehicle;
        vehicleGO.AddComponent<SpawnTracker>().Init(this, lane);
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
                baseAccel    = b.cautiousAccel;
                baseBraking  = b.cautiousbraking;
                baseHeadway  = b.cautiousHeadway;
                minGap       = b.cautiousGap;
                break;
            case TrafficVehicle.DriverProfile.Aggressive:
                baseMaxSpeed = Random.Range(s.aggressiveMin, s.aggressiveMax);
                baseAccel    = b.aggressiveAccel;
                baseBraking  = b.aggressiveBraking;
                baseHeadway  = b.aggressiveHeadway;
                minGap       = b.aggressiveGap;
                break;
            default: // Normal
                baseMaxSpeed = Random.Range(s.normalMin, s.normalMax);
                baseAccel    = b.normalAccel;
                baseBraking  = b.normalBraking;
                baseHeadway  = b.normalHeadway;
                minGap       = b.normalGap;
                break;
        }

        // Apply per-vehicle variance
        float accel   = baseAccel   * Random.Range(1f - b.accelVariance,   1f + b.accelVariance);
        float braking = baseBraking * Random.Range(1f - b.brakingVariance, 1f + b.brakingVariance);
        float headway = baseHeadway + Random.Range(-b.headwayVariance, b.headwayVariance);

        // Clamp to safe minimums
        accel   = Mathf.Max(accel,   0.5f);
        braking = Mathf.Max(braking, 2f);
        headway = Mathf.Max(headway, 0.4f);

        // Desired speed = max speed with small variation, respecting road limit
        float desiredSpeed = baseMaxSpeed * Random.Range(0.85f, 1.0f);
        if (lane?.road != null && lane.road.speedLimit > 0f)
        {
            float limitMs  = lane.road.speedLimit / 3.6f;
            float variance = profile == TrafficVehicle.DriverProfile.Aggressive ? Random.Range(1.0f, 1.2f)
                           : profile == TrafficVehicle.DriverProfile.Normal      ? Random.Range(0.9f, 1.05f)
                           : Random.Range(0.75f, 0.92f);
            desiredSpeed = Mathf.Min(baseMaxSpeed, limitMs * variance);
        }

        // Cap to realistic Indian urban speed: max 70 km/h
        float maxSpeedKph = Mathf.Min(baseMaxSpeed * 3.6f, 70f);
        float desiredMs   = Mathf.Min(desiredSpeed, maxSpeedKph / 3.6f);

        return new TrafficVehicle.VehicleConfig
        {
            profile          = profile,
            maxSpeedKph      = maxSpeedKph,
            desiredSpeedMs   = desiredMs,
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
        if (r < d.cautiousWeight)  return TrafficVehicle.DriverProfile.Cautious;
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

    public void OnVehicleDestroyed(TrafficLane lane)
    {
        if (!laneOccupancy.ContainsKey(lane)) return;
        laneOccupancy[lane] = Mathf.Max(0, laneOccupancy[lane] - 1);
        lastSpawned[lane]   = null;
    }
}

public class SpawnTracker : MonoBehaviour
{
    private VehicleSpawner spawner;
    private TrafficLane    lane;
    public void Init(VehicleSpawner s, TrafficLane l) { spawner = s; lane = l; }
    void OnDestroy() { if (spawner != null) spawner.OnVehicleDestroyed(lane); }
}