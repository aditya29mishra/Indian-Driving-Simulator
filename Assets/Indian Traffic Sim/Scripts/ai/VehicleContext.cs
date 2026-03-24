using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// VehicleContext — shared state container passed to every vehicle behaviour module.
//
// OOP role: Encapsulation + Data Hiding.
//   All mutable state that used to be scattered across TrafficVehicle as private
//   fields is centralised here. Modules read and write through this object.
//   External consumers (LaneManager, VehicleRecorder, TrafficDebugOverlay) only
//   see the read-only surface exposed by TrafficVehicle's public properties —
//   they never touch VehicleContext directly.
//
// VehicleProfile integration:
//   Profile is set once at spawn via ApplyConfig(). It is the authority for
//   all type-specific values. The individual float fields (TimeHeadway, MinimumGap,
//   etc.) are kept in sync from the profile so existing modules that read those
//   fields continue to work without change.
// ─────────────────────────────────────────────────────────────────────────────

public class VehicleContext
{
    // ── Identity ──────────────────────────────────────────────────────────
    public string VehicleId { get; private set; }
    public TrafficVehicle.DriverProfile DriverProfile { get; set; }

    // ── Vehicle type profile — set once at spawn, never mutated ──────────
    // Single source of truth for what this vehicle physically IS.
    // All type-specific behaviour reads from here — no module switches on VehicleType.
    public VehicleProfile Profile { get; set; }

    // ── Physics (set once at spawn, never changed) ────────────────────────
    public CarMove    CarMove    { get; private set; }
    public Rigidbody  Rigidbody  { get; private set; }
    public Transform  Transform  { get; private set; }

    // ── IDM config (set by Initialize, read by IDM and Negotiator) ─────────
    public float DesiredSpeedMs  { get; set; }  // dynamic — adjusted by IDM each tick
    public float BaseSpeedMs     { get; set; }  // fixed at spawn — personality speed, never changes
    public float MaxSpeedKph     { get; set; }
    public float Acceleration    { get; set; }
    public float Braking         { get; set; }
    public float TimeHeadway     { get; set; }
    public float MinimumGap      { get; set; }
    public float VehicleLength   { get; set; }
    public float StopLineDistance{ get; set; }
    // LateralVariance: kept for backward compat — Profile.Agility.maxLateralOffsetM
    // is the authoritative value when a profile is set.
    public float LateralVariance { get; set; }

    // ── Path state (written by Navigator, read by IDM and Steering) ───────
    public TrafficPath  CurrentPath       { get; set; }
    public float        DistanceTravelled { get; set; }
    public bool         IsWaitingForLane  { get; set; }
    public bool         InsideIntersection{ get; set; }

    // ── IDM output (written by IDM, read by TrafficVehicle drive output) ──
    public float IdmAccel        { get; set; }
    public bool  InRestartPhase  { get; set; }
    public float RestartTimer    { get; set; }
    public float RestartDuration { get; set; }

    // ── Signal detection (written by IDM, read by IDM and StateSystem) ────
    public TrafficSignal.SignalState PrevSignalState          { get; set; }
    public TrafficSignal.SignalState CurrentSignalStateCached { get; set; }

    // ── Intersection yield release ramp ──────────────────────────────────
    public float YieldReleaseTimer  { get; set; }
    public float _yieldRampMaxAccel { get; set; } = float.MaxValue;
    public const float YieldReleaseDuration = 1.5f;

    // ── Intersection turn type (written by IntersectionPrioritySystem) ─────
    public int CurrentTurnType { get; set; }

    // ── Lane change state (written by Negotiator) ─────────────────────────
    public bool         OvertakeBoostActive  { get; set; }
    public float        OvertakeCooldown     { get; set; }
    public float        LaneChangeCooldown   { get; set; }
    public float        LaneChangeCheckTimer { get; set; }
    public TrafficVehicle.ManeuverState PendingManeuverState { get; set; }

    // ── Stuck escape (written by TrafficVehicle main loop) ────────────────
    public float StuckTimer { get; set; }

    // ── Merge negotiation (written by Negotiator) ─────────────────────────
    public TrafficVehicle MergingVehicle  { get; set; }
    public float          MergeYieldTimer { get; set; }

    // ── System references (assigned by VehicleSpawner before Start) ───────
    public TrafficSignal  CurrentSignal { get; set; }
    public LaneManager    LaneManager   { get; set; }
    public TrafficLane    CurrentLane   { get; set; }
    public TrafficLane    LeftLane      { get; set; }
    public TrafficLane    RightLane     { get; set; }
    public TrafficVehicle LeaderVehicle { get; set; }

    // ── Perception map ────────────────────────────────────────────────────
    public VehicleMap Map { get; } = new VehicleMap();

    // ── Destination system ────────────────────────────────────────────────
    public TrafficRoadNode            DestNode             { get; set; }
    public DestinationSystem          DestinationSystem    { get; set; }
    public IntersectionPrioritySystem IntersectionPriority { get; set; }

    // ── Logging ───────────────────────────────────────────────────────────
    public TrafficDataRecorder Recorder     { get; set; }
    public List<string>        RouteHistory { get; private set; }

    // ─────────────────────────────────────────────────────────────────────
    // Derived read-only — base values
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Current speed in m/s from the Rigidbody velocity magnitude.</summary>
    public float CurrentSpeed => Rigidbody != null ? Rigidbody.velocity.magnitude : 0f;

    /// <summary>Maximum speed in m/s, derived from MaxSpeedKph.</summary>
    public float MaxSpeedMs => MaxSpeedKph / 3.6f;

