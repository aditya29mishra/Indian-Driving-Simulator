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
[RequireComponent(typeof(VehicleRecorder))]
public class TrafficVehicle : MonoBehaviour
{
    // ───────────────── STATE SYSTEM ─────────────────

    public enum MotionState
    {
        Idle,
        Cruising,
        Following,
        Stopped,
        Starting,
        Braking
    }

    public enum SignalStateEx
    {
        None,
        Approaching,
        WaitingRed,
        Released
    }

    public enum ManeuverState
    {
        LaneKeeping,
        LaneChangeLeft,
        LaneChangeRight,
        Turning
    }
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
    // Current states
    public MotionState CurrentMotionState { get; private set; }
    public SignalStateEx CurrentSignalState { get; private set; }
    public ManeuverState CurrentManeuverState { get; private set; }

    // Previous states
    public MotionState PreviousMotionState { get; private set; }
    public SignalStateEx PreviousSignalState { get; private set; }
    public ManeuverState PreviousManeuverState { get; private set; }

    // Flags
    public bool IsBlocked { get; private set; }
    public bool IsStuck { get; private set; }
    public bool IsInQueue { get; private set; }

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

    // ── Lane change state ─────────────────────────────────────────────────
    private float laneChangeCheckTimer = 0f;
    private float laneChangeCooldown = 0f;
    private bool insideIntersection = false;
    private ManeuverState pendingManeuverState = ManeuverState.LaneKeeping;

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
    public bool IsChangingLane => laneChangeCooldown > 0f && !insideIntersection;
    public float LaneChangeProgress => Mathf.Clamp01(1f - laneChangeCooldown / 5f);
    TrafficDataRecorder rec;
    private string vehicleId;
    public string GetVehicleId() => vehicleId;
    private List<string> routeHistory = new List<string>(32);
    private bool inRestartPhase = false;
    private float restartTimer = 0f;
    private float restartDuration = 1.2f; // key tuning param 

    // Signal edge detection
    private TrafficSignal.SignalState prevSignalState = TrafficSignal.SignalState.Green;
    private TrafficSignal.SignalState currentSignalStateCached = TrafficSignal.SignalState.Green;

    void CacheRecorder()
    {
        if (rec == null)
            rec = FindObjectOfType<TrafficDataRecorder>();
    }
    // ─────────────────────────────────────────────────────────────────────
    // Initialise
    // ─────────────────────────────────────────────────────────────────────

    void Awake()
    {
        carMove = GetComponent<CarMove>();
        rb = GetComponent<Rigidbody>();
        CacheRecorder();
        vehicleId = name + "_" + Random.Range(1000, 9999);

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
        // Only re-assign lane for editor-placed vehicles (currentLane set in inspector).
        // Spawned vehicles have AssignLane called explicitly by VehicleSpawner before
        // Start fires — calling it again here would reset distanceTravelled unnecessarily.
        if (currentLane != null && currentPath == null)
            AssignLane(currentLane);
        routeHistory.Clear();
        routeHistory.Add("START");
        rec?.LogEvent(vehicleId, "SPAWN", GetPathId(), GetIntersectionId(), "", -1f, GetRouteTrace());
    }
    public string GetPathId()
    {
        return currentPath ? currentPath.name : "none";
    }

    public string GetIntersectionId()
    {
        return currentLane && currentLane.road != null
            ? currentLane.road.name
            : "none";
    }

    public string GetRouteTrace()
    {
        return string.Join(">", routeHistory);
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
            // -1f footbrake → CarMove re-negates to +1f → full brake hold. Intentional.
            carMove.Move(0f, -1f, 0f, 0f);
            return;
        }

        adjacencyTimer += Time.fixedDeltaTime;
        if (adjacencyTimer >= 5f)
        {
            adjacencyTimer = 0f;
            if (laneManager != null) laneManager.RefreshVehicle(this);
        }

        UpdateDistanceTravelled();
        insideIntersection = (currentPath != currentLane?.path);

        if (currentSignal != null && currentLane != null)
        {
            currentSignalStateCached = currentSignal.GetStateForLane(currentLane);
        }
        else
        {
            currentSignalStateCached = TrafficSignal.SignalState.Green;
        }

        // DetectLeader();
        UpdateStateSystem();
        UpdateIDM(); // still called

        // ───────── RESTART PHASE TIMER ─────────
        if (inRestartPhase)
        {
            restartTimer -= Time.fixedDeltaTime;
            if (restartTimer <= 0f)
            {
                inRestartPhase = false;
            }
        }

