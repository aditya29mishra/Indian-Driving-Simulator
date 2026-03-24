using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// VehicleSteering — pure-pursuit steering and spline distance tracking.
//
// OOP role: Class + Encapsulation.
//   Only output: SteerOutput (-1..1) and ctx.DistanceTravelled.
//
// VehicleProfile integration:
//   Lateral offset now uses Profile.LateralAgility and Profile.MaxLateralOffset
//   instead of the single ctx.LateralVariance float.
//
//   Gap-threading vehicles (bikes, rickshaws) get a dynamic lateral component
//   that steers toward whichever side has more open space. This is what produces
//   the weaving/threading look without any special state machine — the bike
//   just steers toward the gap it sees in its VehicleMap.
//
//   Non-threading vehicles (cars, buses) keep the original static lean behaviour.
// ─────────────────────────────────────────────────────────────────────────────

public class VehicleSteering : IVehicleBehaviour
{
    private readonly VehicleContext ctx;

    // Pure-pursuit base look-ahead distance in metres.
    private const float LookAheadBase = 6f;

    // Output read by TrafficVehicle for the CarMove.Move() call
    public float SteerOutput { get; private set; }

    public VehicleSteering(VehicleContext ctx) { this.ctx = ctx; }

    public void OnSpawn()   { SteerOutput = 0f; }
    public void OnDespawn() { }

    public void Tick(float dt)
    {
        UpdateDistanceTravelled();
        SteerOutput = ComputeSteering();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Distance tracking — unchanged
    // ─────────────────────────────────────────────────────────────────────

    void UpdateDistanceTravelled()
    {
        if (ctx.CurrentPath == null) return;
        float closest = ctx.CurrentPath.GetClosestDistance(ctx.Transform.position);

        if (closest < ctx.DistanceTravelled - 5f)
            ctx.DistanceTravelled = closest;
        else
            ctx.DistanceTravelled = Mathf.Max(ctx.DistanceTravelled, closest);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Pure-pursuit
    // ─────────────────────────────────────────────────────────────────────

    float ComputeSteering()
    {
        if (ctx.CurrentPath == null) return 0f;

        float lookAhead = LookAheadBase + Mathf.Clamp(ctx.CurrentSpeed, 0f, 10f) * 0.3f;

        Vector3 targetPoint = ctx.CurrentPath.GetPointAtDistance(
            Mathf.Min(ctx.DistanceTravelled + lookAhead, ctx.CurrentPath.TotalLength));

        // Apply lateral positioning from profile
        if (!ctx.InsideIntersection)
            ApplyLateralOffset(ref targetPoint);

        Vector3 localTarget = ctx.Transform.InverseTransformPoint(targetPoint);
        float lateralError  = localTarget.x;
        float forwardDist   = Mathf.Max(localTarget.z, 1f);
        float steerAngle    = Mathf.Atan2(lateralError, forwardDist) * Mathf.Rad2Deg;

        // Steer angle normalisation: divide by maxSteerAngle from profile if available,
        // otherwise fall back to the original 25-degree constant.
        float maxAngle = ctx.Profile?.Agility.maxSteerAngle ?? 25f;
        return Mathf.Clamp(steerAngle / maxAngle, -1f, 1f);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Lateral offset — profile-aware
    // ─────────────────────────────────────────────────────────────────────

    void ApplyLateralOffset(ref Vector3 targetPoint)
    {
        float agility   = ctx.LateralAgility;
        float maxOffset = ctx.MaxLateralOffset;

        if (agility <= 0f || maxOffset <= 0f) return;

        // Static lean: stable per-vehicle offset for its entire lifetime.
        // VehicleId hash gives a deterministic value — different each spawn
        // because VehicleId includes a random suffix, but stable within one life.
        float stableOffset = (ctx.VehicleId.GetHashCode() % 1000 / 1000f - 0.5f) * 2f;

        float dynamicOffset = 0f;

        // Dynamic gap-seek: bikes and rickshaws actively slide toward open space.
        // Reads from VehicleMap which WorldPerception updates every tick.
        // No raycasts, no physics — pure map read.
        if (ctx.CanGapThread)
            dynamicOffset = ComputeGapSeekOffset();

        // Blend static lean (40%) with dynamic gap-seek (60%) for gap-threading vehicles.
        // Pure static lean for non-gap-threading vehicles.
        float blendedOffset = ctx.CanGapThread
            ? stableOffset * 0.4f + dynamicOffset * 0.6f
            : stableOffset;

        float finalOffset = blendedOffset * maxOffset * agility;
        targetPoint += ctx.Transform.right * finalOffset;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Gap-seek offset — returns -1..1
    //   negative = seek left (gap on left)
    //   positive = seek right (gap on right)
    //   0        = balanced or no clear preference
    // ─────────────────────────────────────────────────────────────────────

    float ComputeGapSeekOffset()
    {
        var map = ctx.Map;
        if (map == null) return 0f;

        // Check each side: blocked = vehicle beside or in front on that side
        bool leftBlocked  = map.HasVehicle(MapZone.BesideLeft)  ||
                            map.HasVehicle(MapZone.FrontLeft);
        bool rightBlocked = map.HasVehicle(MapZone.BesideRight) ||
                            map.HasVehicle(MapZone.FrontRight);

        // Also check FrontClose — if directly ahead is clear, stay centred
        bool frontClear = !map.HasVehicle(MapZone.FrontClose);

        if (frontClear && !leftBlocked && !rightBlocked)
            return 0f;   // open road — use static lean only

        if (leftBlocked  && !rightBlocked) return  0.7f;   // right gap — slide right
        if (rightBlocked && !leftBlocked)  return -0.7f;   // left gap  — slide left

        // Both sides blocked — check if there's more distance on one side
        // Use distance from map cells to pick the larger gap
        var leftCell  = map[MapZone.BesideLeft];
        var rightCell = map[MapZone.BesideRight];

        if (leftCell.vehicle != null && rightCell.vehicle != null)
        {
            // Lean toward the side with more space
            return leftCell.distance > rightCell.distance ? -0.4f : 0.4f;
        }

        return 0f;  // symmetric or insufficient data — hold
    }
}