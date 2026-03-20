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
        // Update desired speed before IDM math — dynamic target based on situation
        UpdateDesiredSpeed();

        // ── YIELD RELEASE RAMP ────────────────────────────────────────────
        // After yield clears, ramp acceleration gradually — prevents snap.
        // While YieldReleaseTimer > 0: cap acceleration to a gentle value.
        if (ctx.YieldReleaseTimer > 0f)
        {
            ctx.YieldReleaseTimer -= Time.fixedDeltaTime;
            if (ctx.YieldReleaseTimer < 0f) ctx.YieldReleaseTimer = 0f;

            // Progress 0→1 as timer counts down
            float rampProgress = 1f - (ctx.YieldReleaseTimer / VehicleContext.YieldReleaseDuration);
            // Gentle restart: max accel scales from 0.3 → full over 1.5s
            float maxAccel = Mathf.Lerp(0.3f, ctx.Acceleration, rampProgress);
            // Run normal IDM but cap the output
            // (fall through to normal IDM, then cap below)
            ctx._yieldRampMaxAccel = maxAccel;
        }
        else
        {
            ctx._yieldRampMaxAccel = float.MaxValue;
        }

        float v = ctx.CurrentSpeed;
        // ── INTERSECTION YIELD — hold at stop line for higher-priority crossing vehicle
        // Only applies when approaching (not yet inside) the intersection.
        // Once the higher-priority vehicle clears, IntersectionPrioritySystem
        // clears ShouldYieldAtEntry and normal IDM resumes.
        if (ctx.IntersectionPriority != null &&
            ctx.IntersectionPriority.ShouldYieldAtEntry &&
            !ctx.InsideIntersection)
        {
            // Emergency stop — must halt before stop line.
            // Use distance to stop line to ramp braking:
            //   far away  → moderate brake (no lurch)
            //   close     → maximum brake (must stop)
            //   stopped   → hold with strong brake to prevent creep
            if (ctx.CurrentLane?.path != null && ctx.CurrentLane.path.waypoints.Count > 0)
            {
                Vector3 stopLine    = ctx.CurrentLane.path.waypoints[ctx.CurrentLane.path.waypoints.Count - 1].position;
                float   distToStop  = Vector3.Distance(ctx.Transform.position, stopLine);
                float   stoppingReq = (ctx.CurrentSpeed * ctx.CurrentSpeed) /
                                      (2f * Mathf.Max(ctx.Braking, 1f));

                // Full emergency brake when stopping distance exceeds available distance
                float brakeMult = distToStop < stoppingReq + 2f ? 1.5f : 1.0f;
                ctx.IdmAccel = -ctx.Braking * brakeMult;
            }
            else
            {
                ctx.IdmAccel = -ctx.Braking * 1.5f;
            }
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
            // If there is a stopped car directly ahead — follow it via IDM gap logic.
            // This handles queue stacking correctly: second car follows first car,
            // third car follows second, etc. Stop line braking only for the first car.
            var signalFrontCell = ctx.Map?[MapZone.FrontClose];
            bool carAheadStopped = signalFrontCell != null &&
                                   signalFrontCell.vehicle != null &&
                                   signalFrontCell.vehicle.CurrentSpeed < 0.5f;

            if (carAheadStopped)
            {
                // Follow the stopped car as a leader — IDM gap term handles spacing.
                // Use a tighter minimum gap for queuing (1m instead of normal 2m).
                float vL    = 0f;
                float gap   = Mathf.Max(signalFrontCell.distance - ctx.VehicleLength, 0.1f);
                float deltaV = v - vL;
                float queueGap = Mathf.Max(ctx.MinimumGap * 0.5f, 1.0f);

                float sStar = queueGap + Mathf.Max(0f,
                    v * ctx.TimeHeadway * 0.5f +
                    (v * deltaV) / (2f * Mathf.Sqrt(Mathf.Max(ctx.Acceleration * ctx.Braking, 0.01f))));

                ctx.IdmAccel = ctx.Acceleration * (
                    1f
                    - Mathf.Pow(v / Mathf.Max(ctx.DesiredSpeedMs, 0.1f), 4f)
                    - Mathf.Pow(sStar / gap, 2f));

                ctx.IdmAccel = Mathf.Max(ctx.IdmAccel, -ctx.Braking);
                return;
            }

            // First car in queue — brake to stop line
            Vector3 stopLine  = ctx.CurrentLane.path.waypoints[ctx.CurrentLane.path.waypoints.Count - 1].position;
            float distToLine  = Vector3.Distance(ctx.Transform.position, stopLine);

            float stoppingDist = Mathf.Max(
                ctx.StopLineDistance,
                (ctx.CurrentSpeed * ctx.CurrentSpeed) / (2f * Mathf.Max(ctx.Braking, 1f)) + 4f);

            float brakeFraction = Mathf.Clamp01((stoppingDist - distToLine + 2f) / stoppingDist);
            ctx.IdmAccel = -ctx.Braking * Mathf.Max(brakeFraction, 0.5f);
            return;
        }

        // ── GREEN EDGE DETECTION ──────────────────────────────────────────
        // Detects the single-frame Red→Green transition.
        // Sets InRestartPhase so LaneManager suppresses leader for front car.
        // UpdateDesiredSpeed restores base speed — normal IDM handles acceleration.
        // prevSignalState updated AFTER the check so the edge fires exactly once.
        if (ctx.PrevSignalState != TrafficSignal.SignalState.Green &&
            ctx.CurrentSignalStateCached == TrafficSignal.SignalState.Green)
        {
            ctx.InRestartPhase = true;
            ctx.RestartTimer   = ctx.RestartDuration;
            // Restore desired speed to base — was possibly reduced while following queue
            ctx.DesiredSpeedMs = ctx.BaseSpeedMs;
        }
        ctx.PrevSignalState = ctx.CurrentSignalStateCached;

        // ── CROSSING CAR BRAKING — inside intersection ───────────────────
        // VehicleMap.Crossing has a car whose path conflicts with ours.
        // Brake hard proportional to proximity — closer = harder.
        // When crossing car clears, cell empties and braking stops naturally.
        // ── TWO-BOOL CROSSING SYSTEM ─────────────────────────────────────
        // Bool 1: is there a car on a crossing path? (map[Crossing])
        // Bool 2: does that car have higher priority than me? (turn type comparison)
        //
        // Both bools true  → hard brake (must yield)
        // Only Bool 1      → awareness brake (we have priority but be cautious)
        // Neither          → no crossing brake
        //
        // Works for ALL situations: red signal, green signal, approaching, inside.
        // Does not depend on ShouldYieldAtEntry — that's a pre-entry hold.
        // This is the continuous real-time collision avoidance.
        {
            var crossCell = ctx.Map?[MapZone.Crossing];
            if (crossCell?.vehicle != null)
            {
                float dist    = crossCell.distance;
                float radius  = ctx.Map?.CrossRadius ?? 18f;
                float urgency = Mathf.Clamp01(1f - dist / radius);

                // Bool 2: compare turn types
                int myTurn    = ctx.CurrentTurnType;   // 0=none,1=left,2=straight,3=right
                int theirTurn = crossCell.vehicle.context?.CurrentTurnType ?? 0;

                // Higher turn type number = higher priority
                // If their turn type is unknown (0) assume equal — be cautious
                bool theyHavePriority = theirTurn >= myTurn;

                if (theyHavePriority || ctx.IntersectionPriority?.ShouldYieldAtEntry == true)
                {
                    // Hard brake — scale with distance and intersection state
                    float maxMult = ctx.InsideIntersection ? 2.0f : 1.5f;
                    float minMult = ctx.InsideIntersection ? 0.8f : 0.4f;
                    float brake   = ctx.Braking * Mathf.Lerp(minMult, maxMult, urgency);
                    ctx.IdmAccel  = Mathf.Min(ctx.IdmAccel, -brake);
                }
                else
                {
                    // We have priority — slight awareness brake only
                    // Slows slightly in case the other car doesn't yield
                    float brake  = ctx.Braking * Mathf.Lerp(0f, 0.3f, urgency);
                    ctx.IdmAccel = Mathf.Min(ctx.IdmAccel, -brake);
                }
            }
        }

        // ── LOW SPEED ASSIST ──────────────────────────────────────────────
        // IDM can under-drive at very low speeds; this floor prevents stalling.
        if (v < 1.0f)
            ctx.IdmAccel = Mathf.Max(ctx.IdmAccel, 1.5f);

        // ── NORMAL IDM ────────────────────────────────────────────────────
        // Leader = closest vehicle in FrontClose zone from VehicleMap.
        // Updated every tick by WorldPerception — zero delay, no 0.2s lag.
        // Falls back to ctx.LeaderVehicle for queue ripple signal restart logic
        // (LaneManager still writes LeaderVehicle only for that purpose).
        var idmFrontCell = ctx.Map?[MapZone.FrontClose];
        TrafficVehicle leader = idmFrontCell?.vehicle ?? ctx.LeaderVehicle;

        if (leader == null)
        {
            // Free-flow: (v/v0)^4 smoothly tapers acceleration to 0 as v → v0
            ctx.IdmAccel = ctx.Acceleration *
                (1f - Mathf.Pow(v / Mathf.Max(ctx.DesiredSpeedMs, 0.1f), 4f));
        }
        else
        {
            // Use map cell data when available — more accurate than re-computing distance
            float vL  = leader.CurrentSpeed;
            float gap = idmFrontCell != null
                ? Mathf.Max(idmFrontCell.distance - ctx.VehicleLength, 0.1f)
                : Mathf.Max(Vector3.Distance(ctx.Transform.position,
                              leader.transform.position) - ctx.VehicleLength, 0.1f);

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

        // Apply yield release ramp cap — prevents snap acceleration after yield clears
        if (ctx._yieldRampMaxAccel < float.MaxValue && ctx.IdmAccel > 0f)
            ctx.IdmAccel = Mathf.Min(ctx.IdmAccel, ctx._yieldRampMaxAccel);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Dynamic desired speed
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates ctx.DesiredSpeedMs every tick based on the current situation.
    /// ctx.BaseSpeedMs is the personality speed set at spawn — never changes.
    /// ctx.DesiredSpeedMs is the live target IDM steers toward.
    ///
    /// Rules:
    ///   No leader              → base speed (free flow)
    ///   Leader faster          → base speed (IDM gap term keeps spacing)
    ///   Leader slower, far     → base speed (closing naturally via IDM)
    ///   Leader slower, close   → overtake candidate (LaneChange handles this)
    ///   Overtaking active      → boosted speed to pass the slow car
    ///   After overtake         → return to base speed
    ///   Inside intersection    → base speed (no overtake boost in intersections)
    /// </summary>
    void UpdateDesiredSpeed()
    {
        // Tick down overtake cooldown
        if (ctx.OvertakeCooldown > 0f)
        {
            ctx.OvertakeCooldown -= Time.fixedDeltaTime;
            if (ctx.OvertakeCooldown <= 0f)
            {
                ctx.OvertakeCooldown    = 0f;
                ctx.OvertakeBoostActive = false;
            }
        }

        // No boost inside intersection — focus on navigation
        if (ctx.InsideIntersection)
        {
            ctx.DesiredSpeedMs  = ctx.BaseSpeedMs;
            ctx.OvertakeBoostActive = false;
            return;
        }

        // Overtake boost — actively passing a slower car
        // Triggered externally by LaneChangeScenario when it commits to overtake
        if (ctx.OvertakeBoostActive)
        {
            float boostFactor = ctx.DriverProfile == TrafficVehicle.DriverProfile.Aggressive ? 1.35f
                              : ctx.DriverProfile == TrafficVehicle.DriverProfile.Cautious   ? 1.08f
                              : 1.20f;
            ctx.DesiredSpeedMs = Mathf.Min(ctx.BaseSpeedMs * boostFactor, ctx.MaxSpeedMs);
            return;
        }

        // Read leader from map
        var speedFrontCell = ctx.Map?[MapZone.FrontClose];
        TrafficVehicle leader = speedFrontCell?.vehicle;

        if (leader == null)
        {
            // Free flow — drive at base speed
            ctx.DesiredSpeedMs = ctx.BaseSpeedMs;
            return;
        }

        float leaderSpeed = leader.CurrentSpeed;

        if (leaderSpeed >= ctx.BaseSpeedMs * 0.9f)
        {
            // Leader is fast enough — no need to overtake, drive at base speed
            // IDM gap term will handle keeping distance naturally
            ctx.DesiredSpeedMs = ctx.BaseSpeedMs;
            return;
        }

        // Leader is slower than us
        // If lane change is already in progress, maintain base speed
        // (LaneChangeScenario will set OvertakeBoostActive when it commits)
        if (ctx.LaneChangeCooldown > 0f)
        {
            ctx.DesiredSpeedMs = ctx.BaseSpeedMs;
            return;
        }

        // Approaching a slow car — desired speed drops to match + small buffer
        // This prevents the car from braking too hard while waiting to overtake.
        // The lane change scenario will handle the actual overtake decision.
        float targetFollow = Mathf.Max(leaderSpeed + 0.5f, ctx.BaseSpeedMs * 0.6f);
        ctx.DesiredSpeedMs = Mathf.Lerp(ctx.DesiredSpeedMs, targetFollow, 0.05f);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Signal check — encapsulated here, used only by IDM
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Returns true if this vehicle should be braking for a signal right now.</summary>
    public bool MustStopForSignal()
    {
        if (ctx.CurrentSignal == null || ctx.CurrentLane == null) return false;
        // Arc lanes have road == null — don't brake for signals inside intersection
        if (ctx.CurrentLane.road == null) return false;
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