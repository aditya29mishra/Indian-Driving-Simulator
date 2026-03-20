using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// DestinationSystem — gives every vehicle an end goal and fixes dead-end despawns.
//
// Problem it solves:
//   Vehicles wandered randomly, hitting dead-end lanes (B-direction lanes with
//   no nextPaths/nextLanes) → FAIL_NO_FALLBACK → 15% of vehicles lost per session.
//
// How it works:
//   At spawn, VehicleSpawner assigns a DestNode (a TrafficNode with NodeType.RoadEnd).
//   This system:
//     1. Biases ChooseTurnPath toward the turn that points closest to DestNode.
//     2. Triggers clean despawn when the vehicle enters any lane on DestNode's road.
//
// Turn selection logic (destination-aware weighted random):
//   Current weight for each candidate turn = heading_weight * dest_bias
//   where dest_bias = lerp(0.5, 2.0, alignment)
//   alignment = how well this turn points toward the destination (0 = wrong way, 1 = perfect)
//   Still probabilistic — not GPS. An aggressive driver may take a worse turn to find a gap.
//
// OOP role: IVehicleBehaviour. Slot into TrafficVehicle module list.
//   DestNode is stored in VehicleContext. VehicleNavigator calls
//   DestinationSystem.ModifyWeights() during ChooseTurnPath to apply the bias.
// ─────────────────────────────────────────────────────────────────────────────

public class DestinationSystem : IVehicleBehaviour
{
    private readonly VehicleContext ctx;
    private readonly TrafficVehicle owner;

    public DestinationSystem(VehicleContext ctx, TrafficVehicle owner)
    {
        this.ctx   = ctx;
        this.owner = owner;
    }

    public void OnSpawn()
    {
        // Log destination assignment so CSV shows where each vehicle is headed
        if (ctx.DestNode != null)
            ctx.Log("DESTINATION_ASSIGNED",
                ctx.DestNode.name,
                ctx.DestNode.nodeType.ToString(),
                $"pos={ctx.DestNode.transform.position:F0}");
    }
    public void OnDespawn(){ }

    public void Tick(float dt)
    {
        // Check despawn condition every tick — cheap, just a reference comparison
        CheckDestinationArrival();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Arrival check
    // ─────────────────────────────────────────────────────────────────────────

    void CheckDestinationArrival()
    {
        if (ctx.CurrentLane == null) return;

        // Arc lanes have road == null — skip while in intersection
        if (ctx.CurrentLane.road == null) return;

        if (ctx.DestNode == null) return;

        TrafficRoad road = ctx.CurrentLane.road;

        // Check if this lane's road contains the destination End node
        if (road.startNode != ctx.DestNode && road.endNode != ctx.DestNode) return;

        // Don't despawn the instant we enter the road — wait until the car
        // is physically close to the End node (within 8m).
        // This prevents immediate despawn right after exiting an arc.
        Vector3 destPos  = ctx.DestNode.transform.position;
        Vector3 carPos   = ctx.Transform.position;
        float   distToDest = Vector3.Distance(carPos, destPos);

        if (distToDest > 8f) return;

        ArriveClean("ARRIVED");
    }

    bool IsTerminalLane(TrafficLane lane)
    {
        if (lane == null) return false;
        if (lane.nextLanes != null && lane.nextLanes.Count > 0) return false;
        if (lane.nextPaths != null)
        {
            foreach (var lp in lane.nextPaths)
                if (lp != null && lp.path != null && lp.targetLane != null) return false;
        }
        bool endsAtTrueTip = (lane.road.startNode != null &&
                              lane.road.startNode.connectedRoads.Count == 1) ||
                             (lane.road.endNode   != null &&
                              lane.road.endNode.connectedRoads.Count   == 1);
        return endsAtTrueTip;
    }

    void ArriveClean(string eventName)
    {
        ctx.Recorder?.LogEvent(ctx.VehicleId, eventName,
            ctx.CurrentLane.name,
            ctx.DestNode != null ? ctx.DestNode.name : "terminal",
            "", -1f, ctx.GetRouteTrace());
        ctx.IsWaitingForLane = true;
        Object.Destroy(owner.gameObject, 0.3f);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Turn weight modification — called by VehicleNavigator.ChooseTurnPath
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Modifies the raw heading-based weight array to bias toward the destination.
    /// Called with the candidate paths and their current weights. Modifies weights in-place.
    /// If no destination is set, weights are unchanged (pure heading-based random).
    /// </summary>
    public void ModifyTurnWeights(List<TrafficLane.LanePath> paths, float[] weights)
    {
        if (ctx.DestNode == null) return;
        if (paths == null || weights == null || paths.Count != weights.Length) return;

        Vector3 destPos = ctx.DestNode.transform.position;
        Vector3 myPos   = ctx.Transform.position;
        Vector3 toDestDir = (destPos - myPos).normalized;

        for (int i = 0; i < paths.Count; i++)
        {
            var lp = paths[i];
            if (lp == null || lp.targetLane == null || weights[i] <= 0f) continue;

            // How well does this turn's exit direction point toward the destination?
            Vector3 turnExitDir = lp.targetLane.transform.forward;
            float alignment = Mathf.Clamp01((Vector3.Dot(toDestDir, turnExitDir) + 1f) * 0.5f);
            // alignment: 0 = pointing directly away, 0.5 = perpendicular, 1.0 = pointing right at dest

            // Strong bias — especially important in single-intersection maps where every
            // road arm is a dead end. Wrong turn = guaranteed FAIL 20s later.
            // 0.1× for wrong direction, up to 5× for correct direction.
            // Profile modulates how strictly the vehicle respects the destination:
            // Cautious = very obedient, Aggressive = still biased but some randomness.
            float maxBias = ctx.DriverProfile == TrafficVehicle.DriverProfile.Cautious   ? 6.0f
                          : ctx.DriverProfile == TrafficVehicle.DriverProfile.Aggressive ? 3.0f
                          : 4.5f;

            float biasMult = Mathf.Lerp(0.1f, maxBias, alignment);
            weights[i] *= biasMult;
        }
    }
    
}   