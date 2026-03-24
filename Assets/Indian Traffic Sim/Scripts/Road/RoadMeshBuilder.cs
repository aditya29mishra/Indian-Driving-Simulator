using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// RoadMeshBuilder — procedural mesh generation for TrafficRoad.
//
// Generates four meshes from the road spline:
//   1. Road surface       — flat asphalt quad-strip following the spline
//   2. Lane markings      — solid/dashed painted lines on top of surface
//   3. Kerbs              — raised edge strips on both sides, with side walls
//   4. Intersection fill  — convex polygon at junction centre
//
// All four are independently togglable. MeshCollider always on for surface.
// All meshes rebuild live when any control point is dragged.
//
// Mesh coordinate convention:
//   All verts are in world space. Each child GO has identity local transform.
//   UV.x = 0 (left) → 1 (right) across road width.
//   UV.y = arc-length / uvTileLength  (tiles along the road).
//
// Talks to: TrafficRoad (spline + road config), TrafficIntersectionNode
// ─────────────────────────────────────────────────────────────────────────────

[RequireComponent(typeof(TrafficRoad))]
[ExecuteAlways]
public class RoadMeshBuilder : MonoBehaviour
{
    // ── Sections ──────────────────────────────────────────────────────────
    [Header("Mesh Sections")]
    public bool buildRoadSurface  = true;
    public bool buildLaneMarkings = true;
    public bool buildKerbs        = true;
    public bool buildIntersection = true;

    // ── Road surface ──────────────────────────────────────────────────────
    [Header("Road Surface")]
    public Material roadMaterial;
    [Tooltip("UV tile length in metres — road texture repeats every N metres along V axis.")]
    public float uvTileLength   = 6f;
    [Tooltip("Y lift above spline to prevent Z-fighting with flat terrain.")]
    public float surfaceYOffset = 0.02f;

    // ── Lane markings ─────────────────────────────────────────────────────
    [Header("Lane Markings")]
    public Material markingMaterial;
    [Tooltip("Painted line width in metres.")]
    public float markingWidth   = 0.15f;
    [Tooltip("Dash length in metres. Set 0 for fully solid line.")]
    public float dashLength     = 3.0f;
    [Tooltip("Gap between dashes in metres.")]
    public float dashGap        = 4.0f;
    [Tooltip("Lift above road surface.")]
    public float markingYOffset = 0.005f;

    // ── Kerbs ─────────────────────────────────────────────────────────────
    [Header("Kerbs / Sidewalks")]
    public Material kerbMaterial;
    [Tooltip("Kerb width in metres (how wide the raised strip is).")]
    public float kerbWidth   = 0.25f;
    [Tooltip("Kerb height above road surface in metres.")]
    public float kerbHeight  = 0.12f;

    // ── Intersection ──────────────────────────────────────────────────────
    [Header("Intersection Fill")]
    [Tooltip("Uses the same road material if left null.")]
    public Material intersectionMaterial;
    [Tooltip("Convex hull polygon segments. Higher = rounder fill at large junctions.")]
    public int intersectionSegments = 32;
    [Tooltip("Extra margin beyond road arm endpoints in metres.")]
    public float intersectionMargin = 0.5f;

    // ── Refs ──────────────────────────────────────────────────────────────
    private TrafficRoad road;

    // Child GO names — used to find/reuse across domain reloads
    private const string NAME_SURFACE      = "_Mesh_Surface";
    private const string NAME_MARKINGS     = "_Mesh_Markings";
    private const string NAME_KERB_L       = "_Mesh_KerbLeft";
    private const string NAME_KERB_R       = "_Mesh_KerbRight";
    private const string NAME_INTERSECTION = "_Mesh_Intersection";

    // ─────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────

    void Awake()  => road = GetComponent<TrafficRoad>();
    void OnEnable()=> road = GetComponent<TrafficRoad>();

    // ─────────────────────────────────────────────────────────────────────
    // Entry point
    // ─────────────────────────────────────────────────────────────────────

    [ContextMenu("Build Mesh")]
    public void BuildMesh()
    {
        if (road == null) road = GetComponent<TrafficRoad>();
        if (road == null) return;

        var centre = road.BuildCentreSpline();
        if (centre == null || centre.Count < 2) return;

        // Precompute arc-length table once — shared by all sections
        float[] arc = BuildArcTable(centre);

        if (buildRoadSurface)  BuildSurface(centre, arc);
        else                   DestroySection(NAME_SURFACE);

        if (buildLaneMarkings) BuildMarkings(centre, arc);
        else                   DestroySection(NAME_MARKINGS);

        if (buildKerbs)
        {
            BuildKerb(centre, arc, left: true);
            BuildKerb(centre, arc, left: false);
        }
        else
        {
            DestroySection(NAME_KERB_L);
            DestroySection(NAME_KERB_R);
        }

        if (buildIntersection && road.intersectionNode != null)
            BuildIntersectionFill();
        else
            DestroySection(NAME_INTERSECTION);
    }

    [ContextMenu("Clear All Meshes")]
    public void ClearMeshes()
    {
        DestroySection(NAME_SURFACE);
        DestroySection(NAME_MARKINGS);
        DestroySection(NAME_KERB_L);
        DestroySection(NAME_KERB_R);
        DestroySection(NAME_INTERSECTION);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Arc-length table
    // ─────────────────────────────────────────────────────────────────────

    static float[] BuildArcTable(List<Vector3> pts)
    {
        var arc = new float[pts.Count];
        arc[0] = 0f;
        for (int i = 1; i < pts.Count; i++)
            arc[i] = arc[i - 1] + Vector3.Distance(pts[i - 1], pts[i]);
        return arc;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 1. Road surface
    //
    // Quad-strip: two verts per spline sample (left edge, right edge).
    // Normal points straight up (flat road).
    // MeshCollider always added — vehicles drive on this.
    // ─────────────────────────────────────────────────────────────────────

    void BuildSurface(List<Vector3> centre, float[] arc)
    {
        int n = centre.Count;

        var verts = new Vector3[n * 2];
        var uvs   = new Vector2[n * 2];
        var tris  = new int[(n - 1) * 6];

        float hw = road.roadWidth * 0.5f;
        float tileV = Mathf.Max(uvTileLength, 0.01f);

        for (int i = 0; i < n; i++)
        {
            Vector3 right = TrafficRoad.RightAt(centre, i);
            Vector3 pt    = centre[i] + Vector3.up * surfaceYOffset;

            verts[i * 2]     = pt - right * hw;   // left
            verts[i * 2 + 1] = pt + right * hw;   // right

            float v = arc[i] / tileV;
            uvs[i * 2]     = new Vector2(0f, v);
            uvs[i * 2 + 1] = new Vector2(1f, v);
        }

        for (int i = 0; i < n - 1; i++)
        {
            int bl = i * 2, br = bl + 1;
            int tl = bl + 2, tr = bl + 3;
            int b  = i * 6;
            // CCW winding (Unity standard, normals up)
            tris[b]   = bl; tris[b+1] = tl; tris[b+2] = br;
            tris[b+3] = br; tris[b+4] = tl; tris[b+5] = tr;
        }

        var mesh = MakeMesh($"{road.name}_Surface", verts, uvs, tris);

        var go = GetOrCreateSection(NAME_SURFACE);
        SetMesh(go, mesh, roadMaterial);

        // MeshCollider — always on for road surface
        var col = GetOrAdd<MeshCollider>(go);
        col.sharedMesh = mesh;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 2. Lane markings
    //
    // One strip per lane divider:
    //   Road edges (left, right)     → solid white
    //   Centre divider (fwd vs bwd)  → solid yellow (double-line)
    //   Between same-direction lanes → dashed white
    //
    // All strips combined into one mesh / one material draw call.
    // ─────────────────────────────────────────────────────────────────────

    void BuildMarkings(List<Vector3> centre, float[] arc)
    {
        var verts = new List<Vector3>();
        var uvs   = new List<Vector2>();
        var tris  = new List<int>();

        float hw   = road.roadWidth * 0.5f;
        float yOff = surfaceYOffset + markingYOffset;
        int   fwd  = road.forwardLanes;
        int   bwd  = road.backwardLanes;

        // Build list of (xOffset, isSolid) for each divider
        // Forward lanes occupy [-hw .. 0], backward [0 .. +hw]
        // (matching TrafficRoad GenerateRoad lane layout)
        var dividers = new List<(float x, bool solid, bool doubleLine)>();

        // Left road edge — solid white
        dividers.Add((-hw, true, false));

        // Dividers between forward lanes — dashed white
        for (int i = 1; i < fwd; i++)
            dividers.Add((-hw + road.roadWidth * 0.5f * i / (float)fwd, false, false));

        // Centre — solid yellow double line
        dividers.Add((0f, true, true));

        // Dividers between backward lanes — dashed white
        for (int i = 1; i < bwd; i++)
            dividers.Add((hw * i / (float)bwd, false, false));

        // Right road edge — solid white
        dividers.Add((hw, true, false));

        foreach (var (x, solid, doubleLine) in dividers)
        {
            AddMarkingStrip(centre, arc, x, yOff, solid, verts, uvs, tris);
            if (doubleLine)   // double centre line: add a second strip offset by markingWidth * 1.5
                AddMarkingStrip(centre, arc, x + markingWidth * 1.5f, yOff, true, verts, uvs, tris);
        }

        if (verts.Count == 0) return;

        var mesh = MakeMeshLists($"{road.name}_Markings", verts, uvs, tris);
        var go   = GetOrCreateSection(NAME_MARKINGS);
        SetMesh(go, mesh, markingMaterial);
        // No collider on markings — remove if one exists
        var col = go.GetComponent<MeshCollider>();
        if (col != null) DestroyImmediate(col);  // Unity == safe here
    }

    void AddMarkingStrip(List<Vector3> centre, float[] arc,
                         float xOff, float yOff, bool solid,
                         List<Vector3> verts, List<Vector2> uvs, List<int> tris)
    {
        int   n  = centre.Count;
        float hw = markingWidth * 0.5f;

        for (int i = 0; i < n - 1; i++)
        {
            float segStart = arc[i];
            float segEnd   = arc[i + 1];
            float segLen   = segEnd - segStart;
            if (segLen < 0.001f) continue;

            Vector3 r0  = TrafficRoad.RightAt(centre, i);
            Vector3 r1  = TrafficRoad.RightAt(centre, i + 1);
            Vector3 pt0 = centre[i]   + Vector3.up * yOff + r0 * xOff;
            Vector3 pt1 = centre[i+1] + Vector3.up * yOff + r1 * xOff;

            if (solid)
            {
                AppendQuad(pt0, pt1, r0, r1, hw,
                           segStart / uvTileLength, segEnd / uvTileLength,
                           verts, uvs, tris);
            }
            else
            {
                // Walk dashes across this segment using global arc coordinates
                // so dashes don't restart at every spline sample
                float period = dashLength + dashGap;
                // Find first dash start that falls at or before segStart
                float phase  = segStart % period;
                float t      = segStart - phase;   // start of current dash cycle in global arc
                if (t + dashLength < segStart) t += period;   // skip finished dash

                while (t < segEnd)
                {
                    float ds = Mathf.Max(t,               segStart);
                    float de = Mathf.Min(t + dashLength,  segEnd);

                    if (de > ds)
                    {
                        float f0 = (ds - segStart) / segLen;
                        float f1 = (de - segStart) / segLen;

                        Vector3 dpt0 = Vector3.Lerp(pt0, pt1, f0);
                        Vector3 dpt1 = Vector3.Lerp(pt0, pt1, f1);
                        Vector3 dr0  = Vector3.Slerp(r0, r1, f0);
                        Vector3 dr1  = Vector3.Slerp(r0, r1, f1);

                        AppendQuad(dpt0, dpt1, dr0, dr1, hw,
                                   ds / uvTileLength, de / uvTileLength,
                                   verts, uvs, tris);
                    }

                    t += period;
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 3. Kerbs
    //
    // Each kerb has three faces:
    //   Top face   — horizontal strip at kerbHeight above road
    //   Inner wall — vertical face from road level up to top
    //   Outer wall — thin vertical outer face (cosmetic)
    //
    // This gives a solid raised kerb that casts correct shadows.
    // ─────────────────────────────────────────────────────────────────────

    void BuildKerb(List<Vector3> centre, float[] arc, bool left)
    {
        int   n      = centre.Count;
        float side   = left ? -1f : 1f;
        float edgeX  = road.roadWidth * 0.5f * side;      // road edge
        float outerX = edgeX + side * kerbWidth;           // outer kerb edge
        float yBot   = surfaceYOffset;
        float yTop   = surfaceYOffset + kerbHeight;

        var verts = new List<Vector3>();
        var uvs   = new List<Vector2>();
        var tris  = new List<int>();

        for (int i = 0; i < n - 1; i++)
        {
            float v0 = arc[i]   / uvTileLength;
            float v1 = arc[i+1] / uvTileLength;

            Vector3 r0 = TrafficRoad.RightAt(centre, i);
            Vector3 r1 = TrafficRoad.RightAt(centre, i + 1);

            // Four corners for each face pair
            // Inner bottom (road level, road edge)
            Vector3 ib0 = centre[i]   + Vector3.up * yBot + r0 * edgeX;
            Vector3 ib1 = centre[i+1] + Vector3.up * yBot + r1 * edgeX;
            // Inner top (kerbHeight, road edge)
            Vector3 it0 = centre[i]   + Vector3.up * yTop + r0 * edgeX;
            Vector3 it1 = centre[i+1] + Vector3.up * yTop + r1 * edgeX;
            // Outer top (kerbHeight, outer edge)
            Vector3 ot0 = centre[i]   + Vector3.up * yTop + r0 * outerX;
            Vector3 ot1 = centre[i+1] + Vector3.up * yTop + r1 * outerX;
            // Outer bottom (road level, outer edge)
            Vector3 ob0 = centre[i]   + Vector3.up * yBot + r0 * outerX;
            Vector3 ob1 = centre[i+1] + Vector3.up * yBot + r1 * outerX;

            // ── Inner vertical wall (faces road inward) ────────────────
            AppendFaceQuad(ib0, ib1, it0, it1, v0, v1, verts, uvs, tris, left);

            // ── Top face (horizontal, faces up) ───────────────────────
            AppendFaceQuad(it0, it1, ot0, ot1, v0, v1, verts, uvs, tris, left);

            // ── Outer vertical wall (faces away from road) ─────────────
            AppendFaceQuad(ot0, ot1, ob0, ob1, v0, v1, verts, uvs, tris, !left);
        }

        // End caps (flat quads closing the kerb at start and end of road)
        AddKerbCap(centre, arc, 0,     edgeX, outerX, yBot, yTop, verts, uvs, tris, capStart: true);
        AddKerbCap(centre, arc, n - 1, edgeX, outerX, yBot, yTop, verts, uvs, tris, capStart: false);

        if (verts.Count == 0) return;

        var mesh   = MakeMeshLists($"{road.name}_Kerb{(left ? "L" : "R")}", verts, uvs, tris);
        var goName = left ? NAME_KERB_L : NAME_KERB_R;
        var go     = GetOrCreateSection(goName);
        SetMesh(go, mesh, kerbMaterial != null ? kerbMaterial : roadMaterial);
        // No collider on kerbs — they're cosmetic geometry
    }

    // Append a quad for one face of the kerb.
    // winding param flips winding order so normals point outward for each face.
    void AppendFaceQuad(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1,
                         float v0, float v1,
                         List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                         bool flip)
    {
        int idx = verts.Count;
        verts.Add(a0); verts.Add(a1); verts.Add(b0); verts.Add(b1);
        uvs.Add(new Vector2(0f, v0)); uvs.Add(new Vector2(1f, v0));
        uvs.Add(new Vector2(0f, v1)); uvs.Add(new Vector2(1f, v1));

        if (!flip)
        {
            tris.Add(idx);   tris.Add(idx+2); tris.Add(idx+1);
            tris.Add(idx+1); tris.Add(idx+2); tris.Add(idx+3);
        }
        else
        {
            tris.Add(idx);   tris.Add(idx+1); tris.Add(idx+2);
            tris.Add(idx+1); tris.Add(idx+3); tris.Add(idx+2);
        }
    }

    void AddKerbCap(List<Vector3> centre, float[] arc,
                    int ptIdx, float edgeX, float outerX,
                    float yBot, float yTop,
                    List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                    bool capStart)
    {
        Vector3 r   = TrafficRoad.RightAt(centre, ptIdx);
        Vector3 pt  = centre[ptIdx];
        float   v   = arc[ptIdx] / uvTileLength;

        Vector3 ib = pt + Vector3.up * yBot + r * edgeX;
        Vector3 it = pt + Vector3.up * yTop + r * edgeX;
        Vector3 ot = pt + Vector3.up * yTop + r * outerX;
        Vector3 ob = pt + Vector3.up * yBot + r * outerX;

        int idx = verts.Count;
        verts.Add(ib); verts.Add(it); verts.Add(ot); verts.Add(ob);
        uvs.Add(new Vector2(0f, v)); uvs.Add(new Vector2(0f, v));
        uvs.Add(new Vector2(1f, v)); uvs.Add(new Vector2(1f, v));

        if (capStart)
        {
            tris.Add(idx);   tris.Add(idx+1); tris.Add(idx+2);
            tris.Add(idx);   tris.Add(idx+2); tris.Add(idx+3);
        }
        else
        {
            tris.Add(idx);   tris.Add(idx+2); tris.Add(idx+1);
            tris.Add(idx);   tris.Add(idx+3); tris.Add(idx+2);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 4. Intersection fill
    //
    // Strategy: collect the road-end positions of all arms connected to
    // the intersectionNode, offset them outward by intersectionMargin,
    // compute a convex hull in XZ, then tessellate it as a fan.
    // This gives a correctly-shaped fill for T-junctions and 4-ways alike.
    // ─────────────────────────────────────────────────────────────────────

    void BuildIntersectionFill()
    {
        var node   = road.intersectionNode;
        Vector3 centre = node.transform.position + Vector3.up * surfaceYOffset;

        // Collect arm tip positions (road endpoints nearest to intersection)
        var armTips = new List<Vector3>();

        foreach (var connRoad in node.connectedRoads)
        {
            if (connRoad == null) continue;
            // Pick whichever end node is closer to the intersection centre
            float dStart = connRoad.startNode != null
                ? Vector3.Distance(connRoad.startNode.transform.position, node.transform.position)
                : float.MaxValue;
            float dEnd = connRoad.endNode != null
                ? Vector3.Distance(connRoad.endNode.transform.position, node.transform.position)
                : float.MaxValue;

            Vector3 nearEnd = dStart < dEnd
                ? connRoad.startNode.transform.position
                : connRoad.endNode.transform.position;

            // Offset outward from intersection centre by half road width + margin
            Vector3 dir = (nearEnd - node.transform.position).normalized;
            float   dist = Vector3.Distance(nearEnd, node.transform.position);
            float   radius = Mathf.Max(connRoad.roadWidth * 0.5f + intersectionMargin,
                                       dist * 0.5f + intersectionMargin);

            // Add left and right corners of this road arm
            // Right vector perpendicular to arm direction in XZ
            Vector3 armRight = new Vector3(-dir.z, 0f, dir.x).normalized;
            float   hw = connRoad.roadWidth * 0.5f + intersectionMargin;
            Vector3 nearPos = node.transform.position + dir * radius;

            armTips.Add(nearPos - armRight * hw + Vector3.up * surfaceYOffset);
            armTips.Add(nearPos + armRight * hw + Vector3.up * surfaceYOffset);
        }

        // Fallback: if no connected roads found use a circle
        if (armTips.Count < 3)
        {
            BuildIntersectionCircle(centre);
            return;
        }

        // Convex hull in XZ (Graham scan)
        var hull = ConvexHullXZ(armTips);
        if (hull.Count < 3)
        {
            BuildIntersectionCircle(centre);
            return;
        }

        // Fan triangulation from centroid
        var verts = new List<Vector3>();
        var uvs   = new List<Vector2>();
        var tris  = new List<int>();

        // Centroid
        verts.Add(centre);
        uvs.Add(new Vector2(0.5f, 0.5f));

        // Compute UV bounds for hull
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var p in hull)
        {
            if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
            if (p.z < minZ) minZ = p.z; if (p.z > maxZ) maxZ = p.z;
        }
        float rangeX = Mathf.Max(maxX - minX, 0.01f);
        float rangeZ = Mathf.Max(maxZ - minZ, 0.01f);

        foreach (var p in hull)
        {
            verts.Add(p);
            uvs.Add(new Vector2((p.x - minX) / rangeX, (p.z - minZ) / rangeZ));
        }

        int hCount = hull.Count;
        for (int i = 0; i < hCount; i++)
        {
            tris.Add(0);
            tris.Add(i + 1);
            tris.Add((i + 1) % hCount + 1);
        }

        var mesh = MakeMeshLists($"{road.name}_Intersection", verts, uvs, tris);
        var go   = GetOrCreateSection(NAME_INTERSECTION);
        SetMesh(go, mesh, intersectionMaterial != null ? intersectionMaterial : roadMaterial);
    }

    void BuildIntersectionCircle(Vector3 centre)
    {
        float radius = road.roadWidth * 0.6f + intersectionMargin;
        int   segs   = intersectionSegments;

        var verts = new Vector3[segs + 1];
        var uvs   = new Vector2[segs + 1];
        var tris  = new int[segs * 3];

        verts[0] = centre;
        uvs[0]   = new Vector2(0.5f, 0.5f);

        for (int i = 0; i < segs; i++)
        {
            float a = i / (float)segs * Mathf.PI * 2f;
            var   off = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
            verts[i + 1] = centre + off;
            uvs[i + 1]   = new Vector2(0.5f + Mathf.Cos(a) * 0.5f,
                                        0.5f + Mathf.Sin(a) * 0.5f);
        }

        for (int i = 0; i < segs; i++)
        {
            tris[i * 3]     = 0;
            tris[i * 3 + 1] = i + 1;
            tris[i * 3 + 2] = (i + 1) % segs + 1;
        }

        var mesh = MakeMesh($"{road.name}_Intersection", verts, uvs, tris);
        var go   = GetOrCreateSection(NAME_INTERSECTION);
        SetMesh(go, mesh, intersectionMaterial != null ? intersectionMaterial : roadMaterial);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Convex Hull (Graham scan, XZ plane)
    // Returns points in CCW order as seen from above.
    // ─────────────────────────────────────────────────────────────────────

    static List<Vector3> ConvexHullXZ(List<Vector3> pts)
    {
        if (pts.Count < 3) return new List<Vector3>(pts);

        // Find lowest-Z (then lowest-X) pivot
        int pivotIdx = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            if (pts[i].z < pts[pivotIdx].z ||
                (pts[i].z == pts[pivotIdx].z && pts[i].x < pts[pivotIdx].x))
                pivotIdx = i;
        }
        Vector3 pivot = pts[pivotIdx];

        // Sort by polar angle from pivot
        var sorted = new List<Vector3>(pts);
        sorted.RemoveAt(pivotIdx);
        sorted.Sort((a, b) =>
        {
            float ax = a.x - pivot.x, az = a.z - pivot.z;
            float bx = b.x - pivot.x, bz = b.z - pivot.z;
            float cross = ax * bz - az * bx;
            if (Mathf.Abs(cross) > 0.0001f) return cross > 0 ? -1 : 1;
            // Collinear — keep closer point first
            return (ax*ax + az*az).CompareTo(bx*bx + bz*bz);
        });

        // Graham scan
        var hull = new List<Vector3> { pivot };
        foreach (var p in sorted)
        {
            while (hull.Count > 1)
            {
                Vector3 o = hull[hull.Count - 2];
                Vector3 a = hull[hull.Count - 1];
                float cross = (a.x - o.x) * (p.z - o.z) - (a.z - o.z) * (p.x - o.x);
                if (cross <= 0f) hull.RemoveAt(hull.Count - 1);
                else break;
            }
            hull.Add(p);
        }

        return hull;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Shared quad builder
    // ─────────────────────────────────────────────────────────────────────

    static void AppendQuad(Vector3 pt0, Vector3 pt1,
                            Vector3 r0,  Vector3 r1,  float hw,
                            float v0, float v1,
                            List<Vector3> verts, List<Vector2> uvs, List<int> tris)
    {
        int idx = verts.Count;

        verts.Add(pt0 - r0 * hw);  // 0  left-start
        verts.Add(pt0 + r0 * hw);  // 1  right-start
        verts.Add(pt1 - r1 * hw);  // 2  left-end
        verts.Add(pt1 + r1 * hw);  // 3  right-end

        uvs.Add(new Vector2(0f, v0)); uvs.Add(new Vector2(1f, v0));
        uvs.Add(new Vector2(0f, v1)); uvs.Add(new Vector2(1f, v1));

        tris.Add(idx);   tris.Add(idx+2); tris.Add(idx+1);
        tris.Add(idx+1); tris.Add(idx+2); tris.Add(idx+3);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Mesh helpers
    // ─────────────────────────────────────────────────────────────────────

    static Mesh MakeMesh(string meshName, Vector3[] verts, Vector2[] uvs, int[] tris)
    {
        var mesh = new Mesh { name = meshName };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    static Mesh MakeMeshLists(string meshName,
                               List<Vector3> verts, List<Vector2> uvs, List<int> tris)
    {
        var mesh = new Mesh { name = meshName };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Child GO management
    // ─────────────────────────────────────────────────────────────────────

    GameObject GetOrCreateSection(string sectionName)
    {
        // Search existing children first (survives domain reload)
        for (int i = 0; i < transform.childCount; i++)
        {
            var child = transform.GetChild(i);
            if (child != null && child.name == sectionName)
                return child.gameObject;
        }

        // Create and pre-add components — avoids Unity fake-null race
        // where GetComponent returns non-null but component is not yet usable
        var go = new GameObject(sectionName);
        go.transform.SetParent(transform, worldPositionStays: false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale    = Vector3.one;
        go.AddComponent<MeshFilter>();
        go.AddComponent<MeshRenderer>();
        return go;
    }

    void DestroySection(string sectionName)
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i);
            if (child != null && child.name == sectionName)
                DestroyImmediate(child.gameObject);
        }
    }

    void SetMesh(GameObject go, Mesh mesh, Material mat)
    {
        // Unity overrides == but NOT ??, so we must use explicit null checks
        var mf = go.GetComponent<MeshFilter>();
        if (mf == null) mf = go.AddComponent<MeshFilter>();

        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) mr = go.AddComponent<MeshRenderer>();

        mf.sharedMesh     = mesh;
        mr.sharedMaterial = mat;
    }

    static T GetOrAdd<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (c == null) c = go.AddComponent<T>();
        return c;
    }
}