        // Lane change (unchanged)
        if (laneChangeCooldown > 0f) laneChangeCooldown -= Time.fixedDeltaTime;

        float lcInterval = driverProfile == DriverProfile.Aggressive ? 1.5f
                         : driverProfile == DriverProfile.Cautious ? 6f : 3f;

        laneChangeCheckTimer += Time.fixedDeltaTime;
        if (laneChangeCheckTimer >= lcInterval)
        {
            laneChangeCheckTimer = 0f;
            CheckLaneChange();
        }

        // Stuck escape (unchanged)
        float spd = CurrentSpeed;
        if (spd < 0.2f && leaderVehicle != null) stuckTimer += Time.fixedDeltaTime;
        else stuckTimer = 0f;

        if (stuckTimer >= stuckThreshold)
        {
            if (!TryLaneChange(leftLane) && !TryLaneChange(rightLane))
            {
                if (insideIntersection) Despawn();
                else if (stuckTimer >= stuckThreshold * 2f)
                    idmAccel = Mathf.Max(idmAccel, 0.3f);
            }
            else stuckTimer = 0f;
        }

        // Path end logic
        if (currentPath != null && currentPath.waypoints.Count > 0)
        {
            Vector3 pathEnd = currentPath.waypoints[currentPath.waypoints.Count - 1].position;
            float distToEnd = Vector3.Distance(transform.position, pathEnd);
            bool isTurnPath = currentLane != null && currentPath != currentLane.path;

            float remaining = currentPath.TotalLength - distanceTravelled;

            // Trigger advance when: physically close to end waypoint OR spline distance
            // exhausted. The distToEnd < 6f threshold (was 4f) covers the gap between
            // the last spline point and the actual waypoint transform position.
            bool nearEnd = distToEnd < 6f || remaining < 2f;

            if (isTurnPath && nearEnd)
            {
                AdvanceToNextPath();
            }
            else if (!isTurnPath && nearEnd)
            {
                AdvanceToNextPath();
            }
        }

        // ───────── DRIVE OUTPUT (MODIFIED CORE) ─────────

        float steer = ComputeSteering();

        float throttle;
        float brake;

        if (inRestartPhase)
        {
            float restartBoost = Mathf.Lerp(0.4f, 1f, 1f - (restartTimer / restartDuration));
            throttle = Mathf.Clamp01(restartBoost);
            brake = 0f;
        }
        else
        {
            throttle = Mathf.Clamp01(idmAccel / Mathf.Max(acceleration, 0.1f));
            brake = Mathf.Clamp01(-idmAccel / Mathf.Max(braking, 0.1f));
        }

