using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// NeighbourMap — complete 360° awareness of vehicles around the ego vehicle.
//
// Before this: a vehicle only knew leaderVehicle (directly ahead, same lane).
// Everything else — beside, diagonal, behind, crossing — was invisible.
//
// After this: every vehicle has a populated NeighbourMap updated by
// PerceptionSystem every few ticks. All negotiation scenarios and the new
// intersection priority system read from this instead of running their own
// expensive Physics.OverlapSphere calls.
//
// OOP role: Plain data struct (NeighbourMap) + service class (PerceptionSystem).
//   PerceptionSystem is an IVehicleBehaviour so it slots into the existing
//   module tick loop in TrafficVehicle with no changes to the orchestrator.
// ─────────────────────────────────────────────────────────────────────────────

public class NeighbourMap
{
    // ── Same-lane ──────────────────────────────────────────────────────────
    // frontSameLane already exists as ctx.LeaderVehicle — not duplicated here.

    // ── Adjacent lane — forward half ──────────────────────────────────────
    // "Front" = ahead of ego, within a 60° forward cone, in the adjacent lane.
    public TrafficVehicle frontLeft;
    public TrafficVehicle frontRight;

    // ── Adjacent lane — beside (lateral zone) ─────────────────────────────
    // "Beside" = roughly level with ego (±45° from pure lateral), adjacent lane.
    // This is what was invisible before — the car you're about to side-swipe.
    public TrafficVehicle besideLeft;
    public TrafficVehicle besideRight;

    // ── Adjacent lane — rear half ─────────────────────────────────────────
    // "Rear" = behind ego, within a 60° rear cone, adjacent lane.
    // The car about to rear-end you during a lane change.
    public TrafficVehicle rearLeft;
    public TrafficVehicle rearRight;

    // ── Intersection crossing paths ────────────────────────────────────────
    // Vehicles whose committed turn arc physically crosses this vehicle's arc.
    // Populated by IntersectionPrioritySystem when a turn is committed.
    // Cleared when those vehicles exit the intersection conflict zone.
    public List<TrafficVehicle> crossingPaths;

    // ── Convenience checks ─────────────────────────────────────────────────
    public bool HasAnythingBesideLeft  => besideLeft  != null;
    public bool HasAnythingBesideRight => besideRight != null;
    public bool HasAnythingRearLeft    => rearLeft    != null;
    public bool HasAnythingRearRight   => rearRight   != null;
    public bool HasCrossingConflict    => crossingPaths != null && crossingPaths.Count > 0;

    public void Clear()
    {
        frontLeft  = null; frontRight  = null;
        besideLeft = null; besideRight = null;
        rearLeft   = null; rearRight   = null;
        crossingPaths?.Clear();
    }

    public void EnsureList()
    {
        if (crossingPaths == null) crossingPaths = new List<TrafficVehicle>(4);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PerceptionSystem — populates NeighbourMap every N ticks.
//
// Scan logic uses dot products against the ego vehicle's forward vector
// to classify each detected vehicle into one of the 6 spatial zones.
// Only looks at adjacent lanes (leftLane, rightLane) — not all vehicles
// in range, which keeps the scan cheap.
//
// Zone classification (all relative to ego forward):
//
//   dot > +0.4   AND in adjacent lane  →  frontLeft / frontRight
//   dot between -0.4 and +0.4          →  besideLeft / besideRight
//   dot < -0.4   AND in adjacent lane  →  rearLeft / rearRight
//
// crossingPaths is NOT populated here — that's IntersectionPrioritySystem's job.
// ─────────────────────────────────────────────────────────────────────────────

public class PerceptionSystem : IVehicleBehaviour
{
    private readonly VehicleContext ctx;
    private readonly TrafficVehicle owner;

    // Scan radius — how far to look in each adjacent lane
    private const float ScanRadius = 22f;

    // How often to refresh the map (every N FixedUpdate ticks)
    // 3 = every 60ms at 50Hz — fast enough for lane change decisions
    private const int ScanInterval = 3;
    private int ticksSinceScan = 0;

    // The map — written by this system, read by negotiation scenarios
    public NeighbourMap Map = new NeighbourMap();

    public PerceptionSystem(VehicleContext ctx, TrafficVehicle owner)
    {
        this.ctx   = ctx;
        this.owner = owner;
        Map.EnsureList();
    }

    public void OnSpawn()  { Map.Clear(); Map.EnsureList(); }
    public void OnDespawn(){ Map.Clear(); }

    public void Tick(float dt)
    {
        ticksSinceScan++;
        if (ticksSinceScan < ScanInterval) return;
        ticksSinceScan = 0;

        // Preserve crossingPaths — managed by IntersectionPrioritySystem
        var savedCrossing = Map.crossingPaths;
        Map.Clear();
        Map.crossingPaths = savedCrossing;
        Map.EnsureList();

        ScanAdjacentLane(ctx.LeftLane,  isLeft: true);
        ScanAdjacentLane(ctx.RightLane, isLeft: false);
    }

    void ScanAdjacentLane(TrafficLane lane, bool isLeft)
    {
        if (lane == null) return;

        var cols = Physics.OverlapSphere(ctx.Transform.position, ScanRadius);
        TrafficVehicle closestFront  = null;
        TrafficVehicle closestBeside = null;
        TrafficVehicle closestRear   = null;
        float distFront  = float.MaxValue;
        float distBeside = float.MaxValue;
        float distRear   = float.MaxValue;

        foreach (var col in cols)
        {
            var other = col.GetComponentInParent<TrafficVehicle>();
            if (other == null || other == owner) continue;
            if (other.currentLane != lane) continue;

            Vector3 toOther = other.transform.position - ctx.Transform.position;
            float dist = toOther.magnitude;
            if (dist < 0.3f) continue;

            float dot = Vector3.Dot(ctx.Transform.forward, toOther.normalized);

            if (dot > 0.4f)
            {
                if (dist < distFront) { distFront = dist; closestFront = other; }
            }
            else if (dot < -0.4f)
            {
                if (dist < distRear) { distRear = dist; closestRear = other; }
            }
            else
            {
                if (dist < distBeside) { distBeside = dist; closestBeside = other; }
            }
        }

        // ── Log collision risks ──────────────────────────────────────────
        // Beside: any vehicle level with us is a sideswipe risk during lane change
        if (closestBeside != null && distBeside < ctx.VehicleLength * 2f)
        {
            ctx.Log("COLLISION_RISK",
                closestBeside.GetVehicleId(),
                isLeft ? "beside_left" : "beside_right",
                $"dist={distBeside:F1}");
        }

        // Rear: vehicle behind us that is closing faster than our speed
        if (closestRear != null)
        {
            float closingSpeed = closestRear.CurrentSpeed - ctx.CurrentSpeed;
            if (closingSpeed > 2f && distRear < closingSpeed * ctx.TimeHeadway * 2f)
            {
                ctx.Log("COLLISION_RISK",
                    closestRear.GetVehicleId(),
                    isLeft ? "rear_left" : "rear_right",
                    $"dist={distRear:F1} closing={closingSpeed:F1}");
            }
        }

        if (isLeft)
        {
            Map.frontLeft  = closestFront;
            Map.besideLeft = closestBeside;
            Map.rearLeft   = closestRear;
        }
        else
        {
            Map.frontRight  = closestFront;
            Map.besideRight = closestBeside;
            Map.rearRight   = closestRear;
        }
    }
}