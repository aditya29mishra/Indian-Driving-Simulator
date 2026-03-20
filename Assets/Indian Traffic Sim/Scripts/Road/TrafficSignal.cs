using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficSignal — signal group cycling, per-lane state lookup, phase timing.
//
// Auto lane assignment: assign a TrafficIntersectionNode and click
// "Auto Assign Lanes From Node". Reads connectedRoads, groups forward lanes
// into signal phases based on intersection type.
//
//   FourWay  → 2 phases: opposing road pairs share a green
//   ThreeWay → 2 phases: most coaxial pair = main road, third = side
//   TwoWay   → 1 phase: all lanes green together
//
// Talks to: TrafficVehicle, TrafficLane, TrafficIntersectionNode
// ─────────────────────────────────────────────────────────────────────────────

public class TrafficSignal : MonoBehaviour
{
    public enum SignalState { Green, Yellow, Red }
    public enum TurnType    { Straight, Left, Right, All }

    [System.Serializable]
    public class SignalGroup
    {
        public string            groupName = "Group";
        public TurnType          turnType  = TurnType.All;
        public List<TrafficLane> lanes     = new List<TrafficLane>();
        [HideInInspector]
        public SignalState state = SignalState.Red;
    }

    [Header("Auto Lane Assignment")]
    [Tooltip("The TrafficIntersectionNode whose approach roads are auto-grouped into phases.")]
    public TrafficIntersectionNode intersectionNode;

    [Header("Signal Groups")]
    public List<SignalGroup> groups = new List<SignalGroup>();

    [Header("Timing (seconds)")]
    public float greenTime  = 25f;
    public float yellowTime = 3f;
    public float redTime    = 2f;

    [Header("Debug")]
    public bool showGizmos = true;

    private int   activeGroup = 0;
    private float phaseTimer  = 0f;
    private bool  inYellow    = false;
    private bool  inRedClear  = false;

    private Dictionary<TrafficLane, int> laneToGroup        = new Dictionary<TrafficLane, int>();
    private HashSet<TrafficLane>         missingLaneWarned  = new HashSet<TrafficLane>();

    // ─────────────────────────────────────────────────────────────────────
    // Auto assignment
    // ─────────────────────────────────────────────────────────────────────

    [ContextMenu("Auto Assign Lanes From Node")]
    public void AutoAssignLanesFromNode()
    {
        if (intersectionNode == null)
        {
            Debug.LogWarning("[TrafficSignal] intersectionNode not assigned.");
            return;
        }

        var roads = intersectionNode.connectedRoads;
        if (roads == null || roads.Count == 0)
        {
            Debug.LogWarning($"[TrafficSignal] '{intersectionNode.name}' has no connected roads.");
            return;
        }

        groups.Clear();

        switch (intersectionNode.intersectionType)
        {
            case TrafficIntersectionNode.IntersectionType.FourWay:
            case TrafficIntersectionNode.IntersectionType.ThreeWay:
                // One phase per road arm — sequential cycling, one direction green at a time
                AutoAssignSequential(roads);
                break;
            case TrafficIntersectionNode.IntersectionType.TwoWay:
                AutoAssignSinglePhase(roads);
                break;
        }

        BuildLookup();

        int total = 0;
        foreach (var g in groups) total += g.lanes.Count;
        Debug.Log($"[TrafficSignal] '{name}': {groups.Count} groups, {total} lanes " +
                  $"from '{intersectionNode.name}' ({intersectionNode.intersectionType}).");
    }

    // ── Phase strategies ──────────────────────────────────────────────────