    // ─────────────────────────────────────────────────────────────────────
    // Profile convenience properties — null-safe hot-path shortcuts.
    // All modules should read these instead of drilling into Profile.X.Y.Z.
    // Fall back to sensible car defaults when no profile is set (legacy spawns).
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Vehicle width in metres. Drives VehicleMap zone boundary scaling.</summary>
    public float VehicleWidth      => Profile?.Width            ?? 1.9f;

    /// <summary>Minimum gap in metres this vehicle will attempt to pass through.</summary>
    public float MinPassableGap    => Profile?.MinPassableGap   ?? MinimumGap;

    /// <summary>0-1. How freely the vehicle repositions laterally.</summary>
    public float LateralAgility    => Profile?.LateralAgility   ?? 0.3f;

    /// <summary>Maximum lateral offset in metres from lane spline centre.</summary>
    public float MaxLateralOffset  => Profile?.MaxLateralOffset ?? LateralVariance;

    /// <summary>True if this vehicle actively threads gaps between lane centres.</summary>
    public bool  CanGapThread      => Profile?.CanGapThread     ?? false;

    /// <summary>True if this vehicle can ride between two columns of traffic.</summary>
    public bool  CanLaneSplit      => Profile?.CanLaneSplit     ?? false;

    /// <summary>True if this vehicle ignores lane line discipline.</summary>
    public bool  IgnoresLaneLines  => Profile?.IgnoresLaneLines ?? false;

    /// <summary>Social weight — how much other vehicles defer to this one.</summary>
    public float SocialWeight      => Profile?.Social.socialWeight  ?? 1.0f;

    /// <summary>0-1. How assertively it claims space in negotiation scenarios.</summary>
    public float Assertiveness     => Profile?.Assertiveness    ?? 0.55f;

    /// <summary>True if this vehicle inches forward at a red signal.</summary>
    public bool  CreepsAtSignal    => Profile?.Social.creepsAtSignal    ?? false;

    /// <summary>True if this vehicle begins moving before full green.</summary>
    public bool  JumpsPrelightGreen => Profile?.Social.jumpsPrelightGreen ?? false;

    // ─────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────

    public VehicleContext(string id, Transform transform, CarMove carMove, Rigidbody rb)
    {
        VehicleId    = id;
        Transform    = transform;
        CarMove      = carMove;
        Rigidbody    = rb;
        RouteHistory = new List<string>(32);

        // Defaults — car baseline, overwritten by ApplyConfig
        DesiredSpeedMs   = 13f;
        MaxSpeedKph      = 50f;
        Acceleration     = 2.5f;
        Braking          = 5f;
        TimeHeadway      = 1.5f;
        MinimumGap       = 2f;
        VehicleLength    = 4.5f;
        StopLineDistance = 14f;
        RestartDuration  = 1.2f;

        PrevSignalState          = TrafficSignal.SignalState.Green;
        CurrentSignalStateCached = TrafficSignal.SignalState.Green;
        PendingManeuverState     = TrafficVehicle.ManeuverState.LaneKeeping;
        DriverProfile            = TrafficVehicle.DriverProfile.Normal;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Config application
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a VehicleConfig struct to all config fields in one call.
    /// When cfg.vehicleProfile is set, the profile is the authority and
    /// overrides the individual float fields so all modules get correct values.
    /// </summary>
    public void ApplyConfig(TrafficVehicle.VehicleConfig cfg)
    {
        DriverProfile    = cfg.profile;
        MaxSpeedKph      = cfg.maxSpeedKph;
        DesiredSpeedMs   = cfg.desiredSpeedMs;
        BaseSpeedMs      = cfg.desiredSpeedMs;
        Acceleration     = cfg.acceleration;
        Braking          = cfg.braking;
        TimeHeadway      = cfg.timeHeadway;
        MinimumGap       = cfg.minimumGap;
        VehicleLength    = cfg.vehicleLength;
        StopLineDistance = cfg.stopLineDistance;
        LateralVariance  = cfg.lateralVariance;
        DestNode         = cfg.destNode;

        // ── VehicleProfile override ───────────────────────────────────────
        // Profile is the authority for type-specific values.
        // Individual float fields are kept in sync so legacy module code
        // that reads ctx.TimeHeadway etc. still gets correct values.
        if (cfg.vehicleProfile != null)
        {
            Profile = cfg.vehicleProfile;

            // Physical / agility overrides from profile
            MaxSpeedKph   = Profile.Agility.maxSpeedKph;
            Acceleration  = Profile.Agility.acceleration;
            Braking       = Profile.Agility.braking;
            VehicleLength = Profile.Physical.length;

            // Social overrides — these directly affect IDM behaviour
            TimeHeadway   = Profile.Social.timeHeadway;
            MinimumGap    = Profile.Social.minimumGap;

            // Lateral variance — profile max offset × agility factor
            LateralVariance = Profile.Agility.maxLateralOffsetM * Profile.Agility.lateralAgility;

            // Desired speed and base speed: spawner applies personality variance
            // on top of profile baseline — preserve the spawner's value here.
            // (cfg.desiredSpeedMs already has variance applied by BuildConfigForType)
            DesiredSpeedMs = cfg.desiredSpeedMs;
            BaseSpeedMs    = cfg.desiredSpeedMs;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Logging
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Logs an event to the recorder if recording is active.</summary>
    public void Log(string evt, string pathId = "", string intersectionId = "",
                    string decision = "", float probability = -1f)
    {
        Recorder?.LogEvent(VehicleId, evt, pathId, intersectionId,
                           decision, probability, GetRouteTrace());
    }

    public string GetRouteTrace() => string.Join(">", RouteHistory);
}