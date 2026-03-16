using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficVehicle — AI brain for physics-based car (CarMove)
//
// Architecture:
//   This script computes steering + throttle + brake each FixedUpdate
//   and passes them to CarMove.Move(). The car physics are fully independent.
//   No transform.position manipulation. No ghost point. No lerp.
//
//   The AI does:
//     1. Follow waypoint path (pure-pursuit steering)
//     2. IDM car-following (accel/brake from leader gap)
//     3. Signal stopping (brake hard before stop line)
//     4. Corridor leader detection (Indian traffic model)
//     5. Lane change when blocked
// ─────────────────────────────────────────────────────────────────────────────

[RequireComponent(typeof(CarMove))]
public class TrafficVehicle : MonoBehaviour
{
    public enum DriverProfile { Cautious, Normal, Aggressive }

    // ── Config struct — filled by VehicleSpawner ──────────────────────────
    public struct VehicleConfig
    {
        public DriverProfile profile;
        public float maxSpeedKph;       // km/h — passed to CarMove.m_Topspeed
        public float desiredSpeedMs;    // m/s — IDM target
        public float acceleration;      // IDM max acceleration m/s²
        public float braking;           // IDM comfortable braking m/s²
        public float timeHeadway;       // IDM time headway seconds
        public float minimumGap;        // IDM minimum gap metres
        public float vehicleLength;     // for gap calculation
        public float stopLineDistance;  // metres before stop line to begin braking
    }

    // ── System references ─────────────────────────────────────────────────
    [HideInInspector] public TrafficSignal currentSignal;
    [HideInInspector] public LaneManager laneManager;
    [HideInInspector] public TrafficLane currentLane;
    [HideInInspector] public TrafficLane leftLane;
    [HideInInspector] public TrafficLane rightLane;
    [HideInInspector] public TrafficVehicle leaderVehicle;
    [HideInInspector] public DriverProfile driverProfile;

    // ── Private config ────────────────────────────────────────────────────
    private float desiredSpeedMs;
    private float maxSpeedKph;
    private float acceleration;
    private float braking;
    private float timeHeadway;
    private float minimumGap;
    private float vehicleLength = 4.5f;
    private float stopLineDistance = 14f;

    // ── Path state ────────────────────────────────────────────────────────
    private TrafficPath currentPath;
    private float distanceTravelled = 0f;
    private bool isWaitingForLane = false;

    // ── Pure-pursuit look-ahead ───────────────────────────────────────────
    // Distance ahead on the path to aim at. Larger = smoother turns but cuts corners.
    private float lookAheadBase = 6f;

    // ── IDM state ─────────────────────────────────────────────────────────
    private float idmAccel = 0f;   // current IDM acceleration output (m/s²)
    private bool wasStoppedForSignal = false;

    // ── Lane change state ─────────────────────────────────────────────────
    private float laneChangeCheckTimer = 0f;
    private float laneChangeCooldown = 0f;
    private bool insideIntersection = false;

    // ── Stuck escape ──────────────────────────────────────────────────────
    private float stuckTimer = 0f;
    private const float stuckThreshold = 5f;

    // ── Detection cache ───────────────────────────────────────────────────
    private int detectionFrame = 0;
    private const int detectionInterval = 3;
    private float adjacencyTimer = 0f;
    private TrafficVehicle prevLeader;

    // ── Physics components ────────────────────────────────────────────────
    private CarMove carMove;
    private Rigidbody rb;

    // ── Read-only properties for debug tools / recorder ──────────────────
    public float CurrentSpeed => rb != null ? rb.velocity.magnitude : 0f;
    public float DesiredSpeed => desiredSpeedMs;
    public float MaxSpeed => maxSpeedKph / 3.6f;
    public float TimeHeadway => timeHeadway;
    public float MinimumGap => minimumGap;
    public float StopLineDistance => stopLineDistance;
    public float DistanceTravelled => distanceTravelled;
    public TrafficPath CurrentPath => currentPath;
    public bool IsChangingLane => false;
    public float LaneChangeProgress => 0f;

    // ─────────────────────────────────────────────────────────────────────
    // Initialise
    // ─────────────────────────────────────────────────────────────────────

    void Awake()
    {
        carMove = GetComponent<CarMove>();
        rb = GetComponent<Rigidbody>();
    }

