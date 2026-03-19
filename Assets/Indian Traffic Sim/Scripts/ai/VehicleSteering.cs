using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// VehicleSteering — pure-pursuit steering and spline distance tracking.
//
// OOP role: Class + Encapsulation.
//   All steering logic is isolated here. The only output is ctx.SteerOutput
//   (a -1..1 value) and ctx.DistanceTravelled. TrafficVehicle reads SteerOutput
//   and passes it to carMove.Move(). Nothing outside this class touches the
//   look-ahead or lateral error calculations.
// ─────────────────────────────────────────────────────────────────────────────

public class VehicleSteering : IVehicleBehaviour
{
    private readonly VehicleContext ctx;

    // Pure-pursuit base look-ahead distance in metres.
    // Grows with speed for smoother high-speed turns.
    private const float LookAheadBase = 6f;

    // Output read by TrafficVehicle for the CarMove.Move() call
    public float SteerOutput { get; private set; }

    public VehicleSteering(VehicleContext ctx) { this.ctx = ctx; }

    public void OnSpawn()  { SteerOutput = 0f; }
    public void OnDespawn(){ }

    public void Tick(float dt)
    {
        UpdateDistanceTravelled();
        SteerOutput = ComputeSteering();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Distance tracking
    // ─────────────────────────────────────────────────────────────────────

    void UpdateDistanceTravelled()
    {
        if (ctx.CurrentPath == null) return;
        float closest = ctx.CurrentPath.GetClosestDistance(ctx.Transform.position);

        // Snap directly — old Lerp(0.2) was asymptotic and never reached TotalLength,
        // preventing AdvanceToNextPath from firing at dead ends.
        // Allow backward correction only for jumps > 5m (spline discontinuity).
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

        // Apply lateral drift — models Indian traffic where vehicles don't stay
        // perfectly centred. The offset is applied in world space perpendicular to
        // the vehicle's forward direction. It uses a per-vehicle stable value derived
        // from the vehicle's instance ID so it doesn't jitter every frame.
        if (ctx.LateralVariance > 0f && !ctx.InsideIntersection)
        {
            // Stable per-vehicle offset: use instance ID as a deterministic seed.
            // Produces a fixed left/right lean for the vehicle's lifetime — not random per frame.
            float stableOffset = (ctx.VehicleId.GetHashCode() % 1000 / 1000f - 0.5f) * 2f;
            Vector3 right = ctx.Transform.right;
            targetPoint += right * (stableOffset * ctx.LateralVariance);
        }

        Vector3 localTarget = ctx.Transform.InverseTransformPoint(targetPoint);
        float lateralError  = localTarget.x;
        float forwardDist   = Mathf.Max(localTarget.z, 1f);
        float steerAngle    = Mathf.Atan2(lateralError, forwardDist) * Mathf.Rad2Deg;

        return Mathf.Clamp(steerAngle / 25f, -1f, 1f);
    }
}