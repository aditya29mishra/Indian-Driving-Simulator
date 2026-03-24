#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficRoadEditor — custom inspector + live regeneration on CP drag.
//
// Why this is needed:
//   ExecuteAlways makes the road run in edit mode, but Unity does NOT call
//   any method on a MonoBehaviour when a CHILD transform is moved by the user.
//   The only reliable way to detect "a control point was dragged" is to poll
//   every SceneView frame and compare stored positions.
//
// How it works:
//   - OnEnable caches all control point positions.
//   - SceneGUI is called every SceneView repaint (~60fps while scene is focused).
//   - If any position changed → call GenerateRoad() → cache new positions.
//   - Undo/Redo is registered so Ctrl+Z restores the previous waypoint state.
//
// Inspector additions:
//   - "Add Control Point" and "Remove Last" buttons (same as context menu,
//     but visible without right-clicking).
//   - Shows control point count and spline sample count.
// ─────────────────────────────────────────────────────────────────────────────

[CustomEditor(typeof(TrafficRoad))]
public class TrafficRoadEditor : Editor
{
    TrafficRoad road;

    // Cached positions — used to detect drag changes
    private List<Vector3> cachedCPPositions = new List<Vector3>();

    // Also watch startNode and endNode — moving them should re-bake too
    private Vector3 cachedStartPos;
    private Vector3 cachedEndPos;

    void OnEnable()
    {
        road = (TrafficRoad)target;
        CachePositions();
    }

    void CachePositions()
    {
        cachedCPPositions.Clear();
        foreach (var cp in road.controlPoints)
            cachedCPPositions.Add(cp != null ? cp.position : Vector3.zero);

        cachedStartPos = road.startNode != null ? road.startNode.transform.position : Vector3.zero;
        cachedEndPos   = road.endNode   != null ? road.endNode.transform.position   : Vector3.zero;
    }

    bool PositionsChanged()
    {
        // Control point count changed
        if (cachedCPPositions.Count != road.controlPoints.Count) return true;

        // Any CP moved
        for (int i = 0; i < road.controlPoints.Count; i++)
        {
            if (road.controlPoints[i] == null) continue;
            if (road.controlPoints[i].position != cachedCPPositions[i]) return true;
        }

        // Start or end node moved
        if (road.startNode != null && road.startNode.transform.position != cachedStartPos) return true;
        if (road.endNode   != null && road.endNode.transform.position   != cachedEndPos)   return true;

        return false;
    }

    // ── SceneGUI — called every SceneView repaint ─────────────────────────

    void OnSceneGUI()
    {
        if (road == null) return;

        // Detect any position change and regenerate immediately
        if (PositionsChanged())
        {
            Undo.RecordObject(road, "TrafficRoad Spline Edit");

            // Also record all child transforms so Undo restores waypoints
            foreach (Transform child in road.transform)
            {
                if (child.GetComponent<RoadControlPoint>() == null)
                    Undo.RecordObject(child.gameObject, "TrafficRoad Spline Edit");
            }

            road.GenerateRoad();
            CachePositions();

            // Mark scene dirty so the change is saved
            EditorUtility.SetDirty(road);
        }

        // Draw move handles on all control points
        // This makes them draggable even when the road (not the CP) is selected
        DrawControlPointHandles();
    }

    void DrawControlPointHandles()
    {
        for (int i = 0; i < road.controlPoints.Count; i++)
        {
            var cp = road.controlPoints[i];
            if (cp == null) continue;

            EditorGUI.BeginChangeCheck();

            // Position handle — standard Unity move widget
            Vector3 newPos = Handles.PositionHandle(cp.position, Quaternion.identity);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(cp, "Move Road Control Point");
                cp.position = newPos;
            }

            // Label
            Handles.Label(cp.position + Vector3.up * 1.5f,
                $"  CP{i}\n  {cp.position:F1}",
                EditorStyles.miniLabel);
        }
    }

    // ── Inspector ─────────────────────────────────────────────────────────

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Control Points", EditorStyles.boldLabel);

        // Info line
        int cpCount = road.controlPoints.Count;
        int knots   = cpCount + 2; // start + CPs + end
        int samples = (knots - 1) * road.splineSamplesPerSegment;
        EditorGUILayout.HelpBox(
            $"{cpCount} control point(s) | {knots} knots | ~{samples} spline samples\n" +
            (cpCount == 0
                ? "Straight road — add a control point to curve it."
                : "Drag the yellow spheres in Scene view to reshape the road."),
            MessageType.Info);

        EditorGUILayout.Space(4);

        // Buttons
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("＋ Add Control Point"))
        {
            Undo.RecordObject(road, "Add Road Control Point");
            road.AddControlPoint();
            CachePositions();
            EditorUtility.SetDirty(road);
        }
        GUI.enabled = road.controlPoints.Count > 0;
        if (GUILayout.Button("－ Remove Last"))
        {
            Undo.RecordObject(road, "Remove Road Control Point");
            road.RemoveLastControlPoint();
            CachePositions();
            EditorUtility.SetDirty(road);
        }
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);

        if (GUILayout.Button("⟳  Resync Control Points From Children"))
        {
            road.ResyncControlPoints();
            CachePositions();
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Generation", EditorStyles.boldLabel);

        if (GUILayout.Button("Generate Road  (bake waypoints)"))
        {
            Undo.RecordObject(road, "Generate TrafficRoad");
            road.GenerateRoad();
            CachePositions();
            EditorUtility.SetDirty(road);
        }
    }
}
#endif
