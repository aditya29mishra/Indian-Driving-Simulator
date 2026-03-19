using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficLane — lane data container, nextPaths for intersection turns.
//
// Terminal lane gizmo: any lane with no nextPaths AND no nextLanes is a
// dead-end — vehicle will despawn here. A cyan sphere + stop-bar line is
// drawn at the path end so the road network topology is visible in the editor.
//
// Talks to: TrafficPath (path), TrafficRoad (road), TrafficVehicle (assignment)
// ─────────────────────────────────────────────────────────────────────────────

public class TrafficLane : MonoBehaviour
{
    /// <summary>Represents a path and its target lane for turn navigation.</summary>
    [System.Serializable]
    public class LanePath
    {
        public TrafficPath path;
        public TrafficLane targetLane;
    }

    public TrafficRoad road;
    public int   laneIndex      = 0;
    public float laneWidth      = 3.5f;
    public bool  forwardDirection = true;
    public TrafficPath path;

    public List<TrafficLane>  nextLanes = new List<TrafficLane>();
    public List<LanePath>     nextPaths = new List<LanePath>();

    // ─────────────────────────────────────────────────────────────────────
    // Terminal lane detection
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when this lane has no exits of any kind — no intersection
    /// turn arcs (nextPaths) and no straight-through connections (nextLanes).
    /// Vehicles entering this lane will despawn at its end (D1 or D2 despawn).
    /// </summary>
    public bool IsTerminal
    {
        get
        {
            bool noNextPaths = nextPaths == null || nextPaths.Count == 0;
            bool noNextLanes = nextLanes == null || nextLanes.Count == 0;
            return noNextPaths && noNextLanes;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Gizmos
    // ─────────────────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (!IsTerminal) return;
        if (path == null || path.waypoints == null || path.waypoints.Count == 0) return;

        Transform lastWP = path.waypoints[path.waypoints.Count - 1];
        if (lastWP == null) return;

        Vector3 tipPos = lastWP.position;

        // ── Terminal sphere — cyan, distinct from the yellow TrafficNode spheres ──
        Gizmos.color = new Color(0.0f, 0.9f, 1.0f, 0.9f); // cyan
        Gizmos.DrawSphere(tipPos + Vector3.up * 0.4f, 0.55f);

        // ── Stop bar — perpendicular line across the lane end ──────────────
        // Compute the lane direction from the last two waypoints
        Vector3 laneDir = Vector3.forward;
        if (path.waypoints.Count >= 2)
        {
            Transform prevWP = path.waypoints[path.waypoints.Count - 2];
            if (prevWP != null)
                laneDir = (lastWP.position - prevWP.position).normalized;
        }

        // Perpendicular in XZ plane (ignore Y so bar stays horizontal)
        Vector3 flat    = new Vector3(laneDir.x, 0f, laneDir.z).normalized;
        Vector3 perpDir = new Vector3(-flat.z, 0f, flat.x);

        float barHalfWidth = laneWidth * 0.5f;
        Vector3 barCentre  = tipPos + Vector3.up * 0.05f;
        Vector3 barLeft    = barCentre - perpDir * barHalfWidth;
        Vector3 barRight   = barCentre + perpDir * barHalfWidth;

        Gizmos.color = new Color(0.0f, 0.9f, 1.0f, 0.6f);
        Gizmos.DrawLine(barLeft, barRight);

        // ── Tick marks at bar ends ─────────────────────────────────────────
        Gizmos.DrawLine(barLeft,  barLeft  + Vector3.up * 0.4f);
        Gizmos.DrawLine(barRight, barRight + Vector3.up * 0.4f);

#if UNITY_EDITOR
        // Label so you can confirm lane name + that it really has no exits
        UnityEditor.Handles.Label(tipPos + Vector3.up * 1.2f,
            $"{name}\n[terminal]");
#endif
    }
}