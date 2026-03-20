using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// WorldPerception — central perception system. Updates every vehicle's
// VehicleMap once per FixedUpdate tick.
//
// Lives on LaneManager (one instance per scene).
//
// Algorithm:
//   For each ego vehicle A:
//     1. Clear A's map
//     2. Update A's map zone boundaries based on A's current speed
//     3. For each other vehicle B in registeredVehicles:
//        a. Compute world distance — cull if beyond FrontFar (cheap early exit)
//        b. Transform B's position into A's local space (2 dot products)
//        c. Classify into zone and insert if closer than current cell occupant
//
// Cost: O(N²) dot products. At N=40: 1560 pairs × 4 float ops = 6240 ops/tick.
// Zero Physics API calls. Works on road lanes, arc lanes, intersections.
//
// Two-way awareness emerges naturally: A's scan sees B from A's perspective,
// B's scan sees A from B's perspective. No shared state between cars.
// Each car has a correct independent view of the world.
//
// Talks to: LaneManager (vehicle list), VehicleMap (per-car data), TrafficVehicle
// ─────────────────────────────────────────────────────────────────────────────

public class WorldPerception : MonoBehaviour
{
    [Header("References")]
    [Tooltip("LaneManager that holds registeredVehicles. Auto-found if null.")]
    public LaneManager laneManager;

    [Header("Settings")]
    [Tooltip("Default lane width used for lateral zone boundaries when road data unavailable.")]
    public float defaultLaneWidth = 3.5f;

    [Header("Debug")]
    [Tooltip("Draw zone gizmos for the selected vehicle in Scene view.")]
    public TrafficVehicle debugVehicle;
    public bool showGizmos = true;

    // ─────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────

    void Awake()
    {
        if (laneManager == null)
            laneManager = GetComponent<LaneManager>();
        if (laneManager == null)
            laneManager = FindObjectOfType<LaneManager>();
    }

    void FixedUpdate()
    {
        if (laneManager == null) return;
        UpdateAllMaps(laneManager.registeredVehicles);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Core — update all maps in one pass
    // ─────────────────────────────────────────────────────────────────────

    void UpdateAllMaps(List<TrafficVehicle> vehicles)
    {
        if (vehicles == null || vehicles.Count < 2) return;

        int count = vehicles.Count;

        for (int a = 0; a < count; a++)
        {
            TrafficVehicle ego = vehicles[a];
            if (ego == null) continue;

            VehicleMap map = ego.Map;
            if (map == null) continue;

            float egoSpeed  = ego.CurrentSpeed;
            float laneWidth = GetLaneWidth(ego);
            bool  inInter   = ego.IsInsideIntersection;

            // Approaching = has a signal assigned and not yet inside
            // Used to expand CrossRadius so perpendicular cars detected early
            bool isApproaching = !inInter &&
                                  ego.context?.CurrentSignal != null;

            // Step 1 — clear previous frame data
            map.Clear();

            // Step 2 — update dynamic zone boundaries for this ego + speed
            map.UpdateBoundaries(egoSpeed, laneWidth, isApproaching);

            // Step 3 — scan all other vehicles
            Vector3 egoPos     = ego.transform.position;
            Vector3 egoForward = ego.transform.forward;
            Vector3 egoRight   = ego.transform.right;

            for (int b = 0; b < count; b++)
            {
                if (b == a) continue;

                TrafficVehicle other = vehicles[b];
                if (other == null) continue;

                Vector3 toOther = other.transform.position - egoPos;
                float   dist    = toOther.magnitude;

                // Early cull — beyond max detection range
                if (dist > map.FrontFar + 5f) continue;
                if (dist < 0.3f) continue;

                // Transform into ego-local space
                float lon = Vector3.Dot(toOther, egoForward);
                float lat = Vector3.Dot(toOther, egoRight);

                // Relative speed: positive = closing
                float relSpeed = lon >= 0f
                    ? egoSpeed - other.CurrentSpeed
                    : other.CurrentSpeed - egoSpeed;

                // Classify into zone
                MapZone zone = map.Classify(lon, lat, dist, inInter,
                                            other.transform.forward, egoForward);

                if (zone == MapZone.Count) continue;

                // ── Priority masking — skip secondary zones if primary is filled ──
                // Forward axis: FrontClose > FrontMid > FrontFar
                // Rear axis:    RearClose  > RearFar
                // Primary zones (Beside, FrontLeft/Right, RearLeft/Right, Crossing)
                // are always populated — no masking applied.
                if (zone == MapZone.FrontMid &&
                    map.HasVehicle(MapZone.FrontClose)) continue;

                if (zone == MapZone.FrontFar &&
                    (map.HasVehicle(MapZone.FrontClose) ||
                     map.HasVehicle(MapZone.FrontMid))) continue;

                if (zone == MapZone.RearFar &&
                    map.HasVehicle(MapZone.RearClose)) continue;

                // Insert — keep closest vehicle per zone
                MapCell cell = map[zone];
                if (cell.vehicle == null || dist < cell.distance)
                    cell.Set(other, dist, relSpeed, lon, lat);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    float GetLaneWidth(TrafficVehicle vehicle)
    {
        // Try to read from current lane's road
        TrafficLane lane = vehicle.currentLane;
        if (lane?.road != null) return lane.road.laneWidth;
        return defaultLaneWidth;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Gizmos — visualise one vehicle's map in Scene view
    // ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (!showGizmos || debugVehicle == null) return;

        VehicleMap map = debugVehicle.Map;
        if (map == null) return;

        Vector3 egoPos     = debugVehicle.transform.position + Vector3.up * 0.5f;
        Vector3 egoForward = debugVehicle.transform.forward;
        Vector3 egoRight   = debugVehicle.transform.right;

        // Draw zone boundaries as lines in ego space
        DrawZoneBoundaries(debugVehicle, map, egoPos, egoForward, egoRight);

        // Draw occupied cells
        DrawOccupiedCells(map, egoPos);
    }

    void DrawZoneBoundaries(TrafficVehicle ego, VehicleMap map,
                             Vector3 egoPos, Vector3 fwd, Vector3 right)
    {
        float lw = map.LaneAndHalf;

        // Same lane forward zones
        DrawLonLine(egoPos, fwd, right, map.FrontClose,  lw, new Color(0f, 1f, 0f, 0.3f));
        DrawLonLine(egoPos, fwd, right, map.FrontMid,    lw, new Color(0f, 0.7f, 0f, 0.2f));
        DrawLonLine(egoPos, fwd, right, map.FrontFar,    lw, new Color(0f, 0.4f, 0f, 0.15f));

        // Rear zones
        DrawLonLine(egoPos, fwd, right, -map.RearClose, lw, new Color(1f, 0.5f, 0f, 0.3f));
        DrawLonLine(egoPos, fwd, right, -map.RearFar,   lw, new Color(1f, 0.3f, 0f, 0.2f));

        // Lateral bands
        DrawLatLine(egoPos, fwd, right,  map.HalfLane,    map.FrontFar, new Color(0.5f, 0.5f, 1f, 0.3f));
        DrawLatLine(egoPos, fwd, right, -map.HalfLane,    map.FrontFar, new Color(0.5f, 0.5f, 1f, 0.3f));
        DrawLatLine(egoPos, fwd, right,  map.LaneAndHalf, map.FrontFar, new Color(0.3f, 0.3f, 0.8f, 0.2f));
        DrawLatLine(egoPos, fwd, right, -map.LaneAndHalf, map.FrontFar, new Color(0.3f, 0.3f, 0.8f, 0.2f));

        // Crossing radius
        Gizmos.color = new Color(1f, 0f, 0f, 0.15f);
        Gizmos.DrawWireSphere(egoPos, map.CrossRadius);
    }

    void DrawLonLine(Vector3 origin, Vector3 fwd, Vector3 right,
                     float lon, float halfLat, Color col)
    {
        Gizmos.color = col;
        Vector3 centre = origin + fwd * lon;
        Gizmos.DrawLine(centre - right * halfLat, centre + right * halfLat);
    }

    void DrawLatLine(Vector3 origin, Vector3 fwd, Vector3 right,
                     float lat, float halfLon, Color col)
    {
        Gizmos.color = col;
        Vector3 centre = origin + right * lat;
        Gizmos.DrawLine(centre - fwd * halfLon * 0.5f, centre + fwd * halfLon);
    }

    void DrawOccupiedCells(VehicleMap map, Vector3 egoPos)
    {
        // Colour per zone type
        Color[] zoneColors = new Color[]
        {
            new Color(1f, 0f, 0f, 0.9f),    // FrontClose  — red, danger
            new Color(1f, 0.5f, 0f, 0.8f),  // FrontMid    — orange
            new Color(1f, 1f, 0f, 0.6f),    // FrontFar    — yellow
            new Color(1f, 0.3f, 0.3f, 0.8f),// RearClose   — pink
            new Color(0.8f, 0.4f, 0.4f, 0.6f),// RearFar   — light red
            new Color(0f, 0.8f, 1f, 0.9f),  // FrontLeft   — cyan
            new Color(0f, 0.5f, 1f, 0.9f),  // BesideLeft  — blue
            new Color(0f, 0.3f, 0.8f, 0.7f),// RearLeft    — dark blue
            new Color(0f, 1f, 0.5f, 0.9f),  // FrontRight  — green-cyan
            new Color(0f, 0.8f, 0.3f, 0.9f),// BesideRight — green
            new Color(0f, 0.6f, 0.2f, 0.7f),// RearRight   — dark green
            new Color(1f, 0f, 1f, 0.9f),    // Crossing    — magenta
        };

        for (int i = 0; i < (int)MapZone.Count; i++)
        {
            MapCell cell = map[(MapZone)i];
            if (cell.vehicle == null) continue;

            Vector3 vPos = cell.vehicle.transform.position + Vector3.up * 0.5f;

            // Draw sphere on the detected vehicle
            Gizmos.color = zoneColors[i];
            Gizmos.DrawSphere(vPos, 0.8f);

            // Draw line from ego to detected vehicle
            Gizmos.color = new Color(zoneColors[i].r, zoneColors[i].g, zoneColors[i].b, 0.4f);
            Gizmos.DrawLine(egoPos, vPos);

            // Label with zone name and distance
            UnityEditor.Handles.Label(vPos + Vector3.up * 1.2f,
                $"{(MapZone)i}\n{cell.distance:F1}m {cell.relSpeed:F1}m/s");
        }
    }
#endif
}