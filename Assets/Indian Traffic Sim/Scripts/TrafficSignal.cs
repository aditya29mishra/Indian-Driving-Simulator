using System.Collections.Generic;
using UnityEngine;

public class TrafficSignal : MonoBehaviour
{
    public enum SignalState { Green, Yellow, Red }

    public enum TurnType { Straight, Left, Right, All }

    [System.Serializable]
    public class SignalGroup
    {
        public string groupName = "Group";
        public TurnType turnType = TurnType.All;
        public List<TrafficLane> lanes = new List<TrafficLane>();

        [HideInInspector] public SignalState state = SignalState.Red;
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

    // Called by TrafficVehicle every frame
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

    // Returns true if the lane's group is currently active (green or yellow)
    public bool IsLaneActive(TrafficLane lane)
    {
        SignalState s = GetStateForLane(lane);
        return s == SignalState.Green || s == SignalState.Yellow;
    }

    // How much time is left in the current phase for a lane
    public float TimeRemainingForLane(TrafficLane lane)
    {
        if (lane == null) return 0f;
        if (!laneToGroup.TryGetValue(lane, out int g)) return 0f;
        return g == activeGroup ? phaseTimer : 0f;
    }

    // Rebuild lookup at edit time if groups change
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