        // Convention: TrafficVehicle passes footbrake as a NEGATIVE value (0..-1).
        // CarMove.Move re-negates it internally (line: footbrake = -1 * Clamp(footbrake,-1,0)).
        // So -brake here → +brake inside CarMove. Do NOT pass +brake — it would be clamped to 0.
        carMove.Move(throttle, -brake, 0f, steer);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Pure-pursuit steering
    // ─────────────────────────────────────────────────────────────────────
    void UpdateStateSystem()
    {
        // Store previous
        PreviousMotionState = CurrentMotionState;
        PreviousSignalState = CurrentSignalState;
        PreviousManeuverState = CurrentManeuverState;

        float v = CurrentSpeed;

        // ───────── SIGNAL STATE ─────────
        if (MustStopForSignal())
        {
            CurrentSignalState = SignalStateEx.WaitingRed;
        }
        else if (inRestartPhase)
        {
            CurrentSignalState = SignalStateEx.Released;
        }
        else if (currentSignal != null)
        {
            CurrentSignalState = SignalStateEx.Approaching;
        }
        else
        {
            CurrentSignalState = SignalStateEx.None;
        }

        // ───────── MANEUVER STATE ─────────
        if (currentPath != null && currentLane != null && currentPath != currentLane.path)
        {
            CurrentManeuverState = ManeuverState.Turning;
            pendingManeuverState = ManeuverState.LaneKeeping;
        }
        else if (laneChangeCooldown > 0f)
        {
            CurrentManeuverState = pendingManeuverState; // LaneChangeLeft or LaneChangeRight, set by TryLaneChange
        }
        else
        {
            CurrentManeuverState = ManeuverState.LaneKeeping;
            pendingManeuverState = ManeuverState.LaneKeeping;
        }

        // ───────── FLAGS ─────────
        IsBlocked = leaderVehicle != null && leaderVehicle.CurrentSpeed < CurrentSpeed;
        IsStuck = stuckTimer > stuckThreshold * 0.5f;
        IsInQueue = leaderVehicle != null && CurrentSpeed < 2f;

        // ───────── MOTION STATE ─────────
        if (v < 0.2f)
        {
            if (CurrentSignalState == SignalStateEx.WaitingRed)
                CurrentMotionState = MotionState.Stopped;
            else if (IsBlocked)
                CurrentMotionState = MotionState.Following;
            else
                CurrentMotionState = MotionState.Idle;
        }
        else
        {
            if (inRestartPhase)
                CurrentMotionState = MotionState.Starting;
            else if (idmAccel < -0.1f)
                CurrentMotionState = MotionState.Braking;
            else if (leaderVehicle != null)
                CurrentMotionState = MotionState.Following;
            else
                CurrentMotionState = MotionState.Cruising;
        }
        // ───────── STATE CHANGE LOGGING ─────────
        if (rec != null)
        {
            if (CurrentMotionState != PreviousMotionState)
            {
                rec.LogEvent(vehicleId, "STATE_MOTION", CurrentMotionState.ToString());
            }

            if (CurrentSignalState != PreviousSignalState)
            {
                rec.LogEvent(vehicleId, "STATE_SIGNAL", CurrentSignalState.ToString());
            }

            if (CurrentManeuverState != PreviousManeuverState)
            {
                rec.LogEvent(vehicleId, "STATE_MANEUVER", CurrentManeuverState.ToString());
            }
        }
    }
    float ComputeSteering()
    {
        if (currentPath == null) return 0f;

        // Adaptive look-ahead: further ahead at higher speed
        float spd = CurrentSpeed;
        float lookAhead = lookAheadBase + Mathf.Clamp(spd, 0f, 10f) * 0.3f;

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

        // ─────────────────────────────
        // SIGNAL STOP
        // ─────────────────────────────
        if (MustStopForSignal())
        {
            Vector3 stopLine = currentLane.path.waypoints[currentLane.path.waypoints.Count - 1].position;
            float distToLine = Vector3.Distance(transform.position, stopLine);

            float brakeFraction = Mathf.Clamp01((stopLineDistance - distToLine + 2f) / stopLineDistance);
            idmAccel = -braking * Mathf.Max(brakeFraction, 0.4f);

            return;
        }

        // ─────────────────────────────
        // 🔥 GREEN SIGNAL EDGE DETECTION (FIX)
        // ─────────────────────────────
        if (prevSignalState != TrafficSignal.SignalState.Green &&
            currentSignalStateCached == TrafficSignal.SignalState.Green)
        {
            inRestartPhase = true;
            restartTimer = restartDuration;

            idmAccel = Mathf.Clamp(acceleration * 0.8f, 1.0f, 2.0f);
        }

        // update previous state AFTER check
        prevSignalState = currentSignalStateCached;

        // ─────────────────────────────
        // LOW SPEED ASSIST
        // ─────────────────────────────
        if (v < 1.0f)
        {
            idmAccel = Mathf.Max(idmAccel, 1.5f);
        }

        // ─────────────────────────────
        // NORMAL IDM
        // ─────────────────────────────
        if (leaderVehicle == null)
        {
            idmAccel = acceleration * (1f - Mathf.Pow(v / Mathf.Max(desiredSpeedMs, 0.1f), 4f));
        }
        else
        {
            float vL = leaderVehicle.CurrentSpeed;
            float gap = Mathf.Max(Vector3.Distance(transform.position, leaderVehicle.transform.position) - vehicleLength, 0.1f);
            float deltaV = v - vL;

            float sStar = minimumGap + Mathf.Max(0f,
                v * timeHeadway + (v * deltaV) / (2f * Mathf.Sqrt(Mathf.Max(acceleration * braking, 0.01f)))
            );

            idmAccel = acceleration * (
                1f
                - Mathf.Pow(v / Mathf.Max(desiredSpeedMs, 0.1f), 4f)
                - Mathf.Pow(sStar / gap, 2f)
            );

            // prevent hard lock at low speed
            if (v < 1.5f)
                idmAccel = Mathf.Max(idmAccel, -braking * 0.3f);
            else
                idmAccel = Mathf.Max(idmAccel, -braking);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Signal
    // ─────────────────────────────────────────────────────────────────────

    bool MustStopForSignal()
    {
        if (currentSignal == null || currentLane == null) return false;
        // Never brake while on a turn/intersection path — already committed
        if (currentPath != null && currentLane.path != null && currentPath != currentLane.path) return false;
        if (currentLane.path == null || currentLane.path.waypoints.Count == 0) return false;

        TrafficSignal.SignalState state = currentSignal.GetStateForLane(currentLane);

        // Green — never stop
        if (state == TrafficSignal.SignalState.Green) return false;

        Vector3 stopLinePos = currentLane.path.waypoints[currentLane.path.waypoints.Count - 1].position;
        float distToLine = Vector3.Distance(transform.position, stopLinePos);
        float dynamicDist = Mathf.Max(stopLineDistance,
            (CurrentSpeed * CurrentSpeed) / (2f * Mathf.Max(braking, 1f)) + 6f);

        // Yellow — only stop if not yet past the stop line braking zone.
        // If already inside the zone or past the line, let the vehicle clear the intersection.
        if (state == TrafficSignal.SignalState.Yellow)
            return distToLine <= dynamicDist && distToLine > 1f;

        // Red — stop whenever within braking distance
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
        float closestD = 30f;

        Collider[] cols = Physics.OverlapSphere(transform.position, 30f);

        foreach (var col in cols)
        {
            if (col.gameObject == gameObject) continue;

            TrafficVehicle other = col.GetComponentInParent<TrafficVehicle>();
            if (other == null || other == this) continue;

            // 🔥 MUST BE SAME PATH (CRITICAL FIX)
            if (other.CurrentPath != currentPath) continue;

            Vector3 toOther = other.transform.position - transform.position;
            float dist = toOther.magnitude;

            if (dist < 0.5f) continue;

            // 🔥 STRICT forward check (narrow cone)
            float dot = Vector3.Dot(transform.forward, toOther.normalized);
            if (dot < 0.95f) continue; // was 0.82 → too wide

            // 🔥 MUST BE AHEAD (not side / behind)
            if (Vector3.Dot(transform.forward, toOther) <= 0) continue;

            if (dist < closestD)
            {
                closestD = dist;
                closest = other;
            }
        }

        leaderVehicle = closest;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Path management
    // ─────────────────────────────────────────────────────────────────────

    void UpdateDistanceTravelled()
    {
        if (currentPath == null) return;
        float closest = currentPath.GetClosestDistance(transform.position);

        // Snap directly — the old Lerp(0.2) never actually reached TotalLength,
        // so `remaining` never fell below 2f and AdvanceToNextPath was never triggered
        // at dead ends. Allow small backward correction by clamping to not jump
        // more than 5m backward in a single frame (covers any spline discontinuity).
        if (closest < distanceTravelled - 5f)
            distanceTravelled = closest;
        else
            distanceTravelled = Mathf.Max(distanceTravelled, closest);
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

        // Sync prevSignalState to the actual current state so the green-edge
        // detector doesn't fire a false restart on the very first frame.
        if (currentSignal != null)
            prevSignalState = currentSignal.GetStateForLane(lane);
        else
            prevSignalState = TrafficSignal.SignalState.Green;

        routeHistory.Add("L:" + lane.name);
    }

    void AdvanceToNextPath()
    {
        if (currentLane == null)
        {
            rec?.LogEvent(vehicleId, "FAIL_NO_LANE");
            Despawn();
            return;
        }

        bool isTurnPath = currentPath != currentLane.path;

        // ─────────────────────────────────────────────
        // 1. FINISH TURN → SNAP BACK TO LANE PATH
        // ─────────────────────────────────────────────
        if (isTurnPath)
        {
            rec?.LogEvent(vehicleId, "TURN_COMPLETE", currentLane.name);

            AssignLane(currentLane);
            return;
        }

        // ─────────────────────────────────────────────
        // 2. NORMAL LANE → PICK TURN PATH
        // ─────────────────────────────────────────────
        if (currentLane.nextPaths != null && currentLane.nextPaths.Count > 0)
        {
            var lp = ChooseTurnPath();

            if (lp == null || lp.path == null || lp.targetLane == null)
            {
                rec?.LogEvent(vehicleId, "FAIL_TURN_SELECTION", currentLane.name);
                Debug.LogWarning($"[FAIL SAFE] {vehicleId} couldn't pick turn path on {currentLane.name}");

                // 🔥 fallback instead of doing nothing
                if (currentLane.nextLanes != null && currentLane.nextLanes.Count > 0)
                {
                    var fallbackLane = currentLane.nextLanes[Random.Range(0, currentLane.nextLanes.Count)];

                    rec?.LogEvent(vehicleId, "FALLBACK_LANE", fallbackLane.name);
                    AssignLane(fallbackLane);
                    return;
                }

                // No fallback lanes either — this is a true dead end.
                rec?.LogEvent(vehicleId, "FAIL_NO_FALLBACK");
                Despawn();
                return;
            }

            // ✅ SUCCESS PATH SWITCH
            rec?.LogEvent(
    vehicleId,
    "PATH_SWITCH",
    lp.path.name,                 // ✅ correct path
    GetIntersectionId(),
    "",
    -1f,
    GetRouteTrace()
);
            routeHistory.Add("P:" + lp.path.name);
            currentPath = lp.path;
            currentLane = lp.targetLane;
            distanceTravelled = currentPath.GetClosestDistance(transform.position);

            if (laneManager != null)
                laneManager.RefreshVehicle(this);

            return;
        }

        // ─────────────────────────────────────────────
        // 3. FALLBACK → NEXT LANES
        // ─────────────────────────────────────────────
        if (currentLane.nextLanes != null && currentLane.nextLanes.Count > 0)
        {
            var nextLane = currentLane.nextLanes[Random.Range(0, currentLane.nextLanes.Count)];

            rec?.LogEvent(vehicleId, "LANE_CONTINUE", nextLane.name);
            routeHistory.Add("FALLBACK:" + nextLane.name);
            AssignLane(nextLane);
            return;
        }

        // ─────────────────────────────────────────────
        // 4. DEAD END
        // ─────────────────────────────────────────────
        rec?.LogEvent(vehicleId, "DEAD_END");

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
            if (lp == null || lp.path == null || lp.targetLane == null) continue;

            Vector3 fwd = transform.forward;
            Vector3 tFwd = lp.targetLane.transform.forward;
            float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot(fwd, tFwd), -1f, 1f)) * Mathf.Rad2Deg;

            weights[i] = ang < 30f ? 0.5f :
                         ang > 150f ? 0.05f :
                         Vector3.Dot(Vector3.Cross(fwd, tFwd), Vector3.up) >= 0f ? 0.3f : 0.15f;

            total += weights[i];
        }

        // If no valid path had weight, pick first fully-valid entry
        if (total <= 0f)
        {
            for (int i = 0; i < paths.Count; i++)
            {
                var lp = paths[i];
                if (lp != null && lp.path != null && lp.targetLane != null) return lp;
            }
            return null;
        }

        float pick = Random.value * total;
        float acc = 0f;

        for (int i = 0; i < paths.Count; i++)
        {
            acc += weights[i];

            if (pick <= acc)
            {
                var chosen = paths[i];
                if (chosen == null || chosen.path == null || chosen.targetLane == null) continue;

                float prob = weights[i] / total;

                rec?.LogEvent(
                    vehicleId,
                    "TURN_DECISION",
                    chosen.path.name,
                    GetIntersectionId(),
                    $"choice_{i}",
                    prob,
                    GetRouteTrace()
                );

                return chosen;
            }
        }

        // Fallback: last entry with a valid path
        for (int i = paths.Count - 1; i >= 0; i--)
        {
            var lp = paths[i];
            if (lp != null && lp.path != null && lp.targetLane != null) return lp;
        }
        return null;
    }

    void Despawn()
    {
        isWaitingForLane = true;

        rec?.LogEvent(
            vehicleId,
            "DESPAWN",
            GetPathId(),
            GetIntersectionId(),
            "",
            -1f,
            GetRouteTrace()
        );

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

        // Determine direction before switching currentLane
        pendingManeuverState = (target == leftLane) ? ManeuverState.LaneChangeLeft : ManeuverState.LaneChangeRight;

        currentLane = target;
        currentPath = target.path;
        distanceTravelled = currentPath.GetClosestDistance(transform.position);
        laneChangeCooldown = 5f;
        if (laneManager != null) laneManager.RefreshVehicle(this);
        routeHistory.Add("LC:" + target.name);

        rec?.LogEvent(
            vehicleId,
            "LANE_CHANGE",
            target.path.name,
            GetIntersectionId(),
            pendingManeuverState == ManeuverState.LaneChangeLeft ? "left" : "right",
            CurrentSpeed,
            GetRouteTrace()
        );
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