using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// VehicleIDM — Intelligent Driver Model, signal stopping, and green-light restart.
//
// OOP role: Class + Encapsulation.
//   Writes ctx.IdmAccel every tick. That is its only output. All IDM math,
//   signal detection, and restart-phase logic is hidden here. Nothing outside
//   this class performs acceleration calculations.
// ─────────────────────────────────────────────────────────────────────────────

public class VehicleIDM : IVehicleBehaviour
{
    private readonly VehicleContext ctx;

    public VehicleIDM(VehicleContext ctx) { this.ctx = ctx; }

    public void OnSpawn()
    {
        ctx.IdmAccel = 0f;
        ctx.InRestartPhase = false;
        ctx.RestartTimer = 0f;
    }

    public void OnDespawn() { }

    public void Tick(float dt)
    {
        // Tick down the restart phase timer
        if (ctx.InRestartPhase)
        {
            ctx.RestartTimer -= dt;
            if (ctx.RestartTimer <= 0f) ctx.InRestartPhase = false;
        }

        UpdateIDM();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Core IDM
    // ─────────────────────────────────────────────────────────────────────

    void UpdateIDM()
    {
        float v = ctx.CurrentSpeed;
        // ── INTERSECTION YIELD — hold at stop line for higher-priority crossing vehicle
        // Only applies when approaching (not yet inside) the intersection.
        // Once the higher-priority vehicle clears, IntersectionPrioritySystem
        // clears ShouldYieldAtEntry and normal IDM resumes.
        if (ctx.IntersectionPriority != null &&
            ctx.IntersectionPriority.ShouldYieldAtEntry &&
            !ctx.InsideIntersection)
        {
            // Brake firmly to hold position just before the stop line
            ctx.IdmAccel = -ctx.Braking * 0.7f;
            return;
        }
        // ── MERGE YIELD — highest priority override ────────────────────────
        // A vehicle from an adjacent lane is merging into ours.
        // Apply a soft brake to open a gap — the Indian negotiation model.
        if (ctx.MergingVehicle != null)
        {
            bool done = ctx.MergingVehicle.currentLane == ctx.CurrentLane ||
                        Vector3.Distance(ctx.Transform.position, ctx.MergingVehicle.transform.position) > 25f;

            ctx.MergeYieldTimer -= Time.fixedDeltaTime;
            if (done || ctx.MergeYieldTimer <= 0f)
            {
                ctx.MergingVehicle = null;
                ctx.MergeYieldTimer = 0f;
            }
            else
            {
                // Yield pressure: cautious yields most, aggressive yields least
                float yieldFactor = ctx.DriverProfile == TrafficVehicle.DriverProfile.Cautious ? 0.5f :
                                    ctx.DriverProfile == TrafficVehicle.DriverProfile.Aggressive ? 0.8f : 0.65f;
                ctx.IdmAccel = Mathf.Min(ctx.IdmAccel, ctx.Acceleration * yieldFactor * -0.3f);
                return;
            }
        }

        // ── SIGNAL STOP — overrides all IDM logic ─────────────────────────
        if (MustStopForSignal())
        {
            Vector3 stopLine = ctx.CurrentLane.path.waypoints[ctx.CurrentLane.path.waypoints.Count - 1].position;
            float distToLine = Vector3.Distance(ctx.Transform.position, stopLine);
            float brakeFraction = Mathf.Clamp01((ctx.StopLineDistance - distToLine + 2f) / ctx.StopLineDistance);
            ctx.IdmAccel = -ctx.Braking * Mathf.Max(brakeFraction, 0.4f);
            return;
        }

        // ── GREEN EDGE DETECTION ──────────────────────────────────────────
        // Detects the single-frame Red→Green transition.
        // prevSignalState updated AFTER the check so the edge fires exactly once.
        if (ctx.PrevSignalState != TrafficSignal.SignalState.Green &&
            ctx.CurrentSignalStateCached == TrafficSignal.SignalState.Green)
        {
            ctx.InRestartPhase = true;
            ctx.RestartTimer = ctx.RestartDuration;
            ctx.IdmAccel = Mathf.Clamp(ctx.Acceleration * 0.8f, 1.0f, 2.0f);
        }
        ctx.PrevSignalState = ctx.CurrentSignalStateCached;

        // ── LOW SPEED ASSIST ──────────────────────────────────────────────
        // IDM can under-drive at very low speeds; this floor prevents stalling.
        if (v < 1.0f)
            ctx.IdmAccel = Mathf.Max(ctx.IdmAccel, 1.5f);

        // ── NORMAL IDM ────────────────────────────────────────────────────
        if (ctx.LeaderVehicle == null)
        {
            // Free-flow: (v/v0)^4 smoothly tapers acceleration to 0 as v → v0
            ctx.IdmAccel = ctx.Acceleration *
                (1f - Mathf.Pow(v / Mathf.Max(ctx.DesiredSpeedMs, 0.1f), 4f));
        }
        else
        {
            float vL = ctx.LeaderVehicle.CurrentSpeed;
            float gap = Mathf.Max(Vector3.Distance(ctx.Transform.position,
                          ctx.LeaderVehicle.transform.position) - ctx.VehicleLength, 0.1f);
            float deltaV = v - vL;

            // sStar: desired gap = min gap + headway term + braking term
            float sStar = ctx.MinimumGap + Mathf.Max(0f,
                v * ctx.TimeHeadway +
                (v * deltaV) / (2f * Mathf.Sqrt(Mathf.Max(ctx.Acceleration * ctx.Braking, 0.01f))));

            ctx.IdmAccel = ctx.Acceleration * (
                1f
                - Mathf.Pow(v / Mathf.Max(ctx.DesiredSpeedMs, 0.1f), 4f)
                - Mathf.Pow(sStar / gap, 2f));

            // Soft floor at low speed so the car can inch forward in dense traffic
            ctx.IdmAccel = v < 1.5f
                ? Mathf.Max(ctx.IdmAccel, -ctx.Braking * 0.3f)
                : Mathf.Max(ctx.IdmAccel, -ctx.Braking);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Signal check — encapsulated here, used only by IDM
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Returns true if this vehicle should be braking for a signal right now.</summary>
    public bool MustStopForSignal()
    {
        if (ctx.CurrentSignal == null || ctx.CurrentLane == null) return false;
        if (ctx.CurrentPath != null && ctx.CurrentLane.path != null &&
            ctx.CurrentPath != ctx.CurrentLane.path) return false; // already in intersection
        if (ctx.CurrentLane.path == null || ctx.CurrentLane.path.waypoints.Count == 0) return false;

        var state = ctx.CurrentSignal.GetStateForLane(ctx.CurrentLane);
        if (state == TrafficSignal.SignalState.Green) return false;

        Vector3 stopLinePos = ctx.CurrentLane.path.waypoints[ctx.CurrentLane.path.waypoints.Count - 1].position;
        float distToLine = Vector3.Distance(ctx.Transform.position, stopLinePos);
        float dynamicDist = Mathf.Max(ctx.StopLineDistance,
            (ctx.CurrentSpeed * ctx.CurrentSpeed) / (2f * Mathf.Max(ctx.Braking, 1f)) + 6f);

        if (state == TrafficSignal.SignalState.Yellow)
            return distToLine <= dynamicDist && distToLine > 1f; // don't brake if already past

        return distToLine <= dynamicDist;
    }
}