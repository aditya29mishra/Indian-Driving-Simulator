using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// IntersectionPrioritySystem — pre-entry conflict resolution at intersections.
//
// Problem it solves:
//   Two cars at the same intersection commit to turn paths that physically cross.
//   Without this: both enter, both brake reactively mid-intersection, often collide.
//   With this: before entry, one car yields at the stop line while the other goes.
//
// How it works:
//   1. When a vehicle commits to a turn (ChooseTurnPath fires), this system
//      registers the vehicle's chosen arc with the intersection.
//   2. It scans for other registered vehicles at the same intersection whose
//      arc is geometrically incompatible (crossing or merging into same space).
//   3. A priority score is computed for each conflicting pair.
//   4. The lower-priority vehicle is marked as ShouldYieldAtEntry = true.
//   5. VehicleIDM reads ShouldYieldAtEntry and holds the vehicle at the stop line.
//   6. When the higher-priority vehicle clears the conflict zone, the waiting
//      vehicle's ShouldYieldAtEntry is cleared and it enters normally.
//
// Priority score (higher = goes first):
//   TurnType bonus: straight (3) > right turn (2) > left turn (1)
//   Profile bonus:  Aggressive (+1.5), Normal (+1.0), Cautious (+0.5)
//   Random jitter:  Random.Range(0, 1f) per vehicle per green phase
//   — jitter breaks ties between identical profiles + turn types
//   — makes behaviour feel human, not deterministic
//
// OOP role: IVehicleBehaviour + static registry (IntersectionRegistry).
//   The registry maps intersection positions → list of registered vehicles.
//   Vehicles register on turn commit, deregister on exit.
// ─────────────────────────────────────────────────────────────────────────────

// ── Static registry shared across all vehicles ────────────────────────────────
// Maps a quantised intersection world position → vehicles currently registered.
// Position is quantised to 4m grid so nearby lanes share the same bucket.
public static class IntersectionRegistry
{
    // Key: quantised intersection centre, Value: list of registered entries
    private static readonly Dictionary<Vector3Int, List<IntersectionEntry>> registry
        = new Dictionary<Vector3Int, List<IntersectionEntry>>();

    public struct IntersectionEntry
    {
        public TrafficVehicle vehicle;
        public TrafficPath     chosenArc;     // the Bezier path they committed to
        public float           priority;      // computed once at registration
        public Vector3         arcMidpoint;   // midpoint of the arc, used for cross-check
    }

    static Vector3Int Quantise(Vector3 pos) =>
        new Vector3Int(
            Mathf.RoundToInt(pos.x / 4f),
            Mathf.RoundToInt(pos.y / 4f),
            Mathf.RoundToInt(pos.z / 4f));

    /// <summary>Registers a vehicle's committed turn at this intersection position.</summary>
    public static void Register(Vector3 intersectionPos, IntersectionEntry entry)
    {
        var key = Quantise(intersectionPos);
        if (!registry.TryGetValue(key, out var list))
        {
            list = new List<IntersectionEntry>(8);
            registry[key] = list;
        }
        // Remove stale entry for this vehicle if it re-registers
        list.RemoveAll(e => e.vehicle == entry.vehicle);
        list.Add(entry);
    }

    /// <summary>Returns all registered entries at this intersection position.</summary>
    public static List<IntersectionEntry> GetEntries(Vector3 intersectionPos)
    {
        var key = Quantise(intersectionPos);
        registry.TryGetValue(key, out var list);
        return list;
    }

    /// <summary>Removes a vehicle from the registry (called on exit or despawn).</summary>
    public static void Deregister(TrafficVehicle vehicle, Vector3 intersectionPos)
    {
        var key = Quantise(intersectionPos);
        if (registry.TryGetValue(key, out var list))
            list.RemoveAll(e => e.vehicle == vehicle);
    }

