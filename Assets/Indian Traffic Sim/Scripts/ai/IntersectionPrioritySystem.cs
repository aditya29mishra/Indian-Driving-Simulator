using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// IntersectionPrioritySystem — decides WHO yields at intersection entry.
//
// Responsibility is narrow and clean:
//   1. When car commits to a turn arc → register + classify turn type
//   2. Geometric conflict detection against other registered arcs
//   3. Priority comparison → set ShouldYieldAtEntry once
//   4. Self-release via VehicleMap.Crossing (IDM watches the map, not this system)
//   5. Yield timeout → prevents deadlock
//
// What this system does NOT do (handled by VehicleMap + IDM):
//   - Track when conflicting car has cleared (map[Crossing] empties automatically)
//   - Apply braking force (IDM reads map[Crossing] and brakes)
//   - Notify other vehicles (each car self-releases via its own map)
//
// Priority rules (left-hand traffic):
//   Right turn  = 3  — shortest arc, never crosses anyone
//   Straight    = 2  — crosses left-turn arcs only
//   Left turn   = 1  — longest arc, crosses all others
//   Same type   → profile tiebreak (Aggressive > Normal > Cautious)
//                → first-registered wins if still tied
//
// Geometric conflict = any waypoint from arc A within 2.5m of any waypoint
// from arc B. More accurate than midpoint distance.
// ─────────────────────────────────────────────────────────────────────────────

// ── Static registry ───────────────────────────────────────────────────────────
public static class IntersectionRegistry
{
    private static readonly Dictionary<Vector3Int, List<IntersectionEntry>> registry
        = new Dictionary<Vector3Int, List<IntersectionEntry>>();

    public struct IntersectionEntry
    {
        public TrafficVehicle vehicle;
        public TrafficPath    chosenArc;
        public int            turnType;      // 3=right, 2=straight, 1=left
        public float          priority;      // turnType + profile tiebreak
        public float          registeredTime;// Time.time at registration
    }

    static Vector3Int Quantise(Vector3 pos) =>
        new Vector3Int(
            Mathf.RoundToInt(pos.x / 4f),
            Mathf.RoundToInt(pos.y / 4f),
            Mathf.RoundToInt(pos.z / 4f));

    public static void Register(Vector3 intersectionPos, IntersectionEntry entry)
    {
        var key = Quantise(intersectionPos);
        if (!registry.TryGetValue(key, out var list))
        {
            list = new List<IntersectionEntry>(8);
            registry[key] = list;
        }
        list.RemoveAll(e => e.vehicle == entry.vehicle);
        list.Add(entry);
    }

    public static List<IntersectionEntry> GetEntries(Vector3 intersectionPos)
    {
        var key = Quantise(intersectionPos);
        registry.TryGetValue(key, out var list);
        return list;
    }

    public static void Deregister(TrafficVehicle vehicle, Vector3 intersectionPos)
    {
        var key = Quantise(intersectionPos);
        if (registry.TryGetValue(key, out var list))
            list.RemoveAll(e => e.vehicle == vehicle);
    }

