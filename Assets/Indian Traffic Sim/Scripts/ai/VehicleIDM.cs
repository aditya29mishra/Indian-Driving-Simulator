using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// VehicleIDM — Intelligent Driver Model, signal stopping, and green-light restart.
//
// OOP role: Class + Encapsulation.
//   Writes ctx.IdmAccel every tick. That is its only output.
//
// VehicleProfile integration:
//   All IDM float constants now come from ctx (which ApplyConfig() already
//   overwrites from the profile), so no structural changes to IDM math.
//
//   New profile-driven behaviours:
//     Signal creep  — bikes/rickshaws inch forward at red (ctx.CreepsAtSignal)
//     Pre-light jump — bikes/rickshaws begin accelerating before full green
//                      (ctx.JumpsPrelightGreen)
//     Merge yield factor — scales with Social.assertiveness instead of
//                          being hardcoded per DriverProfile
// ─────────────────────────────────────────────────────────────────────────────

public class VehicleIDM : IVehicleBehaviour
{
    private readonly VehicleContext ctx;

    public VehicleIDM(VehicleContext ctx) { this.ctx = ctx; }

    public void OnSpawn()
    {
        ctx.IdmAccel       = 0f;
        ctx.InRestartPhase = false;
        ctx.RestartTimer   = 0f;
    }

    public void OnDespawn() { }

    public void Tick(float dt)
    {
        if (ctx.InRestartPhase)
        {
            ctx.RestartTimer -= dt;
            if (ctx.RestartTimer <= 0f) ctx.InRestartPhase = false;
        }

        // Pre-light jump check — before UpdateIDM so jump can set InRestartPhase
        CheckPrelightJump();

        UpdateIDM();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Dynamic braking helper
    // ─────────────────────────────────────────────────────────────────────

    float DynamicBrake(float speed, float distToTarget,
                       float safetyMargin   = 1.2f,
                       float maxBrakingMult = 1.0f,
                       float reactionTime   = 0.3f)
    {
        float effectiveDist = distToTarget - speed * reactionTime;
        effectiveDist = Mathf.Max(effectiveDist, 0.1f);
        float required  = (speed * speed) / (2f * effectiveDist);
        float maxBrake  = ctx.Braking * maxBrakingMult;
        return Mathf.Min(required * safetyMargin, maxBrake);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Pre-light jump — bikes/rickshaws start moving before full green
    // ─────────────────────────────────────────────────────────────────────

    void CheckPrelightJump()
    {
        if (!ctx.JumpsPrelightGreen) return;
        if (ctx.InRestartPhase) return;

        // Watch for Yellow→Red transition on the current cycle.
        // In Indian traffic, aggressive riders jump as soon as the opposing
        // phase goes red (i.e. while our signal is still red but about to go green).
        // We model this as: if prev was yellow AND now red, start a partial throttle.
        bool aboutToGreen = ctx.PrevSignalState == TrafficSignal.SignalState.Yellow &&
                            ctx.CurrentSignalStateCached == TrafficSignal.SignalState.Red;

        if (aboutToGreen && ctx.CurrentSpeed < 0.3f)
        {
            ctx.InRestartPhase  = true;
            ctx.RestartTimer    = 0.5f;   // short pre-start — half throttle for 0.5s
            ctx.RestartDuration = 0.5f;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Core IDM
    // ─────────────────────────────────────────────────────────────────────

    void UpdateIDM()
    {
        UpdateDesiredSpeed();

        // ── YIELD RELEASE RAMP ────────────────────────────────────────────
        if (ctx.YieldReleaseTimer > 0f)
        {
            ctx.YieldReleaseTimer -= Time.fixedDeltaTime;
            if (ctx.YieldReleaseTimer < 0f) ctx.YieldReleaseTimer = 0f;

            float rampProgress     = 1f - (ctx.YieldReleaseTimer / VehicleContext.YieldReleaseDuration);
            ctx._yieldRampMaxAccel = Mathf.Lerp(0.3f, ctx.Acceleration, rampProgress);
        }
        else
        {
            ctx._yieldRampMaxAccel = float.MaxValue;
        }

        float v = ctx.CurrentSpeed;

        // ── INTERSECTION YIELD ────────────────────────────────────────────
        if (ctx.IntersectionPriority != null &&
            ctx.IntersectionPriority.ShouldYieldAtEntry &&
            !ctx.InsideIntersection)
        {
            if (ctx.CurrentLane?.path != null && ctx.CurrentLane.path.waypoints.Count > 0)
            {
                Vector3 stopLine   = ctx.CurrentLane.path.waypoints[ctx.CurrentLane.path.waypoints.Count - 1].position;
                float   distToStop = Vector3.Distance(ctx.Transform.position, stopLine);

                ctx.IdmAccel = v < 0.1f
                    ? -ctx.Braking * 2.0f
                    : -DynamicBrake(v, distToStop, 1.4f, 2.0f, 0.25f);
            }
            else
            {
                ctx.IdmAccel = -ctx.Braking * 2.0f;
            }
            return;
        }

        // ── MERGE YIELD ───────────────────────────────────────────────────
        if (ctx.MergingVehicle != null)
        {
            bool done = ctx.MergingVehicle.currentLane == ctx.CurrentLane ||
                        Vector3.Distance(ctx.Transform.position, ctx.MergingVehicle.transform.position) > 25f;

            ctx.MergeYieldTimer -= Time.fixedDeltaTime;
            if (done || ctx.MergeYieldTimer <= 0f)
            {
                ctx.MergingVehicle  = null;
                ctx.MergeYieldTimer = 0f;
            }
            else
            {
                // Yield factor from assertiveness — less assertive = yields more
                // 1.0 assertiveness = 0.8 factor (yields least)
                // 0.0 assertiveness = 0.3 factor (yields most)
                float assertiveness = ctx.Assertiveness;
                float yieldFactor   = Mathf.Lerp(0.3f, 0.8f, assertiveness);
                ctx.IdmAccel = Mathf.Min(ctx.IdmAccel, ctx.Acceleration * yieldFactor * -0.3f);
                return;
            }
        }

        // ── SIGNAL STOP ───────────────────────────────────────────────────
        if (MustStopForSignal())
        {
            var signalFrontCell  = ctx.Map?[MapZone.FrontClose];
            bool carAheadStopped = signalFrontCell != null &&
                                   signalFrontCell.vehicle != null &&
                                   signalFrontCell.vehicle.CurrentSpeed < 0.5f;

            if (carAheadStopped)
            {
                // Queue follower
                float gap      = Mathf.Max(signalFrontCell.distance - ctx.VehicleLength, 0.1f);
                float deltaV   = v - 0f;
                float queueGap = Mathf.Max(ctx.MinimumGap * 0.5f, 1.0f);
                float sStar    = queueGap + Mathf.Max(0f,
                    v * ctx.TimeHeadway * 0.5f +
                    (v * deltaV) / (2f * Mathf.Sqrt(Mathf.Max(ctx.Acceleration * ctx.Braking, 0.01f))));

                ctx.IdmAccel = ctx.Acceleration * (
                    1f
                    - Mathf.Pow(v / Mathf.Max(ctx.DesiredSpeedMs, 0.1f), 4f)
                    - Mathf.Pow(sStar / gap, 2f));
                ctx.IdmAccel = Mathf.Max(ctx.IdmAccel, -ctx.Braking);
                return;
            }

            // Queue leader — brake to stop line
            Vector3 stopLine  = ctx.CurrentLane.path.waypoints[ctx.CurrentLane.path.waypoints.Count - 1].position;
            float distToLine  = Vector3.Distance(ctx.Transform.position, stopLine);

            if (v < 0.1f)
            {
                ctx.IdmAccel = -ctx.Braking * 1.5f;

                // Signal creep — bikes and rickshaws inch forward toward stop line
                ApplySignalCreep(distToLine);
            }
            else
            {
                ctx.IdmAccel = -DynamicBrake(v, distToLine, 1.3f, 1.5f, 0.3f);
            }
            return;
        }

        // ── GREEN EDGE DETECTION ──────────────────────────────────────────
        if (ctx.PrevSignalState != TrafficSignal.SignalState.Green &&
            ctx.CurrentSignalStateCached == TrafficSignal.SignalState.Green)
        {
            ctx.InRestartPhase = true;
            ctx.RestartTimer   = ctx.RestartDuration;
            ctx.DesiredSpeedMs = ctx.BaseSpeedMs;
        }
        ctx.PrevSignalState = ctx.CurrentSignalStateCached;

        // ── CROSSING CAR BRAKING ──────────────────────────────────────────
        {
            var crossCell = ctx.Map?[MapZone.Crossing];
            if (crossCell?.vehicle != null)
            {
                float safeGap    = ctx.VehicleLength + 1.5f;
                float distToStop = Mathf.Max(crossCell.distance - safeGap, 0.1f);

                int  myTurn           = ctx.CurrentTurnType;
                int  theirTurn        = crossCell.vehicle.context?.CurrentTurnType ?? 0;
                bool theyHavePriority = theirTurn >= myTurn;

                if (theyHavePriority || ctx.IntersectionPriority?.ShouldYieldAtEntry == true)
                {
                    float brake = v < 0.1f
                        ? ctx.Braking * 2.0f
                        : DynamicBrake(v, distToStop, 1.4f, 2.0f, 0.25f);
                    ctx.IdmAccel = Mathf.Min(ctx.IdmAccel, -brake);
                }
                else
                {
                    float awarenessGap   = Mathf.Max(distToStop * 0.5f, 2f);
                    float awarenessBrake = v < 0.1f ? 0f
                        : DynamicBrake(v, awarenessGap, 0.5f, 0.5f, 0.1f);
                    ctx.IdmAccel = Mathf.Min(ctx.IdmAccel, -awarenessBrake);
                }
            }
        }

        // ── LOW SPEED ASSIST ──────────────────────────────────────────────
        if (v < 1.0f)
            ctx.IdmAccel = Mathf.Max(ctx.IdmAccel, 1.5f);

        // ── NORMAL IDM ────────────────────────────────────────────────────
        var idmFrontCell = ctx.Map?[MapZone.FrontClose];
        TrafficVehicle leader = idmFrontCell?.vehicle ?? ctx.LeaderVehicle;

        if (leader == null)
        {
            ctx.IdmAccel = ctx.Acceleration *
                (1f - Mathf.Pow(v / Mathf.Max(ctx.DesiredSpeedMs, 0.1f), 4f));
        }
        else
        {
            float vL  = leader.CurrentSpeed;
            float gap = idmFrontCell != null
                ? Mathf.Max(idmFrontCell.distance - ctx.VehicleLength, 0.1f)
                : Mathf.Max(Vector3.Distance(ctx.Transform.position,
                              leader.transform.position) - ctx.VehicleLength, 0.1f);

            float deltaV = v - vL;

            float sStar = ctx.MinimumGap + Mathf.Max(0f,
                v * ctx.TimeHeadway +
                (v * deltaV) / (2f * Mathf.Sqrt(Mathf.Max(ctx.Acceleration * ctx.Braking, 0.01f))));

            ctx.IdmAccel = ctx.Acceleration * (
                1f
                - Mathf.Pow(v / Mathf.Max(ctx.DesiredSpeedMs, 0.1f), 4f)
                - Mathf.Pow(sStar / gap, 2f));

            ctx.IdmAccel = v < 1.5f
                ? Mathf.Max(ctx.IdmAccel, -ctx.Braking * 0.3f)
                : Mathf.Max(ctx.IdmAccel, -ctx.Braking);
        }

        if (ctx._yieldRampMaxAccel < float.MaxValue && ctx.IdmAccel > 0f)
            ctx.IdmAccel = Mathf.Min(ctx.IdmAccel, ctx._yieldRampMaxAccel);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Signal creep — bikes/rickshaws inch toward stop line while stopped
    // ─────────────────────────────────────────────────────────────────────

    void ApplySignalCreep(float distToLine)
    {
        if (!ctx.CreepsAtSignal) return;
        if (distToLine < 1.5f) return;   // already at line

        // Only creep if the space ahead is clear (no car directly in front)
        var frontCell  = ctx.Map?[MapZone.FrontClose];
        bool clearAhead = frontCell == null || frontCell.vehicle == null ||
                          frontCell.distance > ctx.VehicleLength + 1.5f;

        if (clearAhead)
            ctx.IdmAccel = 0.8f;   // gentle creep — overrides the hold brake
    }

    // ─────────────────────────────────────────────────────────────────────
    // Dynamic desired speed — unchanged from original
    // ─────────────────────────────────────────────────────────────────────

    void UpdateDesiredSpeed()
    {
        if (ctx.OvertakeCooldown > 0f)
        {
            ctx.OvertakeCooldown -= Time.fixedDeltaTime;
            if (ctx.OvertakeCooldown <= 0f)
            {
                ctx.OvertakeCooldown    = 0f;
                ctx.OvertakeBoostActive = false;
            }
        }

        if (ctx.InsideIntersection)
        {
            ctx.DesiredSpeedMs      = ctx.BaseSpeedMs;
            ctx.OvertakeBoostActive = false;
            return;
        }

        if (ctx.OvertakeBoostActive)
        {
            float boostFactor = ctx.DriverProfile == TrafficVehicle.DriverProfile.Aggressive ? 1.35f
                              : ctx.DriverProfile == TrafficVehicle.DriverProfile.Cautious   ? 1.08f
                              : 1.20f;
            ctx.DesiredSpeedMs = Mathf.Min(ctx.BaseSpeedMs * boostFactor, ctx.MaxSpeedMs);
            return;
        }

        var speedFrontCell = ctx.Map?[MapZone.FrontClose];
        TrafficVehicle leader = speedFrontCell?.vehicle;

        if (leader == null)
        {
            ctx.DesiredSpeedMs = ctx.BaseSpeedMs;
            return;
        }

        float leaderSpeed = leader.CurrentSpeed;

        if (leaderSpeed >= ctx.BaseSpeedMs * 0.9f)
        {
            ctx.DesiredSpeedMs = ctx.BaseSpeedMs;
            return;
        }

        if (ctx.LaneChangeCooldown > 0f)
        {
            ctx.DesiredSpeedMs = ctx.BaseSpeedMs;
            return;
        }

        float targetFollow = Mathf.Max(leaderSpeed + 0.5f, ctx.BaseSpeedMs * 0.6f);
        ctx.DesiredSpeedMs = Mathf.Lerp(ctx.DesiredSpeedMs, targetFollow, 0.05f);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Signal check — public, called by TrafficVehicle state system
    // ─────────────────────────────────────────────────────────────────────

    public bool MustStopForSignal()
    {
        if (ctx.CurrentSignal == null || ctx.CurrentLane == null) return false;
        if (ctx.CurrentLane.road == null) return false;
        if (ctx.CurrentLane.path == null || ctx.CurrentLane.path.waypoints.Count == 0) return false;

        var state = ctx.CurrentSignal.GetStateForLane(ctx.CurrentLane);
        if (state == TrafficSignal.SignalState.Green) return false;

        Vector3 stopLinePos = ctx.CurrentLane.path.waypoints[ctx.CurrentLane.path.waypoints.Count - 1].position;
        float distToLine    = Vector3.Distance(ctx.Transform.position, stopLinePos);
        float dynamicDist   = Mathf.Max(ctx.StopLineDistance,
            (ctx.CurrentSpeed * ctx.CurrentSpeed) / (2f * Mathf.Max(ctx.Braking, 1f)) + 6f);

        if (state == TrafficSignal.SignalState.Yellow)
            return distToLine <= dynamicDist && distToLine > 1f;

        return distToLine <= dynamicDist;
    }
}