    public void Initialize(VehicleConfig cfg)
    {
        driverProfile = cfg.profile;
        maxSpeedKph = cfg.maxSpeedKph;
        desiredSpeedMs = cfg.desiredSpeedMs;
        acceleration = cfg.acceleration;
        braking = cfg.braking;
        timeHeadway = cfg.timeHeadway;
        minimumGap = cfg.minimumGap;
        vehicleLength = cfg.vehicleLength;
        stopLineDistance = cfg.stopLineDistance;

        // Apply speed cap directly to CarMove so the physics engine respects it
        var cm = GetComponent<CarMove>();
        if (cm != null) cm.SetTopSpeed(cfg.maxSpeedKph);
    }

    void Start()
    {
        if (maxSpeedKph <= 0f) ApplyDefaultConfig();
        if (currentLane != null) AssignLane(currentLane);
    }

    void ApplyDefaultConfig()
    {
        driverProfile = DriverProfile.Normal;
        maxSpeedKph = 50f;
        desiredSpeedMs = 13f;
        acceleration = 2.5f;
        braking = 5f;
        timeHeadway = 1.5f;
        minimumGap = 2f;
        vehicleLength = 4.5f;
        stopLineDistance = 14f;
    }

    void OnDestroy()
    {
        if (carMove != null) carMove.Move(0f, -1f, 0f, 0f); // release throttle on destroy
    }

    // ─────────────────────────────────────────────────────────────────────
    // Main loop
    // ─────────────────────────────────────────────────────────────────────

