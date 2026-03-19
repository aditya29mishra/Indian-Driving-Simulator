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

    public void OnSpawn()  { }
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
        if (ctx.DestNode == null || ctx.CurrentLane == null) return;
        if (ctx.CurrentLane.road == null) return;

        // Arrived if the current lane's road is connected to the destination node
        // (i.e. this road has DestNode as its startNode or endNode)
        TrafficRoad road = ctx.CurrentLane.road;
        if (road.startNode == ctx.DestNode || road.endNode == ctx.DestNode)
        {
            // Clean arrival — log and despawn
            ctx.Recorder?.LogEvent(ctx.VehicleId, "ARRIVED",
                ctx.CurrentLane.name, ctx.DestNode.name, "", -1f, ctx.GetRouteTrace());
            ctx.IsWaitingForLane = true;
            Object.Destroy(owner.gameObject, 0.3f);
        }
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

            // Bias multiplier: 0.5× for wrong direction, up to 2.5× for correct direction
            // Profile affects how strongly the vehicle respects the destination:
            // Cautious follows destination more faithfully
            // Aggressive cares less — will take a faster gap even in the wrong direction
            float maxBias = ctx.DriverProfile == TrafficVehicle.DriverProfile.Cautious   ? 3.0f
                          : ctx.DriverProfile == TrafficVehicle.DriverProfile.Aggressive ? 1.5f
                          : 2.0f;

            float biasMult = Mathf.Lerp(0.4f, maxBias, alignment);
            weights[i] *= biasMult;
        }
    }
}
