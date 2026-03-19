using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// VehicleNavigator — path advancement, turn selection, lane assignment, despawn.
//
// OOP role: Class + Encapsulation.
//   All routing decisions live here. TrafficVehicle calls Tick() and the
//   navigator handles end-of-path events internally. AssignLane is public
//   because VehicleSpawner and VehicleNegotiator call it on spawn and lane change.
// ─────────────────────────────────────────────────────────────────────────────

public class VehicleNavigator : IVehicleBehaviour
{
    private readonly VehicleContext ctx;
    private readonly VehicleIDM idm; // needed for MustStopForSignal in path-end check

    // Reference back to the MonoBehaviour for Destroy() and laneManager calls
    private readonly TrafficVehicle owner;

    public VehicleNavigator(VehicleContext ctx, VehicleIDM idm, TrafficVehicle owner)
    {
        this.ctx = ctx;
        this.idm = idm;
        this.owner = owner;
    }

    public void OnSpawn() { }
    public void OnDespawn() { }

    public void Tick(float dt)
    {
        ctx.InsideIntersection = (ctx.CurrentPath != null && ctx.CurrentLane != null &&
                                   ctx.CurrentPath != ctx.CurrentLane.path);

        if (ctx.CurrentPath == null || ctx.CurrentPath.waypoints.Count == 0) return;

        Vector3 pathEnd = ctx.CurrentPath.waypoints[ctx.CurrentPath.waypoints.Count - 1].position;
        float distToEnd = Vector3.Distance(ctx.Transform.position, pathEnd);
        float remaining = ctx.CurrentPath.TotalLength - ctx.DistanceTravelled;
        bool isTurnPath = ctx.InsideIntersection;

        // distToEnd < 6f covers the spline/waypoint position gap.
        // remaining < 2f catches the lerp-resistant distance cases.
        bool nearEnd = distToEnd < 6f || remaining < 2f;

        if (nearEnd) AdvanceToNextPath();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Lane assignment — called by Spawner, Negotiator, and internally
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Assigns the vehicle to a new lane. fromSpawn=true resets distance to 0;
    /// otherwise snaps to closest spline point so lane changes and turn completions
    /// don't reset progress. Also syncs prevSignalState to avoid false green-edge
    /// restart on the first frame after a red-light spawn.
    /// </summary>
    public void AssignLane(TrafficLane lane, bool fromSpawn = false)
    {
        ctx.CurrentLane = lane;
        ctx.CurrentPath = lane.path;
        ctx.DistanceTravelled = fromSpawn ? 0f
            : (ctx.CurrentPath != null ? ctx.CurrentPath.GetClosestDistance(ctx.Transform.position) : 0f);
        ctx.IsWaitingForLane = false;

        // Adjust desired speed to the new lane's road speed limit with profile variance
        if (lane.road != null && lane.road.speedLimit > 0f)
        {
            float limitMs = lane.road.speedLimit / 3.6f;
            float variance = ctx.DriverProfile == TrafficVehicle.DriverProfile.Aggressive
                ? Random.Range(1.0f, 1.1f)
                : ctx.DriverProfile == TrafficVehicle.DriverProfile.Normal
                ? Random.Range(0.9f, 1.0f)
                : Random.Range(0.75f, 0.9f);
            ctx.DesiredSpeedMs = Mathf.Min(ctx.MaxSpeedMs, limitMs * variance);
        }

        // Sync prevSignalState so the green-edge detector doesn't fire a false restart.
        // currentSignal must be set BEFORE AssignLane is called.
        ctx.PrevSignalState = ctx.CurrentSignal != null
            ? ctx.CurrentSignal.GetStateForLane(lane)
            : TrafficSignal.SignalState.Green;

        ctx.RouteHistory.Add("L:" + lane.name);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Path advancement
    // ─────────────────────────────────────────────────────────────────────

    void AdvanceToNextPath()
    {
        if (ctx.CurrentLane == null) { ctx.Log("FAIL_NO_LANE"); Despawn(); return; }

        bool isTurnPath = ctx.CurrentPath != ctx.CurrentLane.path;

        // 1. Finishing a turn arc → snap back to the target lane's main path
        if (isTurnPath)
        {
            ctx.Log("TURN_COMPLETE", ctx.CurrentLane.name);
            AssignLane(ctx.CurrentLane);
            return;
        }

        // 2. Lane has intersection turn paths → pick one
        if (ctx.CurrentLane.nextPaths != null && ctx.CurrentLane.nextPaths.Count > 0)
        {
            var lp = ChooseTurnPath();

            if (lp == null || lp.path == null || lp.targetLane == null)
            {
                ctx.Log("FAIL_TURN_SELECTION", ctx.CurrentLane.name);
                Debug.LogWarning($"[Navigator] {ctx.VehicleId} no valid turn on {ctx.CurrentLane.name}");

                if (ctx.CurrentLane.nextLanes != null && ctx.CurrentLane.nextLanes.Count > 0)
                {
                    var fallback = ctx.CurrentLane.nextLanes[Random.Range(0, ctx.CurrentLane.nextLanes.Count)];
                    ctx.Log("FALLBACK_LANE", fallback.name);
                    AssignLane(fallback);
                    return;
                }

                ctx.Log("FAIL_NO_FALLBACK");
                Despawn(); // bare return here was the original dead-end freeze bug
                return;
            }

            ctx.Log("PATH_SWITCH", lp.path.name, GetIntersectionId());
            // Notify IntersectionPrioritySystem that a turn arc was committed
            // so it can register at the intersection and compute priority
            if (ctx.IntersectionPriority != null && lp.path != null)
            {
                // Use the midpoint of the current lane's path end as intersection centre
                Vector3 intersectionCentre = ctx.CurrentLane != null && ctx.CurrentLane.path != null &&
                                              ctx.CurrentLane.path.waypoints.Count > 0
                    ? ctx.CurrentLane.path.waypoints[ctx.CurrentLane.path.waypoints.Count - 1].position
                    : ctx.Transform.position;
                ctx.IntersectionPriority.OnTurnCommitted(lp.path, intersectionCentre);
            }
            ctx.RouteHistory.Add("P:" + lp.path.name);
            ctx.CurrentPath = lp.path;
            ctx.CurrentLane = lp.targetLane;
            ctx.DistanceTravelled = ctx.CurrentPath.GetClosestDistance(ctx.Transform.position);
            ctx.LaneManager?.RefreshVehicle(owner);
            return;
        }

        // 3. Straight connections (nextLanes) — used when no intersection connector exists
        if (ctx.CurrentLane.nextLanes != null && ctx.CurrentLane.nextLanes.Count > 0)
        {
            var next = ctx.CurrentLane.nextLanes[Random.Range(0, ctx.CurrentLane.nextLanes.Count)];
            ctx.Log("LANE_CONTINUE", next.name);
            ctx.RouteHistory.Add("FALLBACK:" + next.name);
            AssignLane(next);
            return;
        }

        // 4. Dead end — despawn
        ctx.Log("DEAD_END");
        Despawn();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Turn path selection — weighted by heading angle (Indian traffic preference)
    // ─────────────────────────────────────────────────────────────────────

    TrafficLane.LanePath ChooseTurnPath()
    {
        var paths = ctx.CurrentLane.nextPaths;
        if (paths == null || paths.Count == 0) return null;

        float[] weights = new float[paths.Count];
        float total = 0f;

        for (int i = 0; i < paths.Count; i++)
        {
            var lp = paths[i];
            if (lp == null || lp.path == null || lp.targetLane == null) continue;

            Vector3 fwd = ctx.Transform.forward;
            Vector3 tFwd = lp.targetLane.transform.forward;
            float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot(fwd, tFwd), -1f, 1f)) * Mathf.Rad2Deg;

            // Indian traffic preference: straight > right > left > U-turn
            weights[i] = ang < 30f ? 0.50f
                       : ang > 150f ? 0.05f
                       : Vector3.Dot(Vector3.Cross(fwd, tFwd), Vector3.up) >= 0f ? 0.30f : 0.15f;

            total += weights[i];
        }

        if (total <= 0f)
        {
            for (int i = 0; i < paths.Count; i++)
            {
                var lp = paths[i];
                if (lp != null && lp.path != null && lp.targetLane != null) return lp;
            }
            return null;
        }

        float pick = Random.value * total, acc = 0f;
        for (int i = 0; i < paths.Count; i++)
        {
            acc += weights[i];
            if (pick <= acc)
            {
                var chosen = paths[i];
                if (chosen == null || chosen.path == null || chosen.targetLane == null) continue;
                ctx.Recorder?.LogEvent(ctx.VehicleId, "TURN_DECISION", chosen.path.name,
                    GetIntersectionId(), $"choice_{i}", weights[i] / total, ctx.GetRouteTrace());
                return chosen;
            }
        }

        // Float rounding fallback — return last valid entry
        for (int i = paths.Count - 1; i >= 0; i--)
        {
            var lp = paths[i];
            if (lp != null && lp.path != null && lp.targetLane != null) return lp;
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────

    void Despawn()
    {
        ctx.IsWaitingForLane = true;
        ctx.Log("DESPAWN", GetPathId(), GetIntersectionId());
        Object.Destroy(owner.gameObject, 0.3f);
    }

    string GetPathId() => ctx.CurrentPath ? ctx.CurrentPath.name : "none";
    string GetIntersectionId() => ctx.CurrentLane && ctx.CurrentLane.road != null
                                  ? ctx.CurrentLane.road.name : "none";
}