    void FixedUpdate()
    {
        if (isWaitingForLane || currentPath == null || currentPath.waypoints.Count == 0)
        {
            carMove.Move(0f, -1f, 0f, 0f); // brake while waiting
            return;
        }

        // Adjacency refresh
        adjacencyTimer += Time.fixedDeltaTime;
        if (adjacencyTimer >= 5f)
        {
            adjacencyTimer = 0f;
            if (laneManager != null) laneManager.RefreshVehicle(this);
        }

        // Update path progress from actual physics position
        UpdateDistanceTravelled();
        insideIntersection = (currentPath != currentLane?.path);

        // AI decisions
        DetectLeader();
        UpdateIDM();

        // Lane change check
        if (laneChangeCooldown > 0f) laneChangeCooldown -= Time.fixedDeltaTime;
        float lcInterval = driverProfile == DriverProfile.Aggressive ? 1.5f
                         : driverProfile == DriverProfile.Cautious ? 6f : 3f;
        laneChangeCheckTimer += Time.fixedDeltaTime;
        if (laneChangeCheckTimer >= lcInterval) { laneChangeCheckTimer = 0f; CheckLaneChange(); }

        // Stuck escape
        float spd = CurrentSpeed;
        if (spd < 0.2f && leaderVehicle != null) stuckTimer += Time.fixedDeltaTime;
        else stuckTimer = 0f;
        if (stuckTimer >= stuckThreshold)
        {
            if (!TryLaneChange(leftLane) && !TryLaneChange(rightLane))
            {
                if (insideIntersection) Despawn();
                else if (stuckTimer >= stuckThreshold * 2f) idmAccel = Mathf.Max(idmAccel, 0.3f); // nudge
            }
            else stuckTimer = 0f;
        }

        // World-space end-of-path check — catches physics overshoot on turn paths
        if (currentPath != null && currentPath.waypoints.Count > 0)
        {
            Vector3 pathEnd = currentPath.waypoints[currentPath.waypoints.Count - 1].position;
            float distToEnd = Vector3.Distance(transform.position, pathEnd);
            if (distToEnd < 4f || currentPath.TotalLength - distanceTravelled < 5f)
                AdvanceToNextPath();
        }

        // Drive
        float steer = ComputeSteering();
        float throttle = Mathf.Clamp01(idmAccel / Mathf.Max(acceleration, 0.1f));
        float brake = Mathf.Clamp01(-idmAccel / Mathf.Max(braking, 0.1f));
        carMove.Move(throttle, -brake, 0f, steer);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Pure-pursuit steering
    // ─────────────────────────────────────────────────────────────────────

    float ComputeSteering()
    {
        if (currentPath == null) return 0f;

        // Adaptive look-ahead: further ahead at higher speed
        float spd = CurrentSpeed;
        float lookAhead = lookAheadBase + spd * 0.5f;

        // Find the target point ahead on the path
        Vector3 targetPoint = currentPath.GetPointAtDistance(
            Mathf.Min(distanceTravelled + lookAhead, currentPath.TotalLength));

        // Transform target into local space
        Vector3 localTarget = transform.InverseTransformPoint(targetPoint);

        // Pure-pursuit formula: steering = atan(2L*sin(α) / d)
        // Simplified to: normalised lateral error
        float lateralError = localTarget.x;
        float forwardDist = Mathf.Max(localTarget.z, 1f);
        float steerAngle = Mathf.Atan2(lateralError, forwardDist) * Mathf.Rad2Deg;

        // Normalise to -1..1 based on CarMove's max steer angle (default 25°)
        float maxSteer = 25f;
        return Mathf.Clamp(steerAngle / maxSteer, -1f, 1f);
    }

    // ─────────────────────────────────────────────────────────────────────
    // IDM — produces idmAccel (positive = accelerate, negative = brake)
    // ─────────────────────────────────────────────────────────────────────

    void UpdateIDM()
    {
        float v = CurrentSpeed;

        // Signal stop overrides IDM
        if (MustStopForSignal())
        {
            if (!wasStoppedForSignal) { TrafficEventLogger.Log(name, "SIGNAL_STOP", ""); wasStoppedForSignal = true; }
            Vector3 stopLine = currentLane.path.waypoints[currentLane.path.waypoints.Count - 1].position;
            float distToLine = Vector3.Distance(transform.position, stopLine);
            // Full braking proportional to how close we are
            float brakeFraction = Mathf.Clamp01((stopLineDistance - distToLine + 2f) / stopLineDistance);
            idmAccel = -braking * Mathf.Max(brakeFraction, 0.3f);
            return;
        }
        if (wasStoppedForSignal) { TrafficEventLogger.Log(name, "SIGNAL_GO", ""); wasStoppedForSignal = false; }

        if (leaderVehicle == null)
        {
            // Free road — accelerate toward desired speed
            idmAccel = acceleration * (1f - Mathf.Pow(v / Mathf.Max(desiredSpeedMs, 0.1f), 4f));
        }
        else
        {
            float vL = leaderVehicle.CurrentSpeed;
            float gap = Mathf.Max(Vector3.Distance(transform.position, leaderVehicle.transform.position) - vehicleLength, 0.1f);
            float deltaV = v - vL;
            float sStar = minimumGap + Mathf.Max(0f,
                v * timeHeadway + (v * deltaV) / (2f * Mathf.Sqrt(Mathf.Max(acceleration * braking, 0.01f))));
            idmAccel = acceleration * (1f - Mathf.Pow(v / Mathf.Max(desiredSpeedMs, 0.1f), 4f) - Mathf.Pow(sStar / gap, 2f));
            idmAccel = Mathf.Max(idmAccel, -braking);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Signal
    // ─────────────────────────────────────────────────────────────────────

    bool MustStopForSignal()
    {
        if (currentSignal == null || currentLane == null) return false;
        if (currentPath != null && currentLane.path != null && currentPath != currentLane.path) return false;
        if (currentSignal.GetStateForLane(currentLane) == TrafficSignal.SignalState.Green) return false;
        if (currentLane.path == null || currentLane.path.waypoints.Count == 0) return false;
        Vector3 stopLinePos = currentLane.path.waypoints[currentLane.path.waypoints.Count - 1].position;
        float distToLine = Vector3.Distance(transform.position, stopLinePos);
        float dynamicDist = Mathf.Max(stopLineDistance,
            (CurrentSpeed * CurrentSpeed) / (2f * Mathf.Max(braking, 1f)) + 6f);
        return distToLine <= dynamicDist;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Leader detection — corridor-based (Indian traffic: no lane membership)
    // ─────────────────────────────────────────────────────────────────────

    void DetectLeader()
    {
        detectionFrame++;
        if (detectionFrame < detectionInterval) return;
        detectionFrame = 0;

        TrafficVehicle closest = null;
        float closestD = 35f;
        float corridorCos = 0.82f; // ±35° forward cone

        Collider[] cols = Physics.OverlapSphere(transform.position, 35f);
        foreach (var col in cols)
        {
            if (col.gameObject == gameObject) continue;
            if (col.transform.IsChildOf(transform)) continue;
            TrafficVehicle other = col.GetComponentInParent<TrafficVehicle>();
            if (other == null || other == this) continue;

            Vector3 toOther = other.transform.position - transform.position;
            float dist = toOther.magnitude;
            if (dist < 0.5f) continue;
            if (Vector3.Dot(transform.forward, toOther.normalized) < corridorCos) continue;

            // Closing speed filter: skip if they are pulling away faster than 2 m/s
            if (CurrentSpeed - other.CurrentSpeed < -2f) continue;

            if (dist < closestD) { closestD = dist; closest = other; }
        }

        // Emergency: anything physically in front within vehicleLength+2m
        Collider[] eCols = Physics.OverlapSphere(transform.position, vehicleLength + 2f);
        foreach (var col in eCols)
        {
            if (col.gameObject == gameObject) continue;
            if (col.transform.IsChildOf(transform)) continue;
            TrafficVehicle other = col.GetComponentInParent<TrafficVehicle>();
            if (other == null || other == this) continue;
            Vector3 toOther = other.transform.position - transform.position;
            if (Vector3.Dot(transform.forward, toOther.normalized) < 0.3f) continue;
            float dist = toOther.magnitude;
            if (closest == null || dist < closestD) { closestD = dist; closest = other; }
        }

        if (closest != leaderVehicle)
        {
            if (leaderVehicle != null) TrafficEventLogger.Log(name, "LEADER_LOST", "");
            if (closest != null) TrafficEventLogger.Log(name, "LEADER_FOUND", closest.name);
        }
        leaderVehicle = closest;
        prevLeader = closest;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Path management
    // ─────────────────────────────────────────────────────────────────────

    void UpdateDistanceTravelled()
    {
        if (currentPath == null) return;
        distanceTravelled = currentPath.GetClosestDistance(transform.position);
    }

    public void AssignLane(TrafficLane lane, bool fromSpawn = false)
    {
        currentLane = lane;
        currentPath = lane.path;
        distanceTravelled = fromSpawn ? 0f
            : (currentPath != null ? currentPath.GetClosestDistance(transform.position) : 0f);
        isWaitingForLane = false;

        if (currentLane?.road != null && currentLane.road.speedLimit > 0f)
        {
            float limitMs = currentLane.road.speedLimit / 3.6f;
            float variance = driverProfile == DriverProfile.Aggressive ? Random.Range(1.0f, 1.1f)
                           : driverProfile == DriverProfile.Normal ? Random.Range(0.9f, 1.0f)
                           : Random.Range(0.75f, 0.9f);
            desiredSpeedMs = Mathf.Min(MaxSpeed, limitMs * variance);
        }

        if (fromSpawn) TrafficEventLogger.Log(name, "SPAWN", "");
    }

    void AdvanceToNextPath()
    {
        if (currentLane == null) { Despawn(); return; }

        if (currentLane.nextPaths != null && currentLane.nextPaths.Count > 0)
        {
            var lp = ChooseTurnPath();
            if (lp != null && lp.path != null && lp.targetLane != null)
            {
                currentPath = lp.path;
                currentLane = lp.targetLane;
                distanceTravelled = currentPath.GetClosestDistance(transform.position);

                if (laneManager != null)
                    laneManager.RefreshVehicle(this);

                return;
            }
        }

        if (currentLane.nextLanes != null && currentLane.nextLanes.Count > 0)
        {
            AssignLane(currentLane.nextLanes[Random.Range(0, currentLane.nextLanes.Count)]);
            return;
        }

        Despawn();
    }

    TrafficLane.LanePath ChooseTurnPath()
    {
        var paths = currentLane.nextPaths;
        if (paths == null || paths.Count == 0) return null;

        float[] weights = new float[paths.Count];
        float total = 0f;
        for (int i = 0; i < paths.Count; i++)
        {
            var lp = paths[i];
            if (lp == null || lp.targetLane == null) continue;
            Vector3 fwd = transform.forward;
            Vector3 tFwd = lp.targetLane.transform.forward;
            float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot(fwd, tFwd), -1f, 1f)) * Mathf.Rad2Deg;
            // Weighted toward straight (0°) > right turn > left turn
            weights[i] = ang < 30f ? 0.5f : ang > 150f ? 0.05f
                       : Vector3.Dot(Vector3.Cross(fwd, tFwd), Vector3.up) >= 0f ? 0.3f : 0.15f;
            total += weights[i];
        }
        if (total <= 0f) return paths[Random.Range(0, paths.Count)];
        float pick = Random.value * total, acc = 0f;
        for (int i = 0; i < paths.Count; i++) { acc += weights[i]; if (pick <= acc) return paths[i]; }
        return paths[paths.Count - 1];
    }

    void Despawn()
    {
        TrafficEventLogger.Log(name, "DESPAWN", "");
        isWaitingForLane = true;
        Destroy(gameObject, 0.3f);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Lane change
    // ─────────────────────────────────────────────────────────────────────

    public void CheckLaneChange()
    {
        if (insideIntersection || laneChangeCooldown > 0f) return;
        if (currentPath == null || currentLane == null || currentPath != currentLane.path) return;
        if (currentLane.path == null) return;
        if (currentLane.path.TotalLength - distanceTravelled < 45f) return;
        if (CurrentSpeed < 0.5f) return;
        if (MustStopForSignal()) return;
        if (leaderVehicle != null && leaderVehicle.CurrentSpeed < 0.1f
            && leaderVehicle.currentSignal != null && leaderVehicle.currentLane != null
            && leaderVehicle.currentSignal.GetStateForLane(leaderVehicle.currentLane) != TrafficSignal.SignalState.Green)
            return;

        bool blocked = leaderVehicle != null && leaderVehicle.CurrentSpeed < CurrentSpeed * 0.8f;
        bool slow = CurrentSpeed < desiredSpeedMs * 0.8f;
        if (!blocked && !slow) return;

        float gL = leftLane != null ? GapInLane(leftLane) : -1f;
        float gR = rightLane != null ? GapInLane(rightLane) : -1f;

        TrafficLane target = null;
        if (gL > 0f && gR > 0f) target = gL >= gR ? leftLane : rightLane;
        else if (gL > 0f) target = leftLane;
        else if (gR > 0f) target = rightLane;

        if (target != null) TryLaneChange(target);
    }

    bool TryLaneChange(TrafficLane target)
    {
        if (target == null || target == currentLane || target.path == null) return false;
        float gap = GapInLane(target);
        float reqGap = minimumGap + CurrentSpeed * 1.2f;
        if (gap < reqGap) return false;

        currentLane = target;
        currentPath = target.path;
        distanceTravelled = currentPath.GetClosestDistance(transform.position);
        laneChangeCooldown = 5f;
        TrafficEventLogger.Log(name, "LANE_CHANGE", target.name);
        if (laneManager != null) laneManager.RefreshVehicle(this);
        return true;
    }

    float GapInLane(TrafficLane lane)
    {
        if (lane == null) return 50f;
        float minG = 50f;
        var cols = Physics.OverlapSphere(transform.position, 20f);
        foreach (var col in cols)
        {
            TrafficVehicle other = col.GetComponentInParent<TrafficVehicle>();
            if (other == null || other == this || other.currentLane != lane) continue;
            Vector3 toOther = other.transform.position - transform.position;
            if (Vector3.Dot(transform.forward, toOther.normalized) <= 0.5f) continue;
            float g = toOther.magnitude - vehicleLength;
            if (g < minG) minG = g;
        }
        return minG;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Utilities
    // ─────────────────────────────────────────────────────────────────────

    public Vector3 GetVelocity() => rb != null ? rb.velocity : Vector3.zero;

    // ─────────────────────────────────────────────────────────────────────
    // Gizmos
    // ─────────────────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (currentPath == null) return;
        Vector3 origin = transform.position + Vector3.up;

        // Detection corridor
        Gizmos.color = leaderVehicle != null ? Color.red : Color.green;
        Vector3 fwdEnd = origin + transform.forward * 35f;
        Gizmos.DrawLine(origin, fwdEnd);

        // Lookahead target
        if (Application.isPlaying)
        {
            float la = lookAheadBase + CurrentSpeed * 0.5f;
            Vector3 target = currentPath.GetPointAtDistance(Mathf.Min(distanceTravelled + la, currentPath.TotalLength));
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(origin, target + Vector3.up);
            Gizmos.DrawWireSphere(target + Vector3.up, 0.5f);
        }

        // Leader line
        if (leaderVehicle != null)
        {
            Gizmos.color = new Color(1f, 0.5f, 0f);
            Gizmos.DrawLine(origin, leaderVehicle.transform.position + Vector3.up);
        }

        // Signal indicator
        if (currentSignal != null && currentLane != null)
        {
            Gizmos.color = MustStopForSignal() ? Color.red : Color.green;
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 2f, 0.5f);
        }
    }
}