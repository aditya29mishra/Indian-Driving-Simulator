using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// VehicleNegotiator — runs all negotiation scenarios each tick.
//
// OOP role: Composition + Polymorphism.
//   Holds a List<NegotiationScenario>. Each tick it calls Evaluate() on all,
//   then Apply() on those that are active. Order matters: evaluate all first
//   so no scenario's Apply corrupts another scenario's Evaluate in the same tick.
//
//   Adding a new scenario: implement NegotiationScenario, add an instance to
//   the list in the constructor. VehicleNegotiator itself never changes.
// ─────────────────────────────────────────────────────────────────────────────

public class VehicleNegotiator : IVehicleBehaviour
{
    private readonly VehicleContext ctx;
    private readonly List<NegotiationScenario> scenarios;

    // Expose QueueRipple so LaneManager can read IsFrontOfQueue
    public QueueRippleScenario QueueRipple { get; private set; }

    public VehicleNegotiator(VehicleContext ctx, VehicleIDM idm, VehicleNavigator navigator, TrafficVehicle owner)
    {
        this.ctx = ctx;

        QueueRipple = new QueueRippleScenario(ctx, owner);

        scenarios = new List<NegotiationScenario>
        {
            new LaneChangeScenario(ctx, owner, idm, navigator),
            new CrossPathScenario(ctx, owner),
            new MergeOntoRoadScenario(ctx, owner),
            QueueRipple,
            new NarrowGapScenario(ctx, owner),
            new AggressiveAssertionScenario(ctx, owner),
        };
    }

    public void OnSpawn()
    {
        foreach (var s in scenarios) s.Reset();
    }

    public void OnDespawn()
    {
        // Clear any yield references we may have set on other vehicles
        ctx.MergingVehicle  = null;
        ctx.MergeYieldTimer = 0f;
    }

    public void Tick(float dt)
    {
        // Tick lane change cooldown
        if (ctx.LaneChangeCooldown > 0f)
            ctx.LaneChangeCooldown -= dt;

        ctx.LaneChangeCheckTimer += dt;

        float lcInterval = ctx.DriverProfile == TrafficVehicle.DriverProfile.Aggressive ? 1.5f
                         : ctx.DriverProfile == TrafficVehicle.DriverProfile.Cautious   ? 6f : 3f;

        bool timeToCheck = ctx.LaneChangeCheckTimer >= lcInterval;
        if (timeToCheck) ctx.LaneChangeCheckTimer = 0f;

        // Phase 1: evaluate all scenarios (read-only world scan)
        foreach (var s in scenarios)
        {
            // Only evaluate lane change on its timer interval; others run every tick
            if (s is LaneChangeScenario && !timeToCheck) continue;
            s.Evaluate();
        }

        // Phase 2: apply active scenarios (mutate ctx, signal other vehicles)
        foreach (var s in scenarios)
        {
            if (s.IsActive) s.Apply(dt);
        }
    }
}