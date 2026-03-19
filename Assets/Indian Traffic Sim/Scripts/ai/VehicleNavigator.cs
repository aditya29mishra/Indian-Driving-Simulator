using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// VehicleNavigator — path advancement, turn selection, lane assignment, despawn.
//
// Turn selection updated to use TrafficGraph for informed reactive routing.
// A vehicle at an intersection asks the graph: "what is the next road node
// I should head toward on my way to my destination?" — exactly like a driver
// using their mental map of the city. No pre-planned full route, no greedy
// straight-line heuristic.
//
// OOP role: Class + Encapsulation.
//   All routing decisions live here. TrafficVehicle calls Tick() and the
//   navigator handles end-of-path events internally.
// ─────────────────────────────────────────────────────────────────────────────

public class VehicleNavigator : IVehicleBehaviour
{
    private readonly VehicleContext ctx;
    private readonly VehicleIDM    idm;
    private readonly TrafficVehicle owner;

    public VehicleNavigator(VehicleContext ctx, VehicleIDM idm, TrafficVehicle owner)
    {
        this.ctx   = ctx;
        this.idm   = idm;
        this.owner = owner;
    }

    public void OnSpawn() { }
    public void OnDespawn() { }

    public void Tick(float dt)
    {
        ctx.InsideIntersection = (ctx.CurrentPath != null && ctx.CurrentLane != null &&
                                   ctx.CurrentPath != ctx.CurrentLane.path);

        if (ctx.CurrentPath == null || ctx.CurrentPath.waypoints.Count == 0) return;

        Vector3 pathEnd   = ctx.CurrentPath.waypoints[ctx.CurrentPath.waypoints.Count - 1].position;
        float distToEnd   = Vector3.Distance(ctx.Transform.position, pathEnd);
        float remaining   = ctx.CurrentPath.TotalLength - ctx.DistanceTravelled;
        bool  nearEnd     = distToEnd < 6f || remaining < 2f;

        if (nearEnd) AdvanceToNextPath();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Lane assignment
    // ─────────────────────────────────────────────────────────────────────

    public void AssignLane(TrafficLane lane, bool fromSpawn = false)
    {
        ctx.CurrentLane       = lane;
        ctx.CurrentPath       = lane.path;
        ctx.DistanceTravelled = fromSpawn ? 0f
            : (ctx.CurrentPath != null
               ? ctx.CurrentPath.GetClosestDistance(ctx.Transform.position)
               : 0f);
        ctx.IsWaitingForLane  = false;

        if (lane.road != null && lane.road.speedLimit > 0f)
        {
            float limitMs  = lane.road.speedLimit / 3.6f;
            float variance = ctx.DriverProfile == TrafficVehicle.DriverProfile.Aggressive
                ? Random.Range(1.0f, 1.1f)
                : ctx.DriverProfile == TrafficVehicle.DriverProfile.Normal
                ? Random.Range(0.9f, 1.0f)
                : Random.Range(0.75f, 0.9f);
            ctx.DesiredSpeedMs = Mathf.Min(ctx.MaxSpeedMs, limitMs * variance);
        }

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

        // 2. Lane has intersection turn paths → pick one via graph routing
        if (ctx.CurrentLane.nextPaths != null && ctx.CurrentLane.nextPaths.Count > 0)
        {
            var lp = ChooseTurnPath();

            if (lp == null || lp.path == null || lp.targetLane == null)
            {
                ctx.Log("FAIL_TURN_SELECTION", ctx.CurrentLane.name);

                if (ctx.CurrentLane.nextLanes != null && ctx.CurrentLane.nextLanes.Count > 0)
                {
                    var fallback = ctx.CurrentLane.nextLanes[Random.Range(0, ctx.CurrentLane.nextLanes.Count)];
                    ctx.Log("FALLBACK_LANE", fallback.name);
                    AssignLane(fallback);
                    return;
                }

                ctx.Log("ARRIVED_TERMINAL", ctx.CurrentLane.name,
                    ctx.DestNode != null ? ctx.DestNode.name : "terminal");
                Despawn();
                return;
            }

            ctx.Log("PATH_SWITCH", lp.path.name, GetIntersectionId());

            if (ctx.IntersectionPriority != null && lp.path != null)
            {
                Vector3 intersectionCentre =
                    ctx.CurrentLane.path != null && ctx.CurrentLane.path.waypoints.Count > 0
                    ? ctx.CurrentLane.path.waypoints[ctx.CurrentLane.path.waypoints.Count - 1].position
                    : ctx.Transform.position;
                ctx.IntersectionPriority.OnTurnCommitted(lp.path, intersectionCentre);
            }

            ctx.RouteHistory.Add("P:" + lp.path.name);
            ctx.CurrentPath       = lp.path;
            ctx.CurrentLane       = lp.targetLane;
            ctx.DistanceTravelled = ctx.CurrentPath.GetClosestDistance(ctx.Transform.position);
            ctx.LaneManager?.RefreshVehicle(owner);
            return;
        }

        // 3. Straight connections via nextLanes
        if (ctx.CurrentLane.nextLanes != null && ctx.CurrentLane.nextLanes.Count > 0)
        {
            var next = ctx.CurrentLane.nextLanes[Random.Range(0, ctx.CurrentLane.nextLanes.Count)];
            ctx.Log("LANE_CONTINUE", next.name);
            ctx.RouteHistory.Add("FALLBACK:" + next.name);
            AssignLane(next);
            return;
        }

        // 4. Terminal lane — no exits
        ctx.Log("ARRIVED_TERMINAL", ctx.CurrentLane?.name ?? "none",
            ctx.DestNode != null ? ctx.DestNode.name : "terminal");
        Despawn();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Turn selection — informed reactive routing via TrafficGraph
    // ─────────────────────────────────────────────────────────────────────
    //
    // The vehicle asks TrafficGraph: given my current road's End node and
    // my destination node, what is the next node to head toward?
    // Then picks the turn arc whose target road contains that node.
    //
    // Fallback chain (if graph unavailable or returns no path):
    //   1. Straight-ahead preference (best dot product)
    //   2. First valid path (last resort)
    //
    // This matches how a real driver thinks — informed decision at each
    // junction, no pre-planned full route stored on the vehicle.

    TrafficLane.LanePath ChooseTurnPath()
    {
        var paths = ctx.CurrentLane.nextPaths;
        if (paths == null || paths.Count == 0) return null;

        // Collect valid candidates
        var valid = new System.Collections.Generic.List<TrafficLane.LanePath>();
        foreach (var lp in paths)
            if (lp != null && lp.path != null && lp.targetLane != null)
                valid.Add(lp);

        if (valid.Count == 0) return null;
        if (valid.Count == 1)
        {
            ctx.Recorder?.LogEvent(ctx.VehicleId, "TURN_DECISION",
                valid[0].path.name, GetIntersectionId(), "only_option", 1f, ctx.GetRouteTrace());
            return valid[0];
        }

        // ── Graph-based routing ───────────────────────────────────────────
        if (ctx.DestNode != null && TrafficGraph.Instance != null)
        {
            // Find the End node of the current road — that's "where we are" in graph terms
            TrafficRoadNode currentEndNode = GetCurrentRoadEndNode();

            if (currentEndNode != null)
            {
                // Ask the graph: what is the next node to head toward?
                TrafficRoadNode nextNode = TrafficGraph.Instance.GetNextNode(
                    currentEndNode, ctx.DestNode);

                if (nextNode != null)
                {
                    // Find the turn arc whose target lane's road contains nextNode
                    TrafficLane.LanePath best = FindPathTowardNode(valid, nextNode);

                    if (best != null)
                    {
                        ctx.Recorder?.LogEvent(ctx.VehicleId, "TURN_DECISION",
                            best.path.name, GetIntersectionId(),
                            $"graph_route→{nextNode.name}", 1f, ctx.GetRouteTrace());
                        return best;
                    }
                }
            }
        }

        // ── Fallback: straight-ahead preference ───────────────────────────
        TrafficLane.LanePath straightest = null;
        float bestDot = -1f;

        foreach (var lp in valid)
        {
            float dot = Vector3.Dot(ctx.Transform.forward, lp.targetLane.transform.forward);
            if (dot > bestDot) { bestDot = dot; straightest = lp; }
        }

        if (straightest != null)
            ctx.Recorder?.LogEvent(ctx.VehicleId, "TURN_DECISION",
                straightest.path.name, GetIntersectionId(),
                "straight_fallback", 1f, ctx.GetRouteTrace());

        return straightest ?? valid[0];
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the turn arc in the candidate list whose target road contains
    /// the given node (as startNode or endNode).
    /// </summary>
    TrafficLane.LanePath FindPathTowardNode(
        System.Collections.Generic.List<TrafficLane.LanePath> candidates,
        TrafficRoadNode targetNode)
    {
        foreach (var lp in candidates)
        {
            TrafficRoad road = lp.targetLane?.road;
            if (road == null) continue;
            if (road.startNode == targetNode || road.endNode == targetNode)
                return lp;
        }
        return null;
    }

    /// <summary>
    /// Returns the End-type TrafficRoadNode of the vehicle's current road.
    /// This is "where we are" in graph terms — the outer tip of the road arm.
    /// Works regardless of whether it is startNode or endNode.
    /// </summary>
    TrafficRoadNode GetCurrentRoadEndNode()
    {
        TrafficRoad road = ctx.CurrentLane?.road;
        if (road == null) return null;

        if (road.startNode != null &&
            road.startNode.nodeType == TrafficRoadNode.NodeType.End)
            return road.startNode;
        if (road.endNode != null &&
            road.endNode.nodeType == TrafficRoadNode.NodeType.End)
            return road.endNode;

        return null;
    }

    void Despawn()
    {
        ctx.IsWaitingForLane = true;
        ctx.Log("DESPAWN", GetPathId(), GetIntersectionId());
        Object.Destroy(owner.gameObject, 0.3f);
    }

    string GetPathId()          => ctx.CurrentPath ? ctx.CurrentPath.name : "none";
    string GetIntersectionId()  => ctx.CurrentLane && ctx.CurrentLane.road != null
                                   ? ctx.CurrentLane.road.name : "none";
}