    /// <summary>Cleans up null vehicle references across all buckets.</summary>
    public static void PurgeNulls()
    {
        foreach (var list in registry.Values)
            list.RemoveAll(e => e.vehicle == null);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IntersectionPrioritySystem — per-vehicle behaviour module
// ─────────────────────────────────────────────────────────────────────────────

public class IntersectionPrioritySystem : IVehicleBehaviour
{
    private readonly VehicleContext ctx;
    private readonly TrafficVehicle owner;
    private readonly PerceptionSystem perception;

    // Radius to consider as "same intersection" for conflict detection
    private const float IntersectionRadius = 18f;

    // How far past the stop line before we consider "cleared the conflict zone"
    private const float ClearDistance = 12f;

    // True when this vehicle must wait at the stop line for a higher-priority car
    public bool ShouldYieldAtEntry { get; private set; }

    // The intersection world position this vehicle registered at
    private Vector3 registeredAt;
    private bool    isRegistered = false;

    // Per-phase random jitter, set once when turn is committed
    private float priorityJitter;

    public IntersectionPrioritySystem(VehicleContext ctx, TrafficVehicle owner,
                                      PerceptionSystem perception)
    {
        this.ctx         = ctx;
        this.owner       = owner;
        this.perception  = perception;
    }

    public void OnSpawn()  { Deregister(); ShouldYieldAtEntry = false; }
    public void OnDespawn(){ Deregister(); }

    public void Tick(float dt)
    {
        // Only active when approaching an intersection (has signal, not inside yet)
        bool approachingIntersection = ctx.CurrentSignal != null &&
                                       ctx.CurrentLane   != null &&
                                       !ctx.InsideIntersection;

        // Clear registration and yield flag once we've entered and cleared
        if (isRegistered && ctx.InsideIntersection)
        {
            // Check if we've moved far enough through the intersection to be clear
            float distFromEntry = Vector3.Distance(ctx.Transform.position, registeredAt);
            if (distFromEntry > ClearDistance)
            {
                // We've cleared — notify waiting vehicles
                NotifyWaitersWeCleated();
                Deregister();
                ShouldYieldAtEntry = false;
            }
        }

        if (!approachingIntersection)
        {
            if (!ctx.InsideIntersection && isRegistered)
                Deregister();
            return;
        }

        // Refresh conflict check while waiting at stop line
        if (isRegistered && ShouldYieldAtEntry)
            RefreshYieldDecision();
    }

    /// <summary>
    /// Called by VehicleNavigator.ChooseTurnPath when a turn arc is committed.
    /// Registers this vehicle with the intersection, computes priority,
    /// and determines if it must yield.
    /// </summary>
    public void OnTurnCommitted(TrafficPath chosenArc, Vector3 intersectionCentre)
    {
        registeredAt   = intersectionCentre;
        priorityJitter = Random.Range(0f, 1f); // random per green phase

        float priority = ComputePriority(chosenArc);

        // Register
        var entry = new IntersectionRegistry.IntersectionEntry
        {
            vehicle    = owner,
            chosenArc  = chosenArc,
            priority   = priority,
            arcMidpoint = chosenArc != null && chosenArc.waypoints.Count > 0
                ? chosenArc.GetPointAtDistance(chosenArc.TotalLength * 0.5f)
                : intersectionCentre
        };
        IntersectionRegistry.Register(intersectionCentre, entry);
        isRegistered = true;

        // Immediately check if we need to yield
        RefreshYieldDecision();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Priority calculation
    // ─────────────────────────────────────────────────────────────────────────

    float ComputePriority(TrafficPath arc)
    {
        // Turn type score: classify arc by how much heading changes
        float turnScore = ClassifyTurn(arc);

        // Profile score
        float profileScore = ctx.DriverProfile == TrafficVehicle.DriverProfile.Aggressive ? 1.5f
                           : ctx.DriverProfile == TrafficVehicle.DriverProfile.Cautious    ? 0.5f
                           : 1.0f;

        // jitter: breaks symmetry between identical situations
        return turnScore + profileScore + priorityJitter;
    }

    float ClassifyTurn(TrafficPath arc)
    {
        if (arc == null || arc.waypoints.Count < 2) return 2f; // assume straight

        // Measure how much the arc turns by comparing entry and exit headings
        Vector3 entryDir = (arc.waypoints[1].position - arc.waypoints[0].position).normalized;
        Vector3 exitDir  = (arc.waypoints[arc.waypoints.Count - 1].position -
                            arc.waypoints[arc.waypoints.Count - 2].position).normalized;

        float turnDeg = Vector3.Angle(entryDir, exitDir);

        if (turnDeg < 25f)  return 3f; // straight — highest priority
        // Determine left vs right by cross product
        float cross = Vector3.Dot(Vector3.Cross(entryDir, exitDir), Vector3.up);
        if (cross >= 0f)    return 2f; // right turn — medium priority
        return 1f;                      // left turn  — lowest priority
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Conflict detection
    // ─────────────────────────────────────────────────────────────────────────

    void RefreshYieldDecision()
    {
        if (!isRegistered) return;
        var entries = IntersectionRegistry.GetEntries(registeredAt);
        if (entries == null || entries.Count <= 1) { ShouldYieldAtEntry = false; return; }

        // Find my own entry
        IntersectionRegistry.IntersectionEntry? myEntry = null;
        foreach (var e in entries)
            if (e.vehicle == owner) { myEntry = e; break; }
        if (myEntry == null) { ShouldYieldAtEntry = false; return; }

        float myPriority = myEntry.Value.priority;
        perception.Map.crossingPaths?.Clear();
        perception.Map.EnsureList();
        bool mustYield = false;

        foreach (var other in entries)
        {
            if (other.vehicle == owner) continue;
            if (other.vehicle == null)  continue;

            // Check if this vehicle's arc actually conflicts with ours
            if (!ArcsConflict(myEntry.Value.arcMidpoint, other.arcMidpoint)) continue;

            // Add to crossing paths regardless — NeighbourMap awareness
            perception.Map.crossingPaths.Add(other.vehicle);

            // If the other has higher priority, we yield
            if (other.priority > myPriority)
                mustYield = true;
        }

        ShouldYieldAtEntry = mustYield;
    }

    /// <summary>
    /// Two arcs conflict if their midpoints are close — both pass through
    /// the same zone of the intersection. 6m threshold covers a car length
    /// plus margin.
    /// </summary>
    bool ArcsConflict(Vector3 myMid, Vector3 otherMid)
    {
        // If arc midpoints are within 8m of each other, the arcs share space
        return Vector3.Distance(myMid, otherMid) < 8f;
    }

    void NotifyWaitersWeCleated()
    {
        // When we clear, refresh every vehicle registered at this intersection
        // so they re-evaluate whether they can now enter
        var entries = IntersectionRegistry.GetEntries(registeredAt);
        if (entries == null) return;
        foreach (var e in entries)
        {
            if (e.vehicle == null || e.vehicle == owner) continue;
            e.vehicle.intersectionPriority?.RefreshYieldDecision();
        }
    }

    void Deregister()
    {
        if (isRegistered)
        {
            IntersectionRegistry.Deregister(owner, registeredAt);
            isRegistered = false;
        }
    }
}