    /// <summary>FourWay: find best opposing pair → Phase 0, remaining pair → Phase 1.</summary>
    void AutoAssignOpposingPairs(List<TrafficRoad> roads)
    {
        if (roads.Count < 2) { AutoAssignSinglePhase(roads); return; }

        int bestA = 0, bestB = 1;
        float bestDot = float.MaxValue;
        for (int i = 0; i < roads.Count; i++)
            for (int j = i + 1; j < roads.Count; j++)
            {
                if (roads[i] == null || roads[j] == null) continue;
                float dot = Vector3.Dot(
                    RoadDirFromIntersection(roads[i]),
                    RoadDirFromIntersection(roads[j]));
                if (dot < bestDot) { bestDot = dot; bestA = i; bestB = j; }
            }

        var g0 = new SignalGroup { groupName = "Phase_0" };
        AddArrivingLanes(roads[bestA], g0);
        AddArrivingLanes(roads[bestB], g0);
        groups.Add(g0);

        var g1 = new SignalGroup { groupName = "Phase_1" };
        for (int i = 0; i < roads.Count; i++)
        {
            if (i == bestA || i == bestB) continue;
            AddArrivingLanes(roads[i], g1);
        }
        if (g1.lanes.Count > 0) groups.Add(g1);
    }

    /// <summary>ThreeWay: most coaxial pair → Phase 0 (main), third road → Phase 1 (side).</summary>
    void AutoAssignTJunction(List<TrafficRoad> roads)
    {
        if (roads.Count < 2) { AutoAssignSinglePhase(roads); return; }

        int bestA = 0, bestB = 1;
        float bestDot = float.MaxValue;
        for (int i = 0; i < roads.Count; i++)
            for (int j = i + 1; j < roads.Count; j++)
            {
                if (roads[i] == null || roads[j] == null) continue;
                float dot = Vector3.Dot(
                    RoadDirFromIntersection(roads[i]),
                    RoadDirFromIntersection(roads[j]));
                if (dot < bestDot) { bestDot = dot; bestA = i; bestB = j; }
            }

        var g0 = new SignalGroup { groupName = "Phase_0_Main" };
        AddArrivingLanes(roads[bestA], g0);
        AddArrivingLanes(roads[bestB], g0);
        groups.Add(g0);

        var g1 = new SignalGroup { groupName = "Phase_1_Side" };
        for (int i = 0; i < roads.Count; i++)
        {
            if (i == bestA || i == bestB) continue;
            AddArrivingLanes(roads[i], g1);
        }
        if (g1.lanes.Count > 0) groups.Add(g1);
    }

    /// <summary>One group per road arm — fully sequential phases.</summary>
    void AutoAssignSequential(List<TrafficRoad> roads)
    {
        for (int i = 0; i < roads.Count; i++)
        {
            if (roads[i] == null) continue;
            var group = new SignalGroup { groupName = $"Phase_{i}_{roads[i].name}" };
            AddArrivingLanes(roads[i], group);
            if (group.lanes.Count > 0) groups.Add(group);
        }
    }

    /// <summary>TwoWay: all roads in one phase — both directions green together.</summary>
    void AutoAssignSinglePhase(List<TrafficRoad> roads)
    {
        var g = new SignalGroup { groupName = "Phase_0_All" };
        foreach (var road in roads)
            AddArrivingLanes(road, g);
        if (g.lanes.Count > 0) groups.Add(g);
    }

    /// <summary>
    /// Adds arriving lanes of this road to the signal group.
    /// Arriving = waypoints[last] closer to intersection centre than waypoints[0].
    /// Position-based — works regardless of forwardDirection or startNode/endNode swap.
    /// </summary>
    void AddArrivingLanes(TrafficRoad road, SignalGroup group)
    {
        if (road == null || intersectionNode == null) return;
        Vector3 centre = intersectionNode.transform.position;

        foreach (var lane in road.lanes)
        {
            if (lane?.path == null || lane.path.waypoints == null ||
                lane.path.waypoints.Count < 2) continue;

            Vector3 wp0   = lane.path.waypoints[0].position;
            Vector3 wpEnd = lane.path.waypoints[lane.path.waypoints.Count - 1].position;

            if (Vector3.Distance(wpEnd, centre) < Vector3.Distance(wp0, centre))
                group.lanes.Add(lane);
        }
    }

    /// <summary>Direction vector of this road arm pointing away from the intersection.</summary>
    Vector3 RoadDirFromIntersection(TrafficRoad road)
    {
        if (road == null || intersectionNode == null) return Vector3.forward;
        Vector3 nodePos = intersectionNode.transform.position;

        if (road.startNode != null && road.endNode != null)
        {
            bool startIsNear = Vector3.Distance(road.startNode.transform.position, nodePos) <
                               Vector3.Distance(road.endNode.transform.position,   nodePos);
            Vector3 farEnd = startIsNear ? road.endNode.transform.position
                                         : road.startNode.transform.position;
            return (farEnd - nodePos).normalized;
        }
        return road.transform.forward;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Runtime
    // ─────────────────────────────────────────────────────────────────────

    void Awake()  { BuildLookup(); }

    void Start()
    {
        SetAllRed();
        if (groups.Count > 0)
        {
            groups[activeGroup].state = SignalState.Green;
            phaseTimer = greenTime;
        }
    }

    void BuildLookup()
    {
        laneToGroup.Clear();
        for (int g = 0; g < groups.Count; g++)
            foreach (var lane in groups[g].lanes)
                if (lane != null) laneToGroup[lane] = g;
    }

    void SetAllRed() { foreach (var g in groups) g.state = SignalState.Red; }

    void Update()
    {
        if (groups.Count == 0) return;
        phaseTimer -= Time.deltaTime;
        if (phaseTimer > 0f) return;

        if (!inYellow && !inRedClear)
        {
            groups[activeGroup].state = SignalState.Yellow;
            inYellow = true; phaseTimer = yellowTime;
        }
        else if (inYellow)
        {
            groups[activeGroup].state = SignalState.Red;
            inYellow = false; inRedClear = true; phaseTimer = redTime;
        }
        else if (inRedClear)
        {
            inRedClear  = false;
            activeGroup = (activeGroup + 1) % groups.Count;
            groups[activeGroup].state = SignalState.Green;
            phaseTimer  = greenTime;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────

    public void ForceNextPhase()
    {
        if (groups.Count == 0) return;
        groups[activeGroup].state = SignalState.Red;
        inYellow = false; inRedClear = false;
        activeGroup = (activeGroup + 1) % groups.Count;
        groups[activeGroup].state = SignalState.Green;
        phaseTimer = greenTime;
    }

    public SignalState GetStateForLane(TrafficLane lane)
    {
        if (lane == null) return SignalState.Green;
        if (laneToGroup.TryGetValue(lane, out int g)) return groups[g].state;

        if (!missingLaneWarned.Contains(lane))
        {
            missingLaneWarned.Add(lane);
            Debug.LogWarning($"[TrafficSignal] Lane '{lane.name}' not in any SignalGroup on '{name}'. " +
                             "Run Auto Assign Lanes From Node.", this);
        }
        return SignalState.Green;
    }

    public bool IsLaneActive(TrafficLane lane)
    {
        var s = GetStateForLane(lane);
        return s == SignalState.Green || s == SignalState.Yellow;
    }

    public float TimeRemainingForLane(TrafficLane lane)
    {
        if (lane == null) return 0f;
        if (!laneToGroup.TryGetValue(lane, out int g)) return 0f;
        return g == activeGroup ? phaseTimer : 0f;
    }

    public void RefreshLookup() => BuildLookup();

    // ─────────────────────────────────────────────────────────────────────
    // Gizmos
    // ─────────────────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (!showGizmos) return;
        foreach (var group in groups)
        {
            Color c = group.state == SignalState.Green  ? Color.green
                    : group.state == SignalState.Yellow ? Color.yellow
                    : Color.red;

            foreach (var lane in group.lanes)
            {
                if (lane?.path == null || lane.path.waypoints.Count == 0) continue;
                var lastWP = lane.path.waypoints[lane.path.waypoints.Count - 1];
                Gizmos.color = c;
                Gizmos.DrawSphere(lastWP.position + Vector3.up * 0.5f, 1.0f);

                if (lane.path.waypoints.Count > 1)
                {
                    Vector3 dir = (lastWP.position -
                        lane.path.waypoints[lane.path.waypoints.Count - 2].position).normalized;
                    Gizmos.color = new Color(c.r, c.g, c.b, 0.3f);
                    Gizmos.DrawLine(lastWP.position, lastWP.position - dir * 8f);
                }
            }
        }
    }
}