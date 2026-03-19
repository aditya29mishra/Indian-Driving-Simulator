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
// ─────────────────────────────────────────────────────────────────────────────

public class VehicleContext
{
    // ── Identity ──────────────────────────────────────────────────────────
    public string VehicleId { get; private set; }
    public TrafficVehicle.DriverProfile DriverProfile { get; set; }

    // ── Physics (set once at spawn, never changed) ────────────────────────
    public CarMove CarMove       { get; private set; }
    public Rigidbody Rigidbody  { get; private set; }
    public Transform Transform  { get; private set; }

    // ── IDM config (set by Initialize, read by IDM and Negotiator) ─────────
    public float DesiredSpeedMs  { get; set; }
    public float MaxSpeedKph     { get; set; }
    public float Acceleration    { get; set; }
    public float Braking         { get; set; }
    public float TimeHeadway     { get; set; }
    public float MinimumGap      { get; set; }
    public float VehicleLength   { get; set; }
    public float StopLineDistance{ get; set; }
    // LateralVariance: max metres of random lateral offset applied to pure-pursuit target.
    // Models Indian driving where no vehicle stays perfectly centred in its lane.
    // Aggressive = higher offset (assertive lane position), Cautious = lower (hug the edge).
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
    public TrafficSignal.SignalState PrevSignalState         { get; set; }
    public TrafficSignal.SignalState CurrentSignalStateCached{ get; set; }

    // ── Lane change state (written by Negotiator) ─────────────────────────
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

    // ── Destination system ────────────────────────────────────────────────
    // DestNode: the TrafficRoadNode (NodeType.End) this vehicle is heading toward.
    // Set by VehicleSpawner.PickDestinationNode at spawn.
    // Read by DestinationSystem every tick for arrival check.
    // Read by VehicleNavigator.ChooseTurnPath for turn bias.
    public TrafficRoadNode   DestNode          { get; set; }

    // Module references — set by TrafficVehicle.Awake after modules are created.
    // Stored here so VehicleNavigator and VehicleIDM can reach them without
    // needing a reference back to the MonoBehaviour.
    public DestinationSystem          DestinationSystem    { get; set; }
    public IntersectionPrioritySystem IntersectionPriority { get; set; }

    // ── Logging ───────────────────────────────────────────────────────────
    public TrafficDataRecorder Recorder    { get; set; }
    public List<string>        RouteHistory{ get; private set; }

    // ── Derived read-only ─────────────────────────────────────────────────
    /// <summary>Current speed in m/s from the Rigidbody velocity magnitude.</summary>
    public float CurrentSpeed => Rigidbody != null ? Rigidbody.velocity.magnitude : 0f;

    /// <summary>Maximum speed in m/s, derived from MaxSpeedKph.</summary>
    public float MaxSpeedMs => MaxSpeedKph / 3.6f;

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

        // Defaults matching the original ApplyDefaultConfig
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

    /// <summary>Applies a VehicleConfig struct to all config fields in one call.</summary>
    public void ApplyConfig(TrafficVehicle.VehicleConfig cfg)
    {
        DriverProfile    = cfg.profile;
        MaxSpeedKph      = cfg.maxSpeedKph;
        DesiredSpeedMs   = cfg.desiredSpeedMs;
        Acceleration     = cfg.acceleration;
        Braking          = cfg.braking;
        TimeHeadway      = cfg.timeHeadway;
        MinimumGap       = cfg.minimumGap;
        VehicleLength    = cfg.vehicleLength;
        StopLineDistance = cfg.stopLineDistance;
        LateralVariance  = cfg.lateralVariance;
        DestNode         = cfg.destNode;
    }

    /// <summary>Logs an event to the recorder if recording is active.</summary>
    public void Log(string evt, string pathId = "", string intersectionId = "",
                    string decision = "", float probability = -1f)
    {
        Recorder?.LogEvent(VehicleId, evt, pathId, intersectionId,
                           decision, probability, GetRouteTrace());
    }

    public string GetRouteTrace() => string.Join(">", RouteHistory);
}