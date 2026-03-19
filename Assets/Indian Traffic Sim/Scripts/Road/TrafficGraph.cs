using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficGraph — road network graph, built once at scene load.
//
// Mental model: a driver knows their destination and the road network.
// At each intersection they make a single informed decision — which exit
// moves me closer through the actual road network, not through the air.
// This class IS that mental map.
//
// Graph structure:
//   Nodes  = TrafficRoadNode (End, Joint, etc.)
//   Edges  = road segments connecting nodes through intersections
//
//   For each TrafficIntersectionNode:
//     For each pair of roads (A, B) in connectedRoads:
//       Add bidirectional edge: A's RoadEnd node ↔ B's RoadEnd node
//       Weight = road length (metres) — can be changed to travel time
//
// Usage:
//   TrafficGraph.Instance.GetNextNode(fromNode, toNode)
//   Returns the immediate next TrafficRoadNode to head toward.
//   Called once per intersection decision — not every frame.
//
// Singleton: one instance per scene. Add to any persistent GameObject.
// Rebuilds automatically when roads change (call Rebuild() after Generate All).
//
// Talks to: TrafficRoad, TrafficRoadNode, TrafficIntersectionNode, LaneManager
// ─────────────────────────────────────────────────────────────────────────────

public class TrafficGraph : MonoBehaviour
{
    public static TrafficGraph Instance { get; private set; }

    [Header("References")]
    [Tooltip("LaneManager that holds the full road list.")]
    public LaneManager laneManager;

    [Header("Debug")]
    public bool logOnBuild = true;

    // ── Graph storage ─────────────────────────────────────────────────────
    // adjacency[node] = list of (neighbour, edgeWeight)
    private Dictionary<TrafficRoadNode, List<GraphEdge>> adjacency
        = new Dictionary<TrafficRoadNode, List<GraphEdge>>();

    private struct GraphEdge
    {
        public TrafficRoadNode neighbour;
        public float           weight;    // road length in metres
        public GraphEdge(TrafficRoadNode n, float w) { neighbour = n; weight = w; }
    }

    private bool isBuilt = false;

    // ─────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void Start()
    {
        if (laneManager == null)
            laneManager = FindObjectOfType<LaneManager>();

        Build();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Graph construction
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the road network graph from all roads in LaneManager.
    /// Safe to call multiple times — clears and rebuilds each time.
    /// Call after Generate All to keep the graph in sync.
    /// </summary>
    [ContextMenu("Rebuild Graph")]
    public void Build()
    {
        adjacency.Clear();
        isBuilt = false;

        if (laneManager == null)
        {
            Debug.LogWarning("[TrafficGraph] No LaneManager assigned.");
            return;
        }

        if (laneManager.roads == null || laneManager.roads.Count == 0)
        {
            Debug.LogWarning("[TrafficGraph] LaneManager.roads is empty. " +
                             "Run Generate All first, then Rebuild Graph.");
            return;
        }

        // Collect all TrafficIntersectionNodes in the scene
        var intersections = FindObjectsOfType<TrafficIntersectionNode>();

        int edgeCount = 0;

        foreach (var intersection in intersections)
        {
            if (intersection == null) continue;
            var roads = intersection.connectedRoads;
            if (roads == null || roads.Count < 2) continue;

            // For every ordered pair of roads at this intersection,
            // add an edge between their RoadEnd nodes.
            // A vehicle entering via road A can exit via road B — that's one edge.
            for (int i = 0; i < roads.Count; i++)
            {
                for (int j = 0; j < roads.Count; j++)
                {
                    if (i == j) continue;

                    TrafficRoad roadA = roads[i];
                    TrafficRoad roadB = roads[j];
                    if (roadA == null || roadB == null) continue;

                    // Get the End node of each road (the outer tip, away from intersection)
                    TrafficRoadNode nodeA = GetEndNode(roadA);
                    TrafficRoadNode nodeB = GetEndNode(roadB);
                    if (nodeA == null || nodeB == null) continue;

                    // Edge weight = length of both road arms combined
                    // (cost of travelling from nodeA through intersection to nodeB)
                    float weight = RoadLength(roadA) + RoadLength(roadB);

                    AddEdge(nodeA, nodeB, weight);
                    edgeCount++;
                }
            }
        }

        isBuilt = true;

        if (logOnBuild)
            Debug.Log($"[TrafficGraph] Built — {adjacency.Count} nodes, " +
                      $"{edgeCount} directed edges across {intersections.Length} intersection(s).");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public API — called by VehicleNavigator at each intersection
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the next TrafficRoadNode to head toward on the way from
    /// currentNode to destinationNode through the road network.
    ///
    /// This is the only public method vehicles need. Call it once per
    /// intersection decision — not every frame.
    ///
    /// Returns null if no path exists or graph not built yet.
    /// </summary>
    public TrafficRoadNode GetNextNode(TrafficRoadNode currentNode,
                                       TrafficRoadNode destinationNode)
    {
        if (!isBuilt || currentNode == null || destinationNode == null)
            return null;

        if (currentNode == destinationNode)
            return null; // already there

        return Dijkstra(currentNode, destinationNode);
    }

    /// <summary>
    /// Returns true if a path exists between the two nodes.
    /// Useful for destination validation at spawn time.
    /// </summary>
    public bool HasPath(TrafficRoadNode from, TrafficRoadNode to)
    {
        if (!isBuilt || from == null || to == null) return false;
        return Dijkstra(from, to) != null || from == to;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Dijkstra — returns the FIRST STEP toward destination
    // ─────────────────────────────────────────────────────────────────────

    TrafficRoadNode Dijkstra(TrafficRoadNode start, TrafficRoadNode goal)
    {
        // dist[node] = shortest known distance from start
        var dist    = new Dictionary<TrafficRoadNode, float>();
        // prev[node] = which node we came from on the shortest path
        var prev    = new Dictionary<TrafficRoadNode, TrafficRoadNode>();
        // Simple priority queue — List sorted on access (fine for small graphs)
        var open    = new List<TrafficRoadNode>();
        var visited = new HashSet<TrafficRoadNode>();

        // Initialise all known nodes to infinity
        foreach (var node in adjacency.Keys)
        {
            dist[node] = float.MaxValue;
            prev[node] = null;
        }

        dist[start] = 0f;
        open.Add(start);

        while (open.Count > 0)
        {
            // Pick the unvisited node with lowest distance
            TrafficRoadNode current = null;
            float bestDist = float.MaxValue;
            foreach (var n in open)
            {
                if (dist.TryGetValue(n, out float d) && d < bestDist)
                {
                    bestDist = d;
                    current  = n;
                }
            }

            if (current == null) break;
            open.Remove(current);

            if (visited.Contains(current)) continue;
            visited.Add(current);

            if (current == goal)
            {
                // Path found — trace back to find the FIRST step from start
                return GetFirstStep(prev, start, goal);
            }

            if (!adjacency.TryGetValue(current, out var edges)) continue;

            foreach (var edge in edges)
            {
                if (visited.Contains(edge.neighbour)) continue;

                float newDist = dist[current] + edge.weight;
                if (!dist.ContainsKey(edge.neighbour) || newDist < dist[edge.neighbour])
                {
                    dist[edge.neighbour] = newDist;
                    prev[edge.neighbour] = current;
                    if (!open.Contains(edge.neighbour))
                        open.Add(edge.neighbour);
                }
            }
        }

        return null; // no path found
    }

    /// <summary>
    /// Traces the prev map back from goal to start and returns
    /// the node immediately after start — the first step of the path.
    /// </summary>
    TrafficRoadNode GetFirstStep(Dictionary<TrafficRoadNode, TrafficRoadNode> prev,
                                  TrafficRoadNode start, TrafficRoadNode goal)
    {
        // Walk backward from goal to start, collecting the path
        var path    = new List<TrafficRoadNode>();
        var current = goal;

        while (current != null && current != start)
        {
            path.Add(current);
            prev.TryGetValue(current, out current);
        }

        if (path.Count == 0) return null;

        // path is reversed — last element is the step just after start
        return path[path.Count - 1];
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the TrafficRoadNode with NodeType.End on this road.
    /// Works regardless of whether it is startNode or endNode.
    /// </summary>
    TrafficRoadNode GetEndNode(TrafficRoad road)
    {
        if (road.startNode != null &&
            road.startNode.nodeType == TrafficRoadNode.NodeType.End)
            return road.startNode;
        if (road.endNode != null &&
            road.endNode.nodeType == TrafficRoadNode.NodeType.End)
            return road.endNode;
        return null;
    }

    /// <summary>
    /// Returns the world-space length of a road in metres.
    /// Uses node positions if available, falls back to transform distance.
    /// </summary>
    float RoadLength(TrafficRoad road)
    {
        if (road.startNode != null && road.endNode != null)
            return Vector3.Distance(
                road.startNode.transform.position,
                road.endNode.transform.position);
        return 10f; // fallback if nodes missing
    }

    void AddEdge(TrafficRoadNode from, TrafficRoadNode to, float weight)
    {
        if (!adjacency.ContainsKey(from))
            adjacency[from] = new List<GraphEdge>();
        adjacency[from].Add(new GraphEdge(to, weight));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Gizmo — draws the graph edges in Scene view
    // ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (!isBuilt || adjacency == null) return;

        foreach (var kvp in adjacency)
        {
            if (kvp.Key == null) continue;
            Vector3 from = kvp.Key.transform.position + Vector3.up * 0.8f;

            foreach (var edge in kvp.Value)
            {
                if (edge.neighbour == null) continue;
                Vector3 to = edge.neighbour.transform.position + Vector3.up * 0.8f;

                // Draw edge as a faint purple line
                Gizmos.color = new Color(0.7f, 0.3f, 1f, 0.35f);
                Gizmos.DrawLine(from, to);

                // Small sphere at each node
                Gizmos.color = new Color(0.7f, 0.3f, 1f, 0.8f);
                Gizmos.DrawSphere(from, 0.3f);
            }
        }
    }
#endif
}