    public static void PurgeNulls()
    {
        foreach (var list in registry.Values)
            list.RemoveAll(e => e.vehicle == null);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IntersectionPrioritySystem — per-vehicle module
// ─────────────────────────────────────────────────────────────────────────────

public class IntersectionPrioritySystem : IVehicleBehaviour
{
    private readonly VehicleContext ctx;
    private readonly TrafficVehicle owner;

    public bool ShouldYieldAtEntry { get; private set; }

    // The specific vehicle we are yielding to — tracked for release condition
    public TrafficVehicle YieldingToVehicle { get; private set; }

    private Vector3 registeredAt;
    private bool    isRegistered      = false;
    private float   yieldTimer        = 0f;
    private float   pendingDecideTimer = 0f;  // delay before first yield decision
    private IntersectionRegistry.IntersectionEntry pendingEntry;
    private const float YieldTimeout = 8f; // max wait before forcing entry

    public IntersectionPrioritySystem(VehicleContext ctx, TrafficVehicle owner,
                                      PerceptionSystem perception)
    {
        this.ctx   = ctx;
        this.owner = owner;
    }

    public void OnSpawn()  { Deregister(); ShouldYieldAtEntry = false; yieldTimer = 0f; }
    public void OnDespawn(){ Deregister(); }

    public void Tick(float dt)
    {
        // Deregister once we've entered and are through the intersection
        if (isRegistered && ctx.InsideIntersection)
        {
            float distFromEntry = Vector3.Distance(ctx.Transform.position, registeredAt);
            if (distFromEntry > 14f)
            {
                Deregister();
                ShouldYieldAtEntry = false;
                yieldTimer = 0f;
            }
            return;
        }

        // Delayed first yield decision — ensures all simultaneous registrations visible
        if (pendingDecideTimer > 0f)
        {
            pendingDecideTimer -= dt;
            if (pendingDecideTimer <= 0f)
            {
                pendingDecideTimer = 0f;
                DecideYield(pendingEntry);
            }
        }

        if (!isRegistered) return;
        if (!ShouldYieldAtEntry) return;

        yieldTimer += dt;

        // Release conditions — only release when the causing car has actually passed:
        //
        // Condition A: causing car is now in Crossing zone (entered intersection,
        //   confirmed present, we can see it crossing) — keep waiting
        //
        // Condition B: causing car was in Crossing, now gone from ALL zones —
        //   it has fully passed through → release
        //
        // Condition C: causing car is in FrontClose/Mid/Far ahead of us in the
        //   same direction → it has passed and is ahead on exit road → release
        //
        // Condition D: timeout — deadlock prevention

        bool causingCarCleared = false;

        if (YieldingToVehicle == null)
        {
            // No specific car tracked — use map[Crossing] as before
            var crossCell2 = ctx.Map?[MapZone.Crossing];
            causingCarCleared = (crossCell2 == null || crossCell2.vehicle == null);
        }
        else
        {
            // Track the specific car we are yielding to
            var map = ctx.Map;
            bool isInCrossing  = map != null && map[MapZone.Crossing].vehicle == YieldingToVehicle;
            bool isAheadOfUs   = IsVehicleAheadAndCleared(YieldingToVehicle);

            if (isInCrossing)
            {
                // Still crossing — keep waiting, reset partial timer
                causingCarCleared = false;
            }
            else if (isAheadOfUs)
            {
                // Has passed us and is now ahead — safe to enter
                causingCarCleared = true;
            }
            else
            {
                // Not in crossing, not ahead — either hasn't arrived yet or gone
                // Only release if it's been long enough (partial timeout)
                causingCarCleared = yieldTimer > YieldTimeout * 0.5f;
            }
        }

        if (causingCarCleared)
        {
            ctx.Log("INTERSECTION_ENTER", YieldingToVehicle?.GetVehicleId() ?? "",
                    registeredAt.ToString("F0"), "causing_car_cleared");
            ShouldYieldAtEntry    = false;
            YieldingToVehicle     = null;
            yieldTimer            = 0f;
            ctx.YieldReleaseTimer = VehicleContext.YieldReleaseDuration;
            return;
        }

        // Deadlock detection: both cars stopped and both yielding to each other
        // If ego is stopped AND causing car is also stopped AND waited > 2s
        // → break deadlock by vehicle ID (one car must go first)
        if (yieldTimer > 2f && ctx.CurrentSpeed < 0.2f && YieldingToVehicle != null)
        {
            bool otherAlsoStopped = YieldingToVehicle.CurrentSpeed < 0.2f;
            bool otherAlsoYielding = YieldingToVehicle.IntersectionPriority?.ShouldYieldAtEntry ?? false;

            if (otherAlsoStopped && otherAlsoYielding)
            {
                // Both deadlocked — lower vehicle ID goes first (deterministic)
                bool iGoFirst = string.Compare(owner.GetVehicleId(),
                                               YieldingToVehicle.GetVehicleId(),
                                               System.StringComparison.Ordinal) < 0;
                if (iGoFirst)
                {
                    ctx.Log("INTERSECTION_ENTER", YieldingToVehicle.GetVehicleId(),
                            registeredAt.ToString("F0"), "deadlock_broken");
                    ShouldYieldAtEntry  = false;
                    YieldingToVehicle   = null;
                    yieldTimer          = 0f;
                    ctx.YieldReleaseTimer = VehicleContext.YieldReleaseDuration;
                    return;
                }
            }
        }

        // Hard timeout — prevents permanent deadlock
        if (yieldTimer >= YieldTimeout)
        {
            ctx.Log("INTERSECTION_ENTER", "", registeredAt.ToString("F0"),
                    "yield_timeout");
            ShouldYieldAtEntry  = false;
            YieldingToVehicle   = null;
            yieldTimer          = 0f;
            ctx.YieldReleaseTimer = VehicleContext.YieldReleaseDuration;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Turn commitment — called by VehicleNavigator when arc is chosen
    // ─────────────────────────────────────────────────────────────────────────

    public void OnTurnCommitted(TrafficPath chosenArc, Vector3 intersectionCentre)
    {
        registeredAt = intersectionCentre;
        yieldTimer   = 0f;

        int   turnType = ClassifyTurn(chosenArc);
        float priority = ComputePriority(turnType);

        // Store on context so IDM + WorldPerception can read it
        // without needing registry lookup
        ctx.CurrentTurnType = turnType;

        var entry = new IntersectionRegistry.IntersectionEntry
        {
            vehicle        = owner,
            chosenArc      = chosenArc,
            turnType       = turnType,
            priority       = priority,
            registeredTime = Time.time
        };
        pendingEntry       = entry;
        IntersectionRegistry.Register(intersectionCentre, entry);
        isRegistered       = true;
        pendingDecideTimer = 0.05f; // small delay so simultaneous registrations
                                    // are all visible before we decide yield

        ctx.Log("INTERSECTION_REGISTERED",
            chosenArc?.name ?? "none",
            intersectionCentre.ToString("F0"),
            $"turn={TurnName(turnType)} priority={priority:F2}",
            priority);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Priority + yield decision
    // ─────────────────────────────────────────────────────────────────────────

    void DecideYield(IntersectionRegistry.IntersectionEntry myEntry)
    {
        ShouldYieldAtEntry = false;

        var entries = IntersectionRegistry.GetEntries(registeredAt);
        if (entries == null || entries.Count <= 1) return;

        bool mustYield = false;
        string yieldingTo = "";

        foreach (var other in entries)
        {
            if (other.vehicle == owner || other.vehicle == null) continue;

            // Only yield to cars whose arcs geometrically conflict with ours
            if (!ArcsConflict(myEntry.chosenArc, other.chosenArc)) continue;

            ctx.Log("CROSSING_CONFLICT",
                other.vehicle.GetVehicleId(),
                registeredAt.ToString("F0"),
                $"me={TurnName(myEntry.turnType)}({myEntry.priority:F1}) " +
                $"other={TurnName(other.turnType)}({other.priority:F1})");

            if (other.priority > myEntry.priority)
            {
                mustYield  = true;
                yieldingTo = other.vehicle.GetVehicleId();
                break; // yield to first higher-priority conflict found
            }

            // Same priority — first registered goes first
            // Use 0.3f window (larger than float noise) to catch near-equal priorities
            if (Mathf.Abs(other.priority - myEntry.priority) < 0.3f)
            {
                // First registered wins
                bool otherFirst = other.registeredTime < myEntry.registeredTime - 0.01f;
                // Tiebreak by vehicle ID string comparison (deterministic, no randomness)
                bool otherWinsTie = !otherFirst &&
                    Mathf.Abs(other.registeredTime - myEntry.registeredTime) <= 0.01f &&
                    string.Compare(other.vehicle.GetVehicleId(),
                                   owner.GetVehicleId(),
                                   System.StringComparison.Ordinal) < 0;

                if (otherFirst || otherWinsTie)
                {
                    mustYield  = true;
                    yieldingTo = other.vehicle.GetVehicleId();
                    break;
                }
            }
        }

        if (mustYield)
        {
            ctx.Log("INTERSECTION_YIELD", yieldingTo,
                    registeredAt.ToString("F0"),
                    $"priority={myEntry.priority:F2}");
            ShouldYieldAtEntry = true;

            // Store which vehicle caused the yield so Tick can track it
            var entries2 = IntersectionRegistry.GetEntries(registeredAt);
            if (entries2 != null)
                foreach (var e in entries2)
                    if (e.vehicle != null && e.vehicle.GetVehicleId() == yieldingTo)
                    { YieldingToVehicle = e.vehicle; break; }
        }
        else
        {
            YieldingToVehicle = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Turn classification
    // ─────────────────────────────────────────────────────────────────────────

    int ClassifyTurn(TrafficPath arc)
    {
        if (arc == null || arc.waypoints.Count < 2) return 2; // assume straight

        Vector3 entryDir = (arc.waypoints[1].position -
                            arc.waypoints[0].position).normalized;
        Vector3 exitDir  = (arc.waypoints[arc.waypoints.Count - 1].position -
                            arc.waypoints[arc.waypoints.Count - 2].position).normalized;

        float turnDeg = Vector3.Angle(entryDir, exitDir);
        if (turnDeg < 25f) return 2; // straight

        // Cross product Y component: positive = right turn (left-hand traffic)
        float cross = Vector3.Dot(Vector3.Cross(entryDir, exitDir), Vector3.up);
        return cross >= 0f ? 3 : 1; // 3 = right, 1 = left
    }

    float ComputePriority(int turnType)
    {
        // Turn type is the dominant factor — no random jitter
        float typeScore = turnType; // 3, 2, or 1

        // Profile is a small tiebreaker within the same turn type
        float profileScore = ctx.DriverProfile == TrafficVehicle.DriverProfile.Aggressive ? 0.4f
                           : ctx.DriverProfile == TrafficVehicle.DriverProfile.Cautious    ? 0.1f
                           : 0.2f;

        return typeScore + profileScore;
    }

    string TurnName(int t) => t == 3 ? "right" : t == 2 ? "straight" : "left";

    // ─────────────────────────────────────────────────────────────────────────
    // Geometric conflict — waypoint proximity
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two arcs conflict if any waypoint from arc A is within 2.5m of any
    /// waypoint from arc B. More accurate than midpoint distance.
    /// At resolution=12, this is 12×12=144 checks — cheap.
    /// </summary>
    bool ArcsConflict(TrafficPath arcA, TrafficPath arcB)
    {
        if (arcA == null || arcB == null) return false;
        if (arcA.waypoints == null || arcB.waypoints == null) return false;

        const float ConflictDist = 2.5f; // half lane width

        foreach (var wpA in arcA.waypoints)
        {
            if (wpA == null) continue;
            foreach (var wpB in arcB.waypoints)
            {
                if (wpB == null) continue;
                if (Vector3.Distance(wpA.position, wpB.position) < ConflictDist)
                    return true;
            }
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Release helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the causing vehicle has passed through the intersection
    /// and is now physically ahead of us — lon > 0 in our local space.
    /// This is the cleanest release condition: the car we were waiting for
    /// has crossed and is now on the other side.
    /// </summary>
    bool IsVehicleAheadAndCleared(TrafficVehicle other)
    {
        if (other == null) return true;
        Vector3 toOther = other.transform.position - ctx.Transform.position;
        float   lon     = Vector3.Dot(toOther, ctx.Transform.forward);
        float   dist    = toOther.magnitude;
        // Ahead of us (lon > 5m) and not still in intersection zone (dist > 8m)
        return lon > 5f && dist > 8f;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cleanup
    // ─────────────────────────────────────────────────────────────────────────

    public void RefreshYieldDecision()
    {
        // Legacy hook — kept for compatibility. Self-release now handled in Tick.
    }

    void Deregister()
    {
        if (isRegistered)
        {
            IntersectionRegistry.Deregister(owner, registeredAt);
            isRegistered         = false;
            YieldingToVehicle    = null;
            ctx.CurrentTurnType  = 0; // clear when leaving intersection
        }
    }
}