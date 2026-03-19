using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficVehicle (partial) — Gizmo visualisation
//
// OnDrawGizmos        — always visible, minimal. One sphere per vehicle.
//   Priority: yield (yellow) > signal stop (red) > following (orange) > free (green)
//
// OnDrawGizmosSelected — full detail, only the clicked vehicle.
//   Forward corridor, look-ahead, leader+gap, crossing conflicts,
//   NeighbourMap (beside/rear/front), destination, stop line, perception radius.
// ─────────────────────────────────────────────────────────────────────────────

public partial class TrafficVehicle
{
    // ── Always-on: one sphere per vehicle, colour = most critical state ───
    void OnDrawGizmos()
    {
        if (ctx == null) return;

        Color c;
        if (intersectionPriority != null && intersectionPriority.ShouldYieldAtEntry)
            c = Color.yellow;
        else if (idm != null && idm.MustStopForSignal())
            c = Color.red;
        else if (ctx.LeaderVehicle != null)
            c = new Color(1f, 0.55f, 0f);
        else
            c = Color.green;

        Gizmos.color = c;
        Gizmos.DrawSphere(transform.position + Vector3.up * 2.2f, 0.35f);
    }

    // ── Selected only: full debug detail ─────────────────────────────────
    void OnDrawGizmosSelected()
    {
        if (ctx == null) return;

        Vector3 origin = transform.position + Vector3.up * 0.5f;

        // Forward corridor
        Gizmos.color = ctx.LeaderVehicle != null ? Color.red : Color.green;
        Gizmos.DrawLine(origin, origin + transform.forward * 28f);

        // Look-ahead point
        if (Application.isPlaying && ctx.CurrentPath != null)
        {
            Vector3 la = ctx.CurrentPath.GetPointAtDistance(
                Mathf.Min(ctx.DistanceTravelled + 6f + CurrentSpeed * 0.3f,
                          ctx.CurrentPath.TotalLength));
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(origin, la + Vector3.up);
            Gizmos.DrawWireSphere(la + Vector3.up, 0.4f);
        }

        // Leader line + gap colour at midpoint
        if (ctx.LeaderVehicle != null)
        {
            Vector3 lp = ctx.LeaderVehicle.transform.position + Vector3.up;
            float gap = Vector3.Distance(transform.position,
                        ctx.LeaderVehicle.transform.position) - ctx.VehicleLength;
            Gizmos.color = new Color(1f, 0.5f, 0f);
            Gizmos.DrawLine(origin, lp);
            Gizmos.color = gap > ctx.TimeHeadway * CurrentSpeed ? Color.green
                         : gap > ctx.MinimumGap                  ? Color.yellow : Color.red;
            Gizmos.DrawWireSphere((origin + lp) * 0.5f, 0.25f);
        }

        // Crossing conflicts
        var crossing = perception?.Map?.crossingPaths;
        if (crossing != null)
        {
            Gizmos.color = Color.magenta;
            foreach (var v in crossing)
            {
                if (v == null) continue;
                Gizmos.DrawLine(origin, v.transform.position + Vector3.up);
                Gizmos.DrawWireSphere(v.transform.position + Vector3.up * 3f, 0.45f);
            }
        }

        // NeighbourMap
        var map = perception?.Map;
        if (map != null)
        {
            Gizmos.color = new Color(1f, 0.6f, 0f);
            if (map.besideLeft  != null) Gizmos.DrawLine(origin, map.besideLeft.transform.position  + Vector3.up);
            if (map.besideRight != null) Gizmos.DrawLine(origin, map.besideRight.transform.position + Vector3.up);

            if (map.rearLeft != null)
            {
                Gizmos.color = map.rearLeft.CurrentSpeed - CurrentSpeed > 2f ? Color.red : new Color(1f, 0.4f, 0.4f);
                Gizmos.DrawLine(origin, map.rearLeft.transform.position + Vector3.up);
            }
            if (map.rearRight != null)
            {
                Gizmos.color = map.rearRight.CurrentSpeed - CurrentSpeed > 2f ? Color.red : new Color(1f, 0.4f, 0.4f);
                Gizmos.DrawLine(origin, map.rearRight.transform.position + Vector3.up);
            }

            Gizmos.color = new Color(0.75f, 0.75f, 0.75f, 0.35f);
            if (map.frontLeft  != null) Gizmos.DrawLine(origin, map.frontLeft.transform.position  + Vector3.up);
            if (map.frontRight != null) Gizmos.DrawLine(origin, map.frontRight.transform.position + Vector3.up);
        }

        // Destination
        if (ctx.DestNode != null)
        {
            float d = Vector3.Distance(transform.position, ctx.DestNode.transform.position);
            Gizmos.color = new Color(0.4f, 0.75f, 1f, Mathf.Clamp01(d / 40f));
            Gizmos.DrawLine(origin, ctx.DestNode.transform.position + Vector3.up);
            Gizmos.DrawWireSphere(ctx.DestNode.transform.position + Vector3.up * 2f, 1.2f);
        }

        // Stop line
        if (ctx.CurrentLane?.path?.waypoints != null && ctx.CurrentLane.path.waypoints.Count > 0)
        {
            Vector3 sl = ctx.CurrentLane.path.waypoints[ctx.CurrentLane.path.waypoints.Count - 1].position;
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.6f);
            Gizmos.DrawWireCube(sl + Vector3.up * 0.1f, new Vector3(3f, 0.1f, 0.5f));
        }

        // Perception radius
        Gizmos.color = new Color(0.3f, 0.8f, 0.3f, 0.12f);
        Gizmos.DrawWireSphere(transform.position, 22f);
    }
}