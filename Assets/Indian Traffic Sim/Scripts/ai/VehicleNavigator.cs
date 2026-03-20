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
        // Arc lanes have road == null — that's how we know we're in an intersection
        ctx.InsideIntersection = ctx.CurrentLane != null && ctx.CurrentLane.road == null;

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
        // Always start at 0 — avoids snapping issues when transitioning
        // between road path → arc lane → exit road lane.
        // GetClosestDistance can return a high value if the car isn't yet
        // physically at waypoints[0], causing immediate re-advance and despawn.
        ctx.DistanceTravelled = 0f;
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

        // All connections are now via nextLanes — road lanes, arc lanes, joint lanes.
        // Arc lanes are created by IntersectionConnector and wired:
        //   road lane → arc lane → exit road lane
        // No isTurnPath check needed — just follow nextLanes always.

        if (ctx.CurrentLane.nextLanes != null && ctx.CurrentLane.nextLanes.Count > 0)
        {
            TrafficLane next = ChooseNextLane();

            if (next == null)
            {
                ctx.Log("FAIL_LANE_SELECTION", ctx.CurrentLane.name);
                Despawn();
                return;
            }

            // Notify intersection priority system if entering an arc lane
            if (ctx.IntersectionPriority != null && next.road == null)
            {
                Vector3 intersectionCentre =
                    ctx.CurrentLane.path != null && ctx.CurrentLane.path.waypoints.Count > 0
                    ? ctx.CurrentLane.path.waypoints[ctx.CurrentLane.path.waypoints.Count - 1].position
                    : ctx.Transform.position;
                ctx.IntersectionPriority.OnTurnCommitted(next.path, intersectionCentre);
            }

            ctx.Log("LANE_ADVANCE", next.name);
            AssignLane(next);
            return;
        }

        // No exits — terminal lane
        ctx.Log("ARRIVED_TERMINAL", ctx.CurrentLane?.name ?? "none",
            ctx.DestNode != null ? ctx.DestNode.name : "terminal");
        Despawn();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Lane selection — informed reactive routing via TrafficGraph
    // ─────────────────────────────────────────────────────────────────────

    TrafficLane ChooseNextLane()
    {
        var candidates = ctx.CurrentLane.nextLanes;
        if (candidates == null || candidates.Count == 0) return null;

        // Filter valid
        var valid = new System.Collections.Generic.List<TrafficLane>();
        foreach (var l in candidates)
            if (l != null && l.path != null) valid.Add(l);

        if (valid.Count == 0) return null;
        if (valid.Count == 1) return valid[0];

        // ── Graph-based routing ───────────────────────────────────────────
        // Only applied when choosing between arc lanes (at an intersection).
        // Arc lanes connect to exit road lanes — find the one whose exit road
        // contains the next node from Dijkstra.
        if (ctx.DestNode != null && TrafficGraph.Instance != null)
        {
            TrafficRoadNode currentEndNode = GetCurrentRoadEndNode();

            if (currentEndNode != null)
            {
                TrafficRoadNode nextNode = TrafficGraph.Instance.GetNextNode(
                    currentEndNode, ctx.DestNode);

                if (nextNode != null)
                {
                    // Each arc lane has exactly one nextLane — the exit road lane.
                    // Find the arc lane whose exit road lane belongs to the target road.
                    TrafficLane best = FindLaneTowardNode(valid, nextNode);
                    if (best != null)
                    {
                        ctx.Recorder?.LogEvent(ctx.VehicleId, "LANE_DECISION",
                            best.name, GetIntersectionId(),
                            $"graph_route→{nextNode.name}", 1f, ctx.GetRouteTrace());
                        return best;
                    }
                }
            }
        }

        // ── Fallback: straight-ahead preference ───────────────────────────
        TrafficLane straightest = null;
        float bestDot = -1f;
        foreach (var l in valid)
        {
            float dot = Vector3.Dot(ctx.Transform.forward, l.transform.forward);
            if (dot > bestDot) { bestDot = dot; straightest = l; }
        }

        return straightest ?? valid[0];
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the arc lane (in nextLanes) that leads toward the target node.
    /// Arc lanes have nextLanes[0] = exit road lane. Check that exit road lane's
    /// road for the target node.
    /// </summary>
    TrafficLane FindLaneTowardNode(
        System.Collections.Generic.List<TrafficLane> candidates,
        TrafficRoadNode targetNode)
    {
        foreach (var arcLane in candidates)
        {
            // Arc lane → exit road lane
            if (arcLane.nextLanes == null || arcLane.nextLanes.Count == 0) continue;
            TrafficRoad exitRoad = arcLane.nextLanes[0]?.road;
            if (exitRoad == null) continue;
            if (exitRoad.startNode == targetNode || exitRoad.endNode == targetNode)
                return arcLane;
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
        // If on arc lane (road == null), look at the arc's exit road via nextLanes
        TrafficRoad road = ctx.CurrentLane?.road;
        if (road == null)
        {
            // Arc lane — get exit road from nextLanes[0]
            var exitLane = ctx.CurrentLane?.nextLanes?.Count > 0
                ? ctx.CurrentLane.nextLanes[0] : null;
            road = exitLane?.road;
        }
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