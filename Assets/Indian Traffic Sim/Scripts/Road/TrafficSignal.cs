using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficSignal — signal group cycling, per-lane state lookup, yellow/red phase timing
//
// Cycles through signal groups with green/yellow/red phases. Provides per-lane
// signal state lookups for vehicles. Builds lane-to-group dictionary in Awake
// to ensure availability before other scripts initialize.
//
// Talks to: TrafficVehicle (provides GetStateForLane), TrafficLane (lane references)
// ─────────────────────────────────────────────────────────────────────────────

public class TrafficSignal : MonoBehaviour
{
    /// <summary>Possible states of a traffic signal.</summary>
    public enum SignalState { Green, Yellow, Red }

    /// <summary>Types of turns a signal group can control.</summary>
    public enum TurnType { Straight, Left, Right, All }

    /// <summary>Represents a group of lanes controlled by the same signal phase.</summary>
    [System.Serializable]
    public class SignalGroup
    {
        /// <summary>Name of the signal group.</summary>
        public string groupName = "Group";
        /// <summary>Type of turns this group controls.</summary>
        public TurnType turnType = TurnType.All;
        /// <summary>List of lanes in this group.</summary>
        public List<TrafficLane> lanes = new List<TrafficLane>();

        [HideInInspector] 
        /// <summary>Current state of this signal group.</summary>
        public SignalState state = SignalState.Red;
    }

    [Header("Signal Groups")]
    public List<SignalGroup> groups = new List<SignalGroup>();

    [Header("Timing (seconds)")]
    public float greenTime  = 25f;
    public float yellowTime = 3f;
    public float redTime    = 2f;

    [Header("Debug")]
    public bool showGizmos = true;

    // Internal
    private int   activeGroup  = 0;
    private float phaseTimer   = 0f;
    private bool  inYellow     = false;
    private bool  inRedClear   = false;

    // Fast lookup: lane → group index
    private Dictionary<TrafficLane, int> laneToGroup = new Dictionary<TrafficLane, int>();

    void Awake()
    {
        // Build lookup in Awake so it's ready before any other script's Start
        // runs (e.g. VehicleSpawner.Start calls FindSignalForLane which needs
        // the dictionary populated, and AssignLane reads GetStateForLane).
        BuildLookup();
    }

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
                if (lane != null)
                    laneToGroup[lane] = g;
    }

    void SetAllRed()
    {
        foreach (var g in groups)
            g.state = SignalState.Red;
    }

    void Update()
    {
        if (groups.Count == 0) return;

        phaseTimer -= Time.deltaTime;

        if (phaseTimer > 0f) return;

        if (!inYellow && !inRedClear)
        {
            // Green expired — go yellow
            groups[activeGroup].state = SignalState.Yellow;
            inYellow  = true;
            phaseTimer = yellowTime;
        }
        else if (inYellow)
        {
            // Yellow expired — go red, start red clear
            groups[activeGroup].state = SignalState.Red;
            inYellow   = false;
            inRedClear = true;
            phaseTimer  = redTime;
        }
        else if (inRedClear)
        {
            // Red clear expired — advance to next group
            inRedClear = false;
            activeGroup = (activeGroup + 1) % groups.Count;
            groups[activeGroup].state = SignalState.Green;
            phaseTimer = greenTime;
        }
    }

    /// <summary>Forces the signal to advance to the next phase immediately.</summary>
    public void ForceNextPhase()
    {
        if (groups.Count == 0) return;
        groups[activeGroup].state = SignalState.Red;
        inYellow   = false;
        inRedClear = false;
        activeGroup = (activeGroup + 1) % groups.Count;
        groups[activeGroup].state = SignalState.Green;
        phaseTimer = greenTime;
    }

    /// <summary>Gets the current signal state for the specified lane.</summary>
    /// <param name="lane">The lane to check.</param>
    /// <returns>The signal state for the lane.</returns>
    public SignalState GetStateForLane(TrafficLane lane)
    {
        if (lane == null) return SignalState.Green;
        if (laneToGroup.TryGetValue(lane, out int g)) return groups[g].state;

        // Lane not registered in any group — this is a setup error, not intentional.
        // Log once so it shows in the console without spamming every frame.
        if (!missingLaneWarned.Contains(lane))
        {
            missingLaneWarned.Add(lane);
            Debug.LogWarning($"[TrafficSignal] Lane '{lane.name}' is not in any SignalGroup on '{name}'. " +
                             $"It will always read Green. Check your signal group setup.", this);
        }
        return SignalState.Green;
    }

    private readonly HashSet<TrafficLane> missingLaneWarned = new HashSet<TrafficLane>();

    /// <summary>Checks if the specified lane's signal group is currently active (green or yellow).</summary>
    /// <param name="lane">The lane to check.</param>
    /// <returns>True if the lane is active.</returns>
    public bool IsLaneActive(TrafficLane lane)
    {
        SignalState s = GetStateForLane(lane);
        return s == SignalState.Green || s == SignalState.Yellow;
    }

    /// <summary>Gets the time remaining in the current phase for the specified lane.</summary>
    /// <param name="lane">The lane to check.</param>
    /// <returns>Time remaining in seconds.</returns>
    public float TimeRemainingForLane(TrafficLane lane)
    {
        if (lane == null) return 0f;
        if (!laneToGroup.TryGetValue(lane, out int g)) return 0f;
        return g == activeGroup ? phaseTimer : 0f;
    }

    /// <summary>Rebuilds the lane-to-group lookup dictionary.</summary>
    public void RefreshLookup() => BuildLookup();

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
                if (lane == null || lane.path == null || lane.path.waypoints.Count == 0) continue;

                // Draw a colored sphere at the lane end (stop line position)
                Transform lastWP = lane.path.waypoints[lane.path.waypoints.Count - 1];
                Gizmos.color = c;
                Gizmos.DrawSphere(lastWP.position + Vector3.up * 0.5f, 1.0f);

                // Draw a line from stop line back 8m to show braking zone
                if (lane.path.waypoints.Count > 1)
                {
                    Vector3 dir = (lastWP.position - lane.path.waypoints[lane.path.waypoints.Count - 2].position).normalized;
                    Gizmos.color = new Color(c.r, c.g, c.b, 0.3f);
                    Gizmos.DrawLine(lastWP.position, lastWP.position - dir * 8f);
                }
            }
        }
    }
}