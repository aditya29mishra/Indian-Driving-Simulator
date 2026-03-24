using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// WorldPerception — central perception system. Updates every vehicle's
// VehicleMap once per FixedUpdate tick.
//
// Lives on LaneManager (one instance per scene).
//
// VehicleProfile integration:
//   Zone boundaries now scale from VEHICLE WIDTH, not just lane width.
//   A bike (0.85m wide) has a narrow HalfLane — it sees itself fitting in
//   gaps a car cannot. This is what enables gap threading and lane splitting
//   to emerge naturally from perception, not from special-case logic.
//
//   canGapThread vehicles (bikes, rickshaws) get an additional classification
//   override: vehicles slightly ahead but laterally offset are reclassified
//   as Beside rather than FrontClose, so the bike sees them as gaps to thread
//   rather than blockers.
//
// Algorithm (unchanged):
//   For each ego vehicle A:
//     1. Clear A's map
//     2. Update A's map zone boundaries (now width-aware)
//     3. For each other vehicle B: dot-product classify into zone
//
// Cost: O(N²) dot products. Zero Physics API calls.
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

            float egoSpeed    = ego.CurrentSpeed;
            float laneWidth   = GetLaneWidth(ego);
            float vehicleWidth = GetVehicleWidth(ego);
            bool  inInter     = ego.IsInsideIntersection;

            bool isApproaching = !inInter && ego.context?.CurrentSignal != null;

            // Precompute ego gap-threading flag outside the inner loop
            bool egoCanGapThread = ego.context?.CanGapThread ?? false;

            // Step 1 — clear
            map.Clear();

            // Step 2 — update zone boundaries with vehicle width
            map.UpdateBoundaries(egoSpeed, laneWidth, vehicleWidth, isApproaching);

            // Step 3 — scan
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

                if (dist > map.FrontFar + 5f) continue;
                if (dist < 0.3f) continue;

                float lon = Vector3.Dot(toOther, egoForward);
                float lat = Vector3.Dot(toOther, egoRight);

                float relSpeed = lon >= 0f
                    ? egoSpeed - other.CurrentSpeed
                    : other.CurrentSpeed - egoSpeed;

                MapZone zone = map.Classify(lon, lat, dist, inInter,
                                            other.transform.forward, egoForward,
                                            egoCanGapThread);

                if (zone == MapZone.Count) continue;

                // Priority masking — skip secondary zones if primary is filled
                if (zone == MapZone.FrontMid &&
                    map.HasVehicle(MapZone.FrontClose)) continue;

                if (zone == MapZone.FrontFar &&
                    (map.HasVehicle(MapZone.FrontClose) ||
                     map.HasVehicle(MapZone.FrontMid))) continue;

                if (zone == MapZone.RearFar &&
                    map.HasVehicle(MapZone.RearClose)) continue;

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
        TrafficLane lane = vehicle.currentLane;
        if (lane?.road != null) return lane.road.laneWidth;
        return defaultLaneWidth;
    }

    /// <summary>
    /// Returns this vehicle's physical width from its VehicleProfile.
    /// Falls back to a car default (1.9m) for legacy vehicles without a profile.
    /// </summary>
    float GetVehicleWidth(TrafficVehicle vehicle)
    {
        return vehicle.context?.VehicleWidth ?? 1.9f;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Gizmos
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

        DrawZoneBoundaries(debugVehicle, map, egoPos, egoForward, egoRight);
        DrawOccupiedCells(map, egoPos);
    }

    void DrawZoneBoundaries(TrafficVehicle ego, VehicleMap map,
                             Vector3 egoPos, Vector3 fwd, Vector3 right)
    {
        float lw = map.LaneAndHalf;

        DrawLonLine(egoPos, fwd, right, map.FrontClose,  lw, new Color(0f, 1f, 0f, 0.3f));
        DrawLonLine(egoPos, fwd, right, map.FrontMid,    lw, new Color(0f, 0.7f, 0f, 0.2f));
        DrawLonLine(egoPos, fwd, right, map.FrontFar,    lw, new Color(0f, 0.4f, 0f, 0.15f));
        DrawLonLine(egoPos, fwd, right, -map.RearClose,  lw, new Color(1f, 0.5f, 0f, 0.3f));
        DrawLonLine(egoPos, fwd, right, -map.RearFar,    lw, new Color(1f, 0.3f, 0f, 0.2f));

        DrawLatLine(egoPos, fwd, right,  map.HalfLane,    map.FrontFar, new Color(0.5f, 0.5f, 1f, 0.3f));
        DrawLatLine(egoPos, fwd, right, -map.HalfLane,    map.FrontFar, new Color(0.5f, 0.5f, 1f, 0.3f));
        DrawLatLine(egoPos, fwd, right,  map.LaneAndHalf, map.FrontFar, new Color(0.3f, 0.3f, 0.8f, 0.2f));
        DrawLatLine(egoPos, fwd, right, -map.LaneAndHalf, map.FrontFar, new Color(0.3f, 0.3f, 0.8f, 0.2f));

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
        Color[] zoneColors = new Color[]
        {
            new Color(1f, 0f, 0f, 0.9f),
            new Color(1f, 0.5f, 0f, 0.8f),
            new Color(1f, 1f, 0f, 0.6f),
            new Color(1f, 0.3f, 0.3f, 0.8f),
            new Color(0.8f, 0.4f, 0.4f, 0.6f),
            new Color(0f, 0.8f, 1f, 0.9f),
            new Color(0f, 0.5f, 1f, 0.9f),
            new Color(0f, 0.3f, 0.8f, 0.7f),
            new Color(0f, 1f, 0.5f, 0.9f),
            new Color(0f, 0.8f, 0.3f, 0.9f),
            new Color(0f, 0.6f, 0.2f, 0.7f),
            new Color(1f, 0f, 1f, 0.9f),
        };

        for (int i = 0; i < (int)MapZone.Count; i++)
        {
            MapCell cell = map[(MapZone)i];
            if (cell.vehicle == null) continue;

            Vector3 vPos = cell.vehicle.transform.position + Vector3.up * 0.5f;

            Gizmos.color = zoneColors[i];
            Gizmos.DrawSphere(vPos, 0.8f);

            Gizmos.color = new Color(zoneColors[i].r, zoneColors[i].g, zoneColors[i].b, 0.4f);
            Gizmos.DrawLine(egoPos, vPos);

            UnityEditor.Handles.Label(vPos + Vector3.up * 1.2f,
                $"{(MapZone)i}\n{cell.distance:F1}m {cell.relSpeed:F1}m/s");
        }
    }
#endif
}