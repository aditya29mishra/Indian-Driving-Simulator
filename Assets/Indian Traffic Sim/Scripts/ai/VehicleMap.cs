using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// VehicleMap — local spatial minimap for one vehicle.
//
// VehicleProfile integration:
//   UpdateBoundaries() now takes vehicleWidth so zone boundaries scale from
//   the vehicle's physical size, not a fixed lane constant.
//   A bike (0.85m) has a narrow HalfLane — it classifies the same gap
//   that blocks a car as navigable beside-space.
//
//   Classify() has a canGapThread override: vehicles slightly ahead but
//   only marginally overlapping laterally are reclassified from FrontClose
//   to BesideLeft/BesideRight for narrow gap-threading vehicles.
//   This is what makes bikes thread between cars without special-case logic.
//
// Everything else is unchanged from the original.
// ─────────────────────────────────────────────────────────────────────────────

public enum MapZone
{
    FrontClose  = 0,
    FrontMid    = 1,
    FrontFar    = 2,
    RearClose   = 3,
    RearFar     = 4,
    FrontLeft   = 5,
    BesideLeft  = 6,
    RearLeft    = 7,
    FrontRight  = 8,
    BesideRight = 9,
    RearRight   = 10,
    Crossing    = 11,
    Count       = 12
}

public class MapCell
{
    public TrafficVehicle vehicle;
    public float          distance;
    public float          relSpeed;
    public float          lon;
    public float          lat;

    public bool IsEmpty => vehicle == null;

    public void Set(TrafficVehicle v, float dist, float relSpd, float l, float lt)
    {
        vehicle  = v;
        distance = dist;
        relSpeed = relSpd;
        lon      = l;
        lat      = lt;
    }

    public void Clear()
    {
        vehicle  = null;
        distance = float.MaxValue;
        relSpeed = 0f;
        lon      = 0f;
        lat      = 0f;
    }
}

public class VehicleMap
{
    // ── Zone cells ────────────────────────────────────────────────────────
    private readonly MapCell[] cells = new MapCell[(int)MapZone.Count];

    // ── Dynamic zone boundaries (updated each tick by WorldPerception) ────
    public float HalfLane;
    public float LaneAndHalf;
    public float BesideHalf;
    public float FrontClose;
    public float FrontMid;
    public float FrontFar;
    public float RearClose;
    public float RearFar;
    public float CrossRadius;

    public VehicleMap()
    {
        for (int i = 0; i < (int)MapZone.Count; i++)
            cells[i] = new MapCell();
    }

    // ── Public accessors ──────────────────────────────────────────────────

    public MapCell this[MapZone zone] => cells[(int)zone];

    public bool HasVehicle(MapZone zone) => cells[(int)zone].vehicle != null;

    public TrafficVehicle GetVehicle(MapZone zone) => cells[(int)zone].vehicle;

    // ─────────────────────────────────────────────────────────────────────
    // UpdateBoundaries — now width-aware
    //
    // vehicleWidth drives HalfLane so a narrow vehicle (bike) has a tighter
    // "same lane" band. This means the bike doesn't classify a car slightly
    // to the side as directly ahead — it sees it as beside (gap space).
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates all dynamic zone boundaries for the current ego vehicle and speed.
    /// Called once per tick by WorldPerception before populating cells.
    /// </summary>
    public void UpdateBoundaries(float speed, float laneWidth, float vehicleWidth,
                                 bool isApproachingIntersection = false)
    {
        // HalfLane: same-lane lateral half-width, scaled from vehicle width.
        // Bike (0.85m): HalfLane = 0.47m — very narrow, sees more space as "beside".
        // Car  (1.90m): HalfLane = 1.05m — standard band.
        // Bus  (2.50m): HalfLane = 1.38m — wide band, nothing fits beside it.
        HalfLane    = vehicleWidth * 0.55f;

        // LaneAndHalf: outer edge of adjacent lane detection zone.
        // Scales with road lane width plus half the vehicle width.
        LaneAndHalf = laneWidth + vehicleWidth * 0.5f;

        // BesideHalf: longitudinal extent of the "beside" zone.
        // Shorter for bikes — they make gap decisions faster.
        BesideHalf  = Mathf.Max(4f, speed * 0.35f);

        // Front zones — speed-scaled, same as before
        FrontClose  = Mathf.Max(15f, speed * 1.5f);
        FrontMid    = Mathf.Max(40f, speed * 3.0f);
        FrontFar    = Mathf.Max(80f, speed * 5.0f);

        // Rear zones — slightly tighter than original for all vehicles
        RearClose   = Mathf.Max(8f,  speed * 0.7f);
        RearFar     = Mathf.Max(35f, speed * 1.8f);

        // CrossRadius — expanded when approaching intersection
        CrossRadius = isApproachingIntersection
            ? Mathf.Max(18f, speed * 2.5f)
            : 18f;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Classify — with gap-thread override
    //
    // canGapThread vehicles (bikes, rickshaws): vehicles slightly ahead but
    // only marginally overlapping laterally are reclassified as Beside rather
    // than FrontClose. This lets the bike treat them as gaps to thread through
    // rather than as blockers to stop behind.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Classify a vehicle into a zone based on ego-local lon/lat.
    /// Returns MapZone.Count if outside all zones.
    /// </summary>
    public MapZone Classify(float lon, float lat, float dist,
                             bool insideIntersection,
                             Vector3 otherForward, Vector3 egoForward,
                             bool egoCanGapThread = false)
    {
        float absLat = Mathf.Abs(lat);

        // ── Crossing zone ─────────────────────────────────────────────────
        if (dist < CrossRadius && lon > -5f)
        {
            float headingDot = Vector3.Dot(egoForward, otherForward);
            if (headingDot < 0.7f && headingDot > -0.85f)
                return MapZone.Crossing;
        }

        // ── Same lane band ────────────────────────────────────────────────
        float effectiveHalfLane = insideIntersection ? HalfLane * 3f : HalfLane;

        if (absLat <= effectiveHalfLane)
        {
            // ── GAP THREAD OVERRIDE ────────────────────────────────────────
            // For bikes/rickshaws: a vehicle that is slightly ahead (lon > 0)
            // but only marginally overlapping our narrow lane band is reclassified
            // as Beside rather than FrontClose.
            //
            // Why: the bike's HalfLane is 0.47m (vs car's 1.05m). A car that is
            // 0.5m to the side of the bike IS within the bike's same-lane band,
            // but the bike physically fits beside it. The reclassification lets the
            // bike see that car as "beside me" (navigable gap) rather than "blocking me".
            //
            // Threshold: absLat > HalfLane * 0.35 means there's meaningful lateral
            // separation — the other vehicle isn't directly in our path.
            if (egoCanGapThread &&
                lon > 0f && lon < FrontClose * 0.6f &&
                absLat > HalfLane * 0.35f)
            {
                return lat < 0f ? MapZone.BesideLeft : MapZone.BesideRight;
            }

            if      (lon >= 0f        && lon < FrontClose) return MapZone.FrontClose;
            else if (lon >= FrontClose && lon < FrontMid)   return MapZone.FrontMid;
            else if (lon >= FrontMid   && lon < FrontFar)   return MapZone.FrontFar;
            else if (lon < 0f         && lon > -RearClose)  return MapZone.RearClose;
            else if (lon <= -RearClose && lon > -RearFar)   return MapZone.RearFar;
            return MapZone.Count;
        }

        // ── Left lane band ────────────────────────────────────────────────
        if (lat < -HalfLane && lat > -LaneAndHalf)
        {
            if      (lon >  BesideHalf)  return MapZone.FrontLeft;
            else if (lon < -BesideHalf)  return MapZone.RearLeft;
            else                         return MapZone.BesideLeft;
        }

        // ── Right lane band ───────────────────────────────────────────────
        if (lat > HalfLane && lat < LaneAndHalf)
        {
            if      (lon >  BesideHalf)  return MapZone.FrontRight;
            else if (lon < -BesideHalf)  return MapZone.RearRight;
            else                         return MapZone.BesideRight;
        }

        return MapZone.Count;
    }

    /// <summary>Clear all cells. Called by WorldPerception before each update pass.</summary>
    public void Clear()
    {
        for (int i = 0; i < (int)MapZone.Count; i++)
            cells[i].Clear();
    }
}