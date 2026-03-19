using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// NegotiationScenario — abstract base for every negotiation scenario.
//
// OOP role: Inheritance + Polymorphism + Abstract class.
//   Each of the 6 negotiation scenarios inherits from this base.
//   VehicleNegotiator holds a List<NegotiationScenario> and calls
//   Evaluate() + Apply() on each every tick. Adding scenario 7 means
//   creating one new subclass — VehicleNegotiator never changes.
//
//   The two-phase design (Evaluate then Apply) separates sensing from acting.
//   All scenarios evaluate first (read-only world scan), then apply
//   their results. This prevents one scenario's side-effects from
//   corrupting another scenario's scan within the same tick.
// ─────────────────────────────────────────────────────────────────────────────

public abstract class NegotiationScenario
{
    protected readonly VehicleContext ctx;
    // owner: the TrafficVehicle MonoBehaviour this scenario belongs to.
    // Needed to read computed state (CurrentSignalState) and for identity
    // checks against other vehicles ("am I looking at myself?").
    protected readonly TrafficVehicle owner;

    /// <summary>True when this scenario detected a condition that needs resolution.</summary>
    public bool IsActive { get; protected set; }

    protected NegotiationScenario(VehicleContext ctx, TrafficVehicle owner)
    {
        this.ctx   = ctx;
        this.owner = owner;
    }

    public abstract void Evaluate();
    public abstract void Apply(float dt);
    public virtual void Reset() { IsActive = false; }

    // ── Shared utility: gap scan in a target lane ─────────────────────────

    protected float GapInLane(TrafficLane lane, bool signalYield = false)
    {
        if (lane == null) return 50f;
        float minG = 50f;

        var cols = Physics.OverlapSphere(ctx.Transform.position, 20f);
        foreach (var col in cols)
        {
            var other = col.GetComponentInParent<TrafficVehicle>();
            if (other == null || other == owner) continue; // skip self — use reference equality
            if (other.currentLane != lane) continue;

            Vector3 toOther = other.transform.position - ctx.Transform.position;
            float dist = toOther.magnitude;
            float dot  = Vector3.Dot(ctx.Transform.forward, toOther.normalized);

            if (dot <= -0.3f) continue; // clearly behind — ignore

            float g = dist - ctx.VehicleLength;
            if (g < minG)
            {
                minG = g;
                if (signalYield && (other.mergingVehicle == null || other.mergingVehicle == owner))
                {
                    // Signal: we (owner) are the merger — other should yield to us
                    other.mergingVehicle  = owner;
                    other.mergeYieldTimer = 3f;
                }
            }
        }
        return minG;
    }

    /// <summary>
    /// Returns true if the rear vehicle is far enough away that a lane change is safe.
    /// "Safe" = rear vehicle is not closing fast enough to hit us within 1.5× headway.
    /// Called by LaneChangeScenario before committing to a lane change.
    /// </summary>
    protected bool IsRearGapSafe(TrafficVehicle rear)
    {
        if (rear == null) return true;
        float gap          = Vector3.Distance(ctx.Transform.position, rear.transform.position)
                             - ctx.VehicleLength;
        float closingSpeed = rear.CurrentSpeed - ctx.CurrentSpeed; // positive = rear closing on us
        // If rear is faster and the gap is smaller than 1.5× headway distance — unsafe
        if (closingSpeed > 0f && gap < closingSpeed * ctx.TimeHeadway * 1.5f) return false;
        return gap > ctx.MinimumGap * 2f;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Scenario 1: Lane change competition (open road)
//   Right-lane car wants left. Mid-lane car is beside it. Standard lane change
//   with gap check and merge yield signalling.
// ─────────────────────────────────────────────────────────────────────────────

public class LaneChangeScenario : NegotiationScenario
{
    private TrafficLane targetLane;
    private TrafficVehicle.ManeuverState direction;
    private readonly VehicleIDM idm;
    private readonly VehicleNavigator navigator;

    public LaneChangeScenario(VehicleContext ctx, TrafficVehicle owner, VehicleIDM idm, VehicleNavigator navigator)
        : base(ctx, owner)
    {
        this.idm       = idm;
        this.navigator = navigator;
    }

    public override void Evaluate()
    {
        IsActive   = false;
        targetLane = null;

        if (ctx.InsideIntersection || ctx.LaneChangeCooldown > 0f) return;
        if (ctx.CurrentPath == null || ctx.CurrentLane == null) return;
        if (ctx.CurrentPath != ctx.CurrentLane.path) return;
        if (ctx.CurrentLane.path == null) return;
        if (ctx.CurrentLane.path.TotalLength - ctx.DistanceTravelled < 45f) return;
        if (ctx.CurrentSpeed < 0.5f) return;
        if (idm.MustStopForSignal()) return;

        // Don't change around a car stopped for red
        if (ctx.LeaderVehicle != null && ctx.LeaderVehicle.CurrentSpeed < 0.1f
            && ctx.LeaderVehicle.currentSignal != null
            && ctx.LeaderVehicle.currentSignal.GetStateForLane(ctx.LeaderVehicle.currentLane)
               != TrafficSignal.SignalState.Green) return;

        bool blocked = ctx.LeaderVehicle != null && ctx.LeaderVehicle.CurrentSpeed < ctx.CurrentSpeed * 0.8f;
        bool slow    = ctx.CurrentSpeed < ctx.DesiredSpeedMs * 0.8f;
        if (!blocked && !slow) return;

        // Use NeighbourMap to check if adjacent lanes are safe BEFORE computing gap.
        // Without this: a vehicle would change lanes into a car directly beside it (sideswipe).
        // besideLeft/Right = car level with us → immediate collision if we move laterally.
        // rearLeft/Right   = car behind us closing fast → rear-end if we slow during change.
        var map = owner.Perception?.Map;
        bool leftSafe  = map == null || (!map.HasAnythingBesideLeft  &&
                         (map.rearLeft  == null || IsRearGapSafe(map.rearLeft)));
        bool rightSafe = map == null || (!map.HasAnythingBesideRight &&
                         (map.rearRight == null || IsRearGapSafe(map.rearRight)));

        float gL = (ctx.LeftLane  != null && leftSafe)  ? GapInLane(ctx.LeftLane,  signalYield: true) : -1f;
        float gR = (ctx.RightLane != null && rightSafe) ? GapInLane(ctx.RightLane, signalYield: true) : -1f;

        if      (gL > 0f && gR > 0f) targetLane = gL >= gR ? ctx.LeftLane : ctx.RightLane;
        else if (gL > 0f)             targetLane = ctx.LeftLane;
        else if (gR > 0f)             targetLane = ctx.RightLane;

        if (targetLane == null) return;

        float reqGap = ctx.MinimumGap + ctx.CurrentSpeed * 1.2f;
        float gap    = GapInLane(targetLane, signalYield: false);
        if (gap < reqGap) { targetLane = null; return; }

        direction = (targetLane == ctx.LeftLane) ? TrafficVehicle.ManeuverState.LaneChangeLeft
                                                 : TrafficVehicle.ManeuverState.LaneChangeRight;
        IsActive = true;
    }

    public override void Apply(float dt)
    {
        if (!IsActive || targetLane == null) return;

        ctx.PendingManeuverState = direction;
        ctx.CurrentLane          = targetLane;
        ctx.CurrentPath          = targetLane.path;
        ctx.DistanceTravelled    = ctx.CurrentPath.GetClosestDistance(ctx.Transform.position);
        ctx.LaneChangeCooldown   = 5f;
        ctx.LaneManager?.RefreshVehicle(owner);
        ctx.RouteHistory.Add("LC:" + targetLane.name);
        ctx.Log("LANE_CHANGE", targetLane.path.name, "", direction.ToString(), ctx.CurrentSpeed);

        IsActive   = false;
        targetLane = null;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Scenario 2: Crossing paths inside intersection
//   North car turning left crosses the path of South car going straight.
//   The left-turner yields to the straight-goer.
// ─────────────────────────────────────────────────────────────────────────────

public class CrossPathScenario : NegotiationScenario
{
    private float yieldAccel;
    private const float CrossCheckRadius = 15f;
    private const float CrossDotThreshold = 0.7f; // ~45° — paths are crossing if dot < this

    public CrossPathScenario(VehicleContext ctx, TrafficVehicle owner) : base(ctx, owner) { }

    public override void Evaluate()
    {
        IsActive  = false;
        yieldAccel = 0f;

        if (!ctx.InsideIntersection) return;

        var cols = Physics.OverlapSphere(ctx.Transform.position, CrossCheckRadius);
        foreach (var col in cols)
        {
            var other = col.GetComponentInParent<TrafficVehicle>();
            if (other == null || other == owner) continue; // skip self
            if (!other.IsInsideIntersection) continue; // other must also be in intersection

            Vector3 toOther = other.transform.position - ctx.Transform.position;
            float dist = toOther.magnitude;
            if (dist < 0.5f || dist > CrossCheckRadius) continue;

            // Check if paths are crossing: heading vectors not aligned = crossing
            float headingDot = Vector3.Dot(ctx.Transform.forward, other.transform.forward);
            if (Mathf.Abs(headingDot) > CrossDotThreshold) continue; // same or opposite direction, not crossing

            // Check other is ahead and closing
            float aheadDot = Vector3.Dot(ctx.Transform.forward, toOther.normalized);
            if (aheadDot < 0.2f) continue; // not ahead of us

            // Left-turner yields to straight-goer.
            // Detect left turn: our heading turned significantly left of our approach direction.
            // Simple proxy: if the other vehicle is going roughly straight (its heading dot with
            // its own forward vs world up-cross is small), it has right of way.
            float crossingGap = dist - ctx.VehicleLength;
            if (crossingGap < 8f) // within collision risk range
            {
                // Yield: reduce acceleration proportionally to how close the crossing point is
                float urgency = 1f - Mathf.Clamp01(crossingGap / 8f);
                yieldAccel = -ctx.Braking * urgency * 0.6f;
                IsActive = true;
                break; // yield to first detected threat
            }
        }
    }

    public override void Apply(float dt)
    {
        if (!IsActive) return;
        // Soft brake — don't override signal or full brake, just reduce acceleration
        ctx.IdmAccel = Mathf.Min(ctx.IdmAccel, yieldAccel);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Scenario 3: Merge onto road from turn path
//   Vehicle completes a turn and enters a lane that already has traffic.
//   The entering vehicle yields briefly to let existing traffic establish gap.
// ─────────────────────────────────────────────────────────────────────────────

public class MergeOntoRoadScenario : NegotiationScenario
{
    private float yieldTimer = 0f;
    private const float YieldDuration = 1.5f; // brief yield after completing a turn

    public MergeOntoRoadScenario(VehicleContext ctx, TrafficVehicle owner) : base(ctx, owner) { }

    public override void Reset() { base.Reset(); yieldTimer = 0f; }

    public override void Evaluate()
    {
        // Trigger when we just finished a turn (transition: was in intersection, now on lane path)
        // This is detected by checking that we are NOT in intersection but were recently (yieldTimer > 0)
        // OR that we just completed a turn this frame.
        // The navigator sets InsideIntersection to false when the turn completes,
        // so we monitor that transition.
        if (yieldTimer > 0f) { IsActive = true; return; }

        // No ongoing yield — check if we just merged (fast vehicles nearby in our lane)
        if (ctx.InsideIntersection) { IsActive = false; return; }

        float gap = GapInLane(ctx.CurrentLane, signalYield: false);
        if (gap < 6f) // someone is close in the lane we just merged into
        {
            yieldTimer = YieldDuration;
            IsActive   = true;
        }
        else
        {
            IsActive = false;
        }
    }

    public override void Apply(float dt)
    {
        if (!IsActive) return;
        yieldTimer -= dt;
        if (yieldTimer <= 0f) { yieldTimer = 0f; IsActive = false; return; }

        // Soft yield: cap acceleration to let existing traffic establish a gap
        float yieldFactor = ctx.DriverProfile == TrafficVehicle.DriverProfile.Cautious  ? 0.3f :
                            ctx.DriverProfile == TrafficVehicle.DriverProfile.Aggressive ? 0.7f : 0.5f;
        ctx.IdmAccel = Mathf.Min(ctx.IdmAccel, ctx.Acceleration * yieldFactor);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Scenario 4: Queue restart ripple
//   At green light, only the front vehicle should get leader-suppressed.
//   Vehicles further back keep their leader and follow IDM naturally —
//   this produces a compressed ripple instead of synchronised launch.
//   This scenario communicates the position-in-queue to LaneManager.
// ─────────────────────────────────────────────────────────────────────────────

public class QueueRippleScenario : NegotiationScenario
{
    public bool IsFrontOfQueue { get; private set; }

    public QueueRippleScenario(VehicleContext ctx, TrafficVehicle owner) : base(ctx, owner) { }

    public override void Evaluate()
    {
        // Front of queue = no leader, or leader is not waiting at a red signal
        IsFrontOfQueue = ctx.LeaderVehicle == null ||
                         ctx.LeaderVehicle.CurrentSignalState != TrafficVehicle.SignalStateEx.WaitingRed;
        // Active during the green-light restart phase — owner.CurrentSignalState reads the
        // computed value from TrafficVehicle.UpdateStateSystem, not from VehicleContext
        IsActive = owner.CurrentSignalState == TrafficVehicle.SignalStateEx.Released;
    }

    public override void Apply(float dt)
    {
        // If not the front car, restore leader so IDM naturally creates the ripple delay.
        // LaneManager reads IsFrontOfQueue to decide whether to suppress leaderVehicle.
        // No idmAccel override here — IDM handles it correctly once leader is restored.
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Scenario 5: Narrow gap / oncoming vehicle
//   Two vehicles on opposing paths approaching a narrow point.
//   The vehicle with lower priority yields (typically the one turning).
// ─────────────────────────────────────────────────────────────────────────────

public class NarrowGapScenario : NegotiationScenario
{
    private const float NarrowRadius   = 12f;
    private const float NarrowGapLimit = 5f;

    public NarrowGapScenario(VehicleContext ctx, TrafficVehicle owner) : base(ctx, owner) { }

    public override void Evaluate()
    {
        IsActive = false;
        if (ctx.InsideIntersection) return;

        var cols = Physics.OverlapSphere(ctx.Transform.position, NarrowRadius);
        foreach (var col in cols)
        {
            var other = col.GetComponentInParent<TrafficVehicle>();
            if (other == null || other == owner) continue; // skip self

            Vector3 toOther = other.transform.position - ctx.Transform.position;
            float dist      = toOther.magnitude;

            // Oncoming: heading roughly opposite (dot < -0.5) AND ahead
            float headDot   = Vector3.Dot(ctx.Transform.forward, other.transform.forward);
            float aheadDot  = Vector3.Dot(ctx.Transform.forward, toOther.normalized);
            if (headDot > -0.5f || aheadDot < 0.3f) continue;

            if (dist < NarrowGapLimit)
            {
                IsActive = true;
                break;
            }
        }
    }

    public override void Apply(float dt)
    {
        if (!IsActive) return;
        // Yield: slow down significantly but don't stop — creep forward to negotiate
        float yieldFactor = ctx.DriverProfile == TrafficVehicle.DriverProfile.Aggressive ? 0.5f : 0.2f;
        ctx.IdmAccel = Mathf.Min(ctx.IdmAccel, ctx.Acceleration * yieldFactor);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Scenario 6: Aggressive assertion
//   An aggressive driver pulls alongside a slower/cautious vehicle and holds
//   the position, forcing the other to yield. Models lane-sharing pressure.
// ─────────────────────────────────────────────────────────────────────────────

public class AggressiveAssertionScenario : NegotiationScenario
{
    private float assertionTimer = 0f;
    private const float AssertionCooldown = 8f;

    public AggressiveAssertionScenario(VehicleContext ctx, TrafficVehicle owner) : base(ctx, owner) { }

    public override void Reset() { base.Reset(); assertionTimer = 0f; }

    public override void Evaluate()
    {
        IsActive = false;
        assertionTimer -= Time.fixedDeltaTime;
        if (assertionTimer > 0f) return;

        if (ctx.DriverProfile != TrafficVehicle.DriverProfile.Aggressive) return;
        if (ctx.CurrentSpeed < 3f) return;
        if (ctx.InsideIntersection) return;

        TrafficLane assertTarget = ctx.LeftLane ?? ctx.RightLane;
        if (assertTarget == null) return;

        var cols = Physics.OverlapSphere(ctx.Transform.position, 10f);
        foreach (var col in cols)
        {
            var other = col.GetComponentInParent<TrafficVehicle>();
            if (other == null || other == owner) continue; // skip self — reference equality
            if (other.currentLane != assertTarget) continue;
            if (other.driverProfile != TrafficVehicle.DriverProfile.Cautious) continue;

            Vector3 toOther = other.transform.position - ctx.Transform.position;
            float aheadDot  = Vector3.Dot(ctx.Transform.forward, toOther.normalized);
            if (aheadDot < 0.3f || aheadDot > 0.85f) continue;

            // Assert: we (owner) are the asserting vehicle — signal other to yield to us
            other.mergingVehicle  = owner;
            other.mergeYieldTimer = 2f;
            assertionTimer        = AssertionCooldown;
            IsActive              = true;
            break;
        }
    }

    public override void Apply(float dt)
    {
        // No idmAccel change — the asserting car keeps its speed.
        // The effect is purely on the other vehicle via mergingVehicle yield.
    }
}