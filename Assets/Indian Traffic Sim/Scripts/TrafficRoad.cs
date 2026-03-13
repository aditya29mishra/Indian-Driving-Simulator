using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public class TrafficRoad : MonoBehaviour
{
    [Header("Road Nodes")]
    public TrafficNode startNode;
    public TrafficNode endNode;

    [Header("Road Properties")]
    public float roadWidth = 8f;
    public float speedLimit = 50f;

    [Header("Guide System")]
    public int guideCount = 3; // left, center, right

    [HideInInspector]
    public List<Vector3> startGuides = new List<Vector3>();

    [HideInInspector]
    public List<Vector3> endGuides = new List<Vector3>();

    [Header("Paths")]
    public List<TrafficLane> lanes = new List<TrafficLane>();

    private void OnEnable()
    {
        RegisterWithNodes();
    }

    private void OnDisable()
    {
        UnregisterFromNodes();
    }

    void RegisterWithNodes()
    {
        if (startNode != null)
            startNode.RegisterRoad(this);

        if (endNode != null)
            endNode.RegisterRoad(this);
    }

    void UnregisterFromNodes()
    {
        if (startNode != null)
            startNode.RemoveRoad(this);

        if (endNode != null)
            endNode.RemoveRoad(this);
    }

    public void GenerateGuides()
    {
        startGuides.Clear();
        endGuides.Clear();

        if (startNode == null || endNode == null)
            return;

        Vector3 start = startNode.transform.position;
        Vector3 end = endNode.transform.position;

        Vector3 dir = (end - start).normalized;
        Vector3 right = Vector3.Cross(Vector3.up, dir);

        float step = roadWidth / (guideCount - 1);

        for (int i = 0; i < guideCount; i++)
        {
            float offset = -roadWidth * 0.5f + step * i;

            startGuides.Add(start + right * offset);
            endGuides.Add(end + right * offset);
        }
    }

    private void OnDrawGizmos()
    {
        if (startNode == null || endNode == null)
            return;

        Vector3 start = startNode.transform.position;
        Vector3 end = endNode.transform.position;

        // center line
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(start, end);

        // road edges
        Vector3 dir = (end - start).normalized;
        Vector3 right = Vector3.Cross(Vector3.up, dir);

        Vector3 leftA = start - right * roadWidth * 0.5f;
        Vector3 rightA = start + right * roadWidth * 0.5f;

        Vector3 leftB = end - right * roadWidth * 0.5f;
        Vector3 rightB = end + right * roadWidth * 0.5f;

        Gizmos.color = Color.blue;
        Gizmos.DrawLine(leftA, leftB);
        Gizmos.DrawLine(rightA, rightB);

        // guide points
        GenerateGuides();

        Gizmos.color = Color.yellow;

        for (int i = 0; i < startGuides.Count; i++)
        {
            Gizmos.DrawSphere(startGuides[i], 0.25f);
            Gizmos.DrawSphere(endGuides[i], 0.25f);

            Gizmos.DrawLine(startGuides[i], endGuides[i]);
        }
    }
}