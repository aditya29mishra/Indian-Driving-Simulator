using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// VehicleMap — local spatial minimap for one vehicle.
//
// Stores the closest vehicle in each of 11 zones around the ego car.
// All positions are in ego-local space:
//   lon = Vector3.Dot(toOther, ego.forward)   positive = ahead
//   lat = Vector3.Dot(toOther, ego.right)     positive = right
//
// Zones:
//   SAME LANE (lat within ±halfLane):
//     FrontClose   0m to +frontClose
//     FrontMid     +frontClose to +frontMid
//     FrontFar     +frontMid to +frontFar
//     RearClose    -rearClose to 0m
//     RearFar      -rearFar to -rearClose
//
//   LEFT LANE (lat between -halfLane and -laneAndHalf):
//     FrontLeft    lon > +besideHalf
//     BesideLeft   lon between -besideHalf and +besideHalf
//     RearLeft     lon < -besideHalf
//
//   RIGHT LANE (lat between +halfLane and +laneAndHalf):
//     FrontRight   lon > +besideHalf
//     BesideRight  lon between -besideHalf and +besideHalf
//     RearRight    lon < -besideHalf
//
//   CROSSING (intersection only — any heading conflict within radius):
//     Crossing     heading dot < 0.7 AND lon > 0 AND dist < crossRadius
//
// Zone boundaries are dynamic — scale with ego speed so at high speed
// the front zones stretch further. At low speed they contract.
//
// Populated by WorldPerception every tick. Zero physics calls.
// Pure dot products against LaneManager.registeredVehicles.
//
// Zone priority — WorldPerception applies masking during population:
//   PRIMARY (always filled, react immediately):
//     FrontClose, RearClose                — braking / being hit
//     BesideLeft, BesideRight              — sideswipe risk
//     FrontLeft, FrontRight                — lane change ahead safety
//     RearLeft, RearRight                  — lane change rear safety
//     Crossing                             — intersection yield
//
//   SECONDARY (filled only if primary zone on same axis is empty):
//     FrontMid  — only if FrontClose empty
//     FrontFar  — only if FrontClose AND FrontMid empty
//     RearFar   — only if RearClose empty
//
// All AI systems read from here. No AI logic lives in this class.
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
    public TrafficVehicle vehicle;    // closest vehicle in this zone (null if empty)
    public float          distance;   // world-space distance to vehicle
    public float          relSpeed;   // positive = closing, negative = opening
    public float          lon;        // longitudinal offset in ego space
    public float          lat;        // lateral offset in ego space

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
    public float HalfLane;       // same-lane lateral half-width
    public float LaneAndHalf;    // adjacent lane outer lateral bound
    public float BesideHalf;     // beside zone longitudinal half-extent
    public float FrontClose;     // same-lane front close limit
    public float FrontMid;       // same-lane front mid limit
    public float FrontFar;       // same-lane front far limit (scan cutoff)
    public float RearClose;      // same-lane rear close limit
    public float RearFar;        // same-lane rear far limit
    public float CrossRadius;    // intersection crossing scan radius

    public VehicleMap()
    {
        for (int i = 0; i < (int)MapZone.Count; i++)
            cells[i] = new MapCell();
    }

    // ── Public accessors ──────────────────────────────────────────────────

    public MapCell this[MapZone zone] => cells[(int)zone];

    public bool HasVehicle(MapZone zone) => cells[(int)zone].vehicle != null;

    public TrafficVehicle GetVehicle(MapZone zone) => cells[(int)zone].vehicle;

    /// <summary>
    /// Try to insert a vehicle into the appropriate zone.
    /// Keeps only the closest vehicle per zone.
    /// </summary>
    public void TryInsert(TrafficVehicle other, float dist, float relSpeed,
                          float lon, float lat, bool insideIntersection)
    {
        MapZone zone = Classify(lon, lat, dist, insideIntersection,
                                other.transform.forward,
                                // ego forward is handled by WorldPerception before calling
                                // this method — it passes already-transformed lon/lat
                                Vector3.zero);

        if (zone == MapZone.Count) return; // outside all zones

        var cell = cells[(int)zone];
        if (cell.vehicle == null || dist < cell.distance)
            cell.Set(other, dist, relSpeed, lon, lat);
    }

    /// <summary>
    /// Classify a vehicle into a zone based on its ego-local lon/lat.
    /// Returns MapZone.Count if the vehicle is outside all zones (ignore it).
    /// </summary>
    public MapZone Classify(float lon, float lat, float dist,
                             bool insideIntersection,
                             Vector3 otherForward, Vector3 egoForward)
    {
        float absLat = Mathf.Abs(lat);
        float absLon = Mathf.Abs(lon);

        // ── Crossing zone ─────────────────────────────────────────────────
        // Detects cars on conflicting paths — works BOTH inside intersection
        // and when approaching (both cars not yet inside but paths will conflict).
        // Conditions:
        //   dist < CrossRadius      — within conflict zone
        //   heading not parallel    — different travel directions
        //   not directly behind us  — lon > -5m (ahead or beside)
        if (dist < CrossRadius && lon > -5f)
        {
            float headingDot = Vector3.Dot(egoForward, otherForward);
            // Not parallel (same dir) AND not opposite (head-on same lane)
            // headingDot near 0 = perpendicular = crossing paths
            // headingDot between -0.5 and 0.7 = angled = potential crossing
            if (headingDot < 0.7f && headingDot > -0.85f)
                return MapZone.Crossing;
        }

        // ── Same lane band ────────────────────────────────────────────────
        // Inside intersection: widen the lateral detection to catch arc lane cars
        // which are physically offset from their original lane position.
        float effectiveHalfLane = insideIntersection ? HalfLane * 3f : HalfLane;

        if (absLat <= effectiveHalfLane)
        {
            if      (lon >= 0f         && lon < FrontClose) return MapZone.FrontClose;
            else if (lon >= FrontClose  && lon < FrontMid)   return MapZone.FrontMid;
            else if (lon >= FrontMid    && lon < FrontFar)   return MapZone.FrontFar;
            else if (lon <  0f         && lon > -RearClose)  return MapZone.RearClose;
            else if (lon <= -RearClose  && lon > -RearFar)   return MapZone.RearFar;
            return MapZone.Count; // beyond scan range
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

        return MapZone.Count; // beyond lateral scan range
    }

    /// <summary>Clear all cells. Called by WorldPerception before each update pass.</summary>
    public void Clear()
    {
        for (int i = 0; i < (int)MapZone.Count; i++)
            cells[i].Clear();
    }

    /// <summary>
    /// Update dynamic zone boundaries based on ego speed.
    /// Called once per tick by WorldPerception before populating cells.
    /// </summary>
    public void UpdateBoundaries(float speed, float laneWidth, bool isApproachingIntersection = false)
    {
        HalfLane     = laneWidth * 0.55f;
        LaneAndHalf  = laneWidth * 1.6f;
        BesideHalf   = Mathf.Max(5f,  speed * 0.4f);    // beside zone: 5m stopped, grows at speed
        FrontClose   = Mathf.Max(15f, speed * 1.5f);    // ~22m at 14m/s
        FrontMid     = Mathf.Max(40f, speed * 3.0f);    // ~42m at 14m/s
        FrontFar     = Mathf.Max(80f, speed * 5.0f);    // ~70m at 14m/s
        RearClose    = Mathf.Max(10f, speed * 0.8f);    // ~11m at 14m/s
        RearFar      = Mathf.Max(40f, speed * 2.0f);    // ~28m at 14m/s
        // Expand CrossRadius when approaching intersection so cars on perpendicular
        // roads are detected early enough to brake — not just when already inside.
        // At 14 m/s approaching: radius = max(18, 14*2.5) = 35m = 2.5s warning
        CrossRadius = isApproachingIntersection
            ? Mathf.Max(18f, speed * 2.5f)
            : 18f;
    }
}