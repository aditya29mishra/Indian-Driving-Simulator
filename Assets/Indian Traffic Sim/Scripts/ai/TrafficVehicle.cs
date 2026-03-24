using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficVehicle — orchestrator for the vehicle AI system.
//
// OOP role: Composition + Interface-driven tick loop.
//   This MonoBehaviour creates VehicleContext (shared state), instantiates all
//   behaviour modules (IDM, Steering, Navigator, Negotiator), and drives them
//   via the IVehicleBehaviour interface. It does not contain AI logic itself —
//   it only wires the modules together and reads their outputs to produce the
//   final carMove.Move() call.
//
//   Public surface exposed here is what external scripts consume:
//     LaneManager  — reads leaderVehicle, currentLane, CurrentSignalState
//     VehicleRecorder — reads all state properties via context
//     TrafficDebugOverlay / TrafficInspector — reads display properties
//     VehicleSpawner — calls Initialize() and navigator.AssignLane()
// ─────────────────────────────────────────────────────────────────────────────

[RequireComponent(typeof(CarMove))]
[RequireComponent(typeof(VehicleRecorder))]
public partial class TrafficVehicle : MonoBehaviour
{
    // ── Enums and config struct (kept here for external visibility) ────────
    public enum MotionState   { Idle, Cruising, Following, Stopped, Starting, Braking }
    public enum SignalStateEx { None, Approaching, WaitingRed, Released }
    public enum ManeuverState { LaneKeeping, LaneChangeLeft, LaneChangeRight, Turning }
    public enum DriverProfile { Cautious, Normal, Aggressive }

    public struct VehicleConfig
    {
        public DriverProfile profile;
        public float maxSpeedKph, desiredSpeedMs, acceleration, braking;
        public float timeHeadway, minimumGap, vehicleLength, stopLineDistance;
        // lateralVariance: metres of random offset added to steering look-ahead target.
        // Set by VehicleSpawner from BehaviourDistribution.lateralVariance * profile multiplier.
        public float lateralVariance;
        // destNode: the RoadEnd node this vehicle is navigating toward.
        // Set by VehicleSpawner.PickDestinationNode at spawn. Null = random wandering (legacy).
        public TrafficRoadNode destNode;
        // vehicleProfile: full type profile — Physical, Agility, Social specs.
        // Null = legacy spawn (float fields used directly).
        public VehicleProfile vehicleProfile;
    }

    // ── Public state (read by LaneManager, VehicleRecorder, debug tools) ──
    public MotionState    CurrentMotionState  { get; private set; }
    public SignalStateEx  CurrentSignalState  { get; private set; }
    public ManeuverState  CurrentManeuverState{ get; private set; }
    public MotionState    PreviousMotionState { get; private set; }
    public SignalStateEx  PreviousSignalState { get; private set; }
    public ManeuverState  PreviousManeuverState{ get; private set; }
    public bool IsBlocked { get; private set; }
    public bool IsStuck   { get; private set; }
    public bool IsInQueue { get; private set; }

    // ── Properties forwarded from VehicleContext ───────────────────────────
    public float CurrentSpeed      => ctx?.CurrentSpeed ?? 0f;
    public float DesiredSpeed      => ctx?.DesiredSpeedMs ?? 0f;
    public float MaxSpeed          => ctx?.MaxSpeedMs ?? 0f;
    public float TimeHeadway       => ctx?.TimeHeadway ?? 0f;
    public float MinimumGap        => ctx?.MinimumGap ?? 0f;
    public float StopLineDistance  => ctx?.StopLineDistance ?? 0f;
    public float DistanceTravelled => ctx?.DistanceTravelled ?? 0f;
    public TrafficPath CurrentPath => ctx?.CurrentPath;
    public bool  IsChangingLane    => ctx != null && ctx.LaneChangeCooldown > 0f && !ctx.InsideIntersection;
    public float LaneChangeProgress=> ctx != null ? Mathf.Clamp01(1f - ctx.LaneChangeCooldown / 5f) : 0f;
    public bool  IsInsideIntersection => ctx?.InsideIntersection ?? false;
    // VehicleMap — local minimap populated by WorldPerception each tick
    public VehicleMap Map => ctx?.Map;
    public PerceptionSystem           Perception           => perception;
    public IntersectionPrioritySystem IntersectionPriority => intersectionPriority;
    public DestinationSystem          Destination          => destination;

    // ── Fields accessed directly by NegotiationScenario subclasses ────────
    // These forward to ctx for backwards-compat with existing external code.
    [HideInInspector] public TrafficSignal  currentSignal  { get => ctx?.CurrentSignal;  set { if (ctx != null) ctx.CurrentSignal  = value; } }
    [HideInInspector] public LaneManager   laneManager    { get => ctx?.LaneManager;    set { if (ctx != null) ctx.LaneManager    = value; } }
    [HideInInspector] public TrafficLane   currentLane    { get => ctx?.CurrentLane;    set { if (ctx != null) ctx.CurrentLane    = value; } }
    [HideInInspector] public TrafficLane   leftLane       { get => ctx?.LeftLane;       set { if (ctx != null) ctx.LeftLane       = value; } }
    [HideInInspector] public TrafficLane   rightLane      { get => ctx?.RightLane;      set { if (ctx != null) ctx.RightLane      = value; } }
    [HideInInspector] public TrafficVehicle leaderVehicle { get => ctx?.LeaderVehicle;  set { if (ctx != null) ctx.LeaderVehicle  = value; } }
    [HideInInspector] public DriverProfile  driverProfile  { get => ctx?.DriverProfile ?? DriverProfile.Normal; set { if (ctx != null) ctx.DriverProfile = value; } }
    [HideInInspector] public TrafficVehicle mergingVehicle { get => ctx?.MergingVehicle;  set { if (ctx != null) ctx.MergingVehicle  = value; } }
    [HideInInspector] public float          mergeYieldTimer{ get => ctx?.MergeYieldTimer ?? 0f; set { if (ctx != null) ctx.MergeYieldTimer = value; } }

    // ── Expose context for NegotiationScenario base-class equality checks ─
    public VehicleContext context => ctx;
    public SignalStateEx  CurrentSignalState_Public => CurrentSignalState;

    // ── Internal modules ──────────────────────────────────────────────────
    private VehicleContext    ctx;
    private VehicleIDM        idm;
    private VehicleSteering   steering;
    private VehicleNavigator  navigator;
    private PerceptionSystem            perception;
    private IntersectionPrioritySystem  intersectionPriority;
    private DestinationSystem           destination;
    // Public so LaneManager can read negotiator.QueueRipple.IsFrontOfQueue
    // for the queue-ripple green-light restart logic.
    public  VehicleNegotiator negotiator { get; private set; }
    private List<IVehicleBehaviour> modules;

    private float adjacencyTimer = 0f;
    private float stuckTimer     = 0f;
    private const float stuckThreshold = 5f;

    // ─────────────────────────────────────────────────────────────────────
    // Unity lifecycle
    // ─────────────────────────────────────────────────────────────────────

    void Awake()
    {
        var carMove = GetComponent<CarMove>();
        var rb      = GetComponent<Rigidbody>();
        var rec     = FindObjectOfType<TrafficDataRecorder>();

        string id = name + "_" + Random.Range(1000, 9999);
        ctx       = new VehicleContext(id, transform, carMove, rb);
        ctx.Recorder = rec;

        // Instantiate modules — order here is the tick order
        idm        = new VehicleIDM(ctx);
        steering   = new VehicleSteering(ctx);
        navigator  = new VehicleNavigator(ctx, idm, this);
        negotiator = new VehicleNegotiator(ctx, idm, navigator, this);

        // New systems — perception must be first so map is populated before others read it
        perception           = new PerceptionSystem(ctx, this);
        intersectionPriority = new IntersectionPrioritySystem(ctx, this, perception);
        destination          = new DestinationSystem(ctx, this);

        // Wire module references into ctx so VehicleNavigator and VehicleIDM can reach them
        ctx.IntersectionPriority = intersectionPriority;
        ctx.DestinationSystem    = destination;

        modules = new List<IVehicleBehaviour>
        {
            perception,           // first — populates NeighbourMap before others read it
            steering,
            idm,
            negotiator,
            intersectionPriority,
            destination,
        };
        // Navigator ticked manually inside FixedUpdate after path checks
    }

    /// <summary>Called by VehicleSpawner with all config and system refs before Start fires.</summary>
    public void Initialize(VehicleConfig cfg)
    {
        ctx.ApplyConfig(cfg);
        GetComponent<CarMove>()?.SetTopSpeed(cfg.maxSpeedKph);
        // Apply profile-specific physics settings to CarMove
        if (cfg.vehicleProfile != null)
        {
            GetComponent<CarMove>()?.SetMaxSteerAngle(cfg.vehicleProfile.Agility.maxSteerAngle);
        }
    }

    void Start()
    {
        if (ctx.MaxSpeedKph <= 0f) ctx.ApplyConfig(DefaultConfig());

        // Fire OnSpawn on all modules — called here (not in Awake/Initialize) so that
        // AssignLane and DestNode are already set by VehicleSpawner before modules initialise.
        foreach (var m in modules) m.OnSpawn();
        navigator.OnSpawn();
        ctx.RouteHistory.Clear();
        ctx.RouteHistory.Add("START");
        ctx.Log("SPAWN");
    }

    void OnDestroy()
    {
        foreach (var m in modules) m.OnDespawn();
        navigator.OnDespawn();
        ctx?.CarMove?.Move(0f, -1f, 0f, 0f);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Main loop — orchestration only
    // ─────────────────────────────────────────────────────────────────────

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;

        if (ctx.IsWaitingForLane || ctx.CurrentPath == null || ctx.CurrentPath.waypoints.Count == 0)
        {
            // -1f footbrake → CarMove re-negates to +1f → full hold. Intentional.
            ctx.CarMove.Move(0f, -1f, 0f, 0f);
            return;
        }

        // Periodic adjacency refresh (every 5s)
        adjacencyTimer += dt;
        if (adjacencyTimer >= 5f) { adjacencyTimer = 0f; ctx.LaneManager?.RefreshVehicle(this); }

        // Cache signal state for this tick
        ctx.CurrentSignalStateCached = ctx.CurrentSignal != null && ctx.CurrentLane != null
            ? ctx.CurrentSignal.GetStateForLane(ctx.CurrentLane)
            : TrafficSignal.SignalState.Green;

        // Tick all behaviour modules
        foreach (var m in modules) m.Tick(dt);

        // Path end check — navigator handles advancement
        navigator.Tick(dt);

        // Stuck escape
        if (ctx.CurrentSpeed < 0.2f && ctx.LeaderVehicle != null)
            stuckTimer += dt;
        else
            stuckTimer = 0f;

        ctx.StuckTimer = stuckTimer;

        if (stuckTimer >= stuckThreshold)
        {
            if (ctx.InsideIntersection)
            {
                ctx.IsWaitingForLane = true;
                Object.Destroy(gameObject, 0.3f);
            }
            else if (stuckTimer >= stuckThreshold * 2f)
            {
                ctx.IdmAccel = Mathf.Max(ctx.IdmAccel, 0.3f);
            }
        }

        // State machine update — reads all modules, writes public state properties
        UpdateStateSystem();

        // ── Drive output ──────────────────────────────────────────────────
        float throttle, brake;
        if (ctx.InRestartPhase)
        {
            // Green-light restart: ramp throttle from 0.4 → 1.0 over restartDuration
            float boost = Mathf.Lerp(0.4f, 1f, 1f - (ctx.RestartTimer / ctx.RestartDuration));
            throttle = Mathf.Clamp01(boost);
            brake    = 0f;
        }
        else
        {
            throttle = Mathf.Clamp01(ctx.IdmAccel  / Mathf.Max(ctx.Acceleration, 0.1f));
            brake    = Mathf.Clamp01(-ctx.IdmAccel / Mathf.Max(ctx.Braking,      0.1f));
        }

        // Sign convention: footbrake passed as negative (-brake → CarMove re-negates to +brake)
        ctx.CarMove.Move(throttle, -brake, 0f, steering.SteerOutput);
    }

    // ─────────────────────────────────────────────────────────────────────
    // State aggregation — reads all modules, writes public properties
    // ─────────────────────────────────────────────────────────────────────

    void UpdateStateSystem()
    {
        PreviousMotionState   = CurrentMotionState;
        PreviousSignalState   = CurrentSignalState;
        PreviousManeuverState = CurrentManeuverState;

        float v = CurrentSpeed;

        // Signal state
        if (idm.MustStopForSignal())          CurrentSignalState = SignalStateEx.WaitingRed;
        else if (ctx.InRestartPhase)           CurrentSignalState = SignalStateEx.Released;
        else if (ctx.CurrentSignal != null)    CurrentSignalState = SignalStateEx.Approaching;
        else                                   CurrentSignalState = SignalStateEx.None;

        // Maneuver state
        if (ctx.InsideIntersection)
            CurrentManeuverState = ManeuverState.Turning;
        else if (ctx.LaneChangeCooldown > 0f)
            CurrentManeuverState = ctx.PendingManeuverState;
        else
            CurrentManeuverState = ManeuverState.LaneKeeping;

        // Flags
        IsBlocked = ctx.LeaderVehicle != null && ctx.LeaderVehicle.CurrentSpeed < v;
        IsStuck   = stuckTimer > stuckThreshold * 0.5f;
        IsInQueue = ctx.LeaderVehicle != null && v < 2f;

        // Motion state
        if (v < 0.2f)
        {
            CurrentMotionState = CurrentSignalState == SignalStateEx.WaitingRed ? MotionState.Stopped
                               : IsBlocked ? MotionState.Following : MotionState.Idle;
        }
        else
        {
            CurrentMotionState = ctx.InRestartPhase       ? MotionState.Starting
                               : ctx.IdmAccel < -0.1f     ? MotionState.Braking
                               : ctx.LeaderVehicle != null ? MotionState.Following
                               :                             MotionState.Cruising;
        }

        // Log on transition only
        if (ctx.Recorder != null)
        {
            if (CurrentMotionState   != PreviousMotionState)   ctx.Log("STATE_MOTION",   CurrentMotionState.ToString());
            if (CurrentSignalState   != PreviousSignalState)   ctx.Log("STATE_SIGNAL",   CurrentSignalState.ToString());
            if (CurrentManeuverState != PreviousManeuverState) ctx.Log("STATE_MANEUVER", CurrentManeuverState.ToString());
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public API (called by VehicleSpawner, LaneManager, debug tools)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Assigns the vehicle to a lane. Called by VehicleSpawner and LaneConnector.</summary>
    public void AssignLane(TrafficLane lane, bool fromSpawn = false)
        => navigator.AssignLane(lane, fromSpawn);

    /// <summary>Forces a lane change check — called by TrafficStressTester.</summary>
    public void CheckLaneChange() { /* Negotiator handles this on its own timer */ }

    public string GetVehicleId()    => ctx?.VehicleId ?? name;
    public string GetPathId()       => ctx?.CurrentPath ? ctx.CurrentPath.name : "none";
    public string GetIntersectionId()=> ctx?.CurrentLane && ctx.CurrentLane.road != null ? ctx.CurrentLane.road.name : "none";
    public string GetRouteTrace()   => ctx?.GetRouteTrace() ?? "";
    public Vector3 GetVelocity()    => ctx?.Rigidbody != null ? ctx.Rigidbody.velocity : Vector3.zero;

    // ─────────────────────────────────────────────────────────────────────
    // Gizmos — see TrafficVehicleGizmos.cs (partial class)
    // ─────────────────────────────────────────────────────────────────────

    static VehicleConfig DefaultConfig() => new VehicleConfig
    {
        profile          = DriverProfile.Normal,
        maxSpeedKph      = 50f,
        desiredSpeedMs   = 13f,
        acceleration     = 2.5f,
        braking          = 5f,
        timeHeadway      = 1.5f,
        minimumGap       = 2f,
        vehicleLength    = 4.5f,
        stopLineDistance = 14f,
    };
}