using UnityEngine;

[ExecuteAlways]
public class RoadToCarWalkPath : MonoBehaviour
{
    public CarWalkPath carPath;

    public Transform[] laneCenters;

    public int resolution = 12;

    public float laneOffset = 2.5f;
    [ContextMenu("Generate")]
    public void Generate()
    {
        if (carPath == null || laneCenters.Length < 2)
            return;

        int ways = 2;
        int pointsPerWay = resolution;

        carPath.points = new Vector3[ways, pointsPerWay];

        for (int w = 0; w < ways; w++)
        {
            for (int i = 0; i < pointsPerWay; i++)
            {
                float t = (float)i / (pointsPerWay - 1);

                Vector3 p = Vector3.Lerp(
                    laneCenters[0].position,
                    laneCenters[1].position,
                    t
                );

                Vector3 dir = (laneCenters[1].position - laneCenters[0].position).normalized;

                Vector3 right = Vector3.Cross(Vector3.up, dir);

                if (w == 0)
                    p += right * laneOffset;
                else
                    p -= right * laneOffset;

                carPath.points[w, i] = p;
            }
        }

        carPath.DrawCurved(false);
        carPath.CreateSpawnPoints();
    }
}
