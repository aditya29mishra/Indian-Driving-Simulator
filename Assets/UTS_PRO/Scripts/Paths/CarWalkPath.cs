using UnityEngine;
using System.Collections.Generic;
public class CarWalkPath : WalkPath
{
    [System.Serializable]
    public class RoadConnection
    {
        public CarWalkPath road;
        public bool enterAtP0;
    }

    [Tooltip("Vehicle Speed / Скорость машины")] public float moveSpeed = 12.0f;
    [Tooltip("Vehicle Deseleration / Торможение машины")] public float speadDecrease = 2.0f;
    [Tooltip("Vehicle Acceleration / Ускорение автомобиля")] public float speadIncrease = 2.0f;
    [Tooltip("Distance to Car / Расстояние до автомобиля")] public float distanceToCar = 10.0f;
    [Tooltip("Distance to traffic lights / Расстояние до светофора")] public float distanceToSemaphore = 10.0f;
    [HideInInspector][SerializeField][Tooltip("Ignore pedestrian colliders? / Игнорировать коллайдеры пешеходов?")] private bool _ignorePeople = false;
    [Tooltip("Distance to the point / Расстояние до точки")] public float nextPointThreshold = 3;
    [Tooltip("Maximum rotation angle for braking / Максимальный угол поворота для притормаживания")] public float maxAngleToMoveBreak = 8.0f;

    [Header("Road Connections")]
    public bool spawnAtP0 = true;
    public bool spawnAtP1 = false;
    public List<RoadConnection> p0Connections = new List<RoadConnection>();
    public List<RoadConnection> p1Connections = new List<RoadConnection>();

    private void Start()
    {
        if (_ignorePeople)
        {
            Physics.IgnoreLayerCollision(9, 8, true);
        }
    }

    public override void CreateSpawnPoints()
    {
        if (points == null)
            DrawCurved(false);

        List<SpawnPoint> spList = new List<SpawnPoint>();

        for (int i = 0; i < points.GetLength(0); i++)
        {
            int laneID = GetLaneID(i);

            // Forward lanes (spawn at P0)
            if (spawnAtP0 && laneID > 0)
            {
                var sp = SpawnPoint.CarCreate(
                    "SpawnPoint_P0",
                    points[i, 0],
                    points[i, 2],
                    lineSpacing,
                    i,
                    true,
                    this,
                    3f,
                    10f
                );

                spList.Add(sp);
            }

            // Backward lanes (spawn at P1)
            if (spawnAtP1 && laneID < 0)
            {
                var sp = SpawnPoint.CarCreate(
                    "SpawnPoint_P1",
                    points[i, pointLength[0] - 1],
                    points[i, pointLength[0] - 3],
                    lineSpacing,
                    i,
                    false,
                    this,
                    3f,
                    10f
                );

                spList.Add(sp);
            }
        }

        SpawnPoints = spList.ToArray();
    }
    public override void SpawnOnePeople(int w, bool forward)
    {
        List<GameObject> pfb = new List<GameObject>(walkingPrefabs);

        for (int i = pfb.Count - 1; i >= 0; i--)
        {
            if (pfb[i] == null)
            {
                pfb.RemoveAt(i);
            }
        }

        walkingPrefabs = pfb.ToArray();
        int prefabNum = UnityEngine.Random.Range(0, walkingPrefabs.Length);
        var people = gameObject;

        if (!forward)
        {
            people = Instantiate(walkingPrefabs[prefabNum], points[w, pointLength[0] - 2], Quaternion.identity) as GameObject;
        }
        else
        {
            people = Instantiate(walkingPrefabs[prefabNum], points[w, 1], Quaternion.identity) as GameObject;
        }

        var movePath = people.AddComponent<MovePath>();
        var car = people.AddComponent<CarAIController>();
        CarInitialize(ref car);

        people.transform.parent = par.transform;
        movePath.walkPath = this;

        if (!forward)
        {
            movePath.InitStartPosition(w, pointLength[0] - 3, loopPath, forward);
            people.transform.LookAt(points[w, pointLength[0] - 3]);
        }
        else
        {
            movePath.InitStartPosition(w, 1, loopPath, forward);
            people.transform.LookAt(points[w, 2]);
        }

        movePath._walkPointThreshold = nextPointThreshold;
        TrafficDebugger.Lane(
$"Car spawned on lane {w} | Forward {forward}"
);

    }

    public override void SpawnPeople()
    {
        List<GameObject> pfb = new List<GameObject>(walkingPrefabs);

        for (int i = pfb.Count - 1; i >= 0; i--)
        {
            if (pfb[i] == null)
            {
                pfb.RemoveAt(i);
            }
        }

        walkingPrefabs = pfb.ToArray();

        if (points == null) DrawCurved(false);

        if (par == null)
        {
            par = new GameObject();
            par.transform.parent = gameObject.transform;
            par.name = "walkingObjects";
        }

        int pathPointCount;

        if (!loopPath)
        {
            pathPointCount = pointLength[0] - 2;
        }
        else
        {
            pathPointCount = pointLength[0] - 1;
        }

        if (pathPointCount < 2) return;

        var pCount = loopPath ? pointLength[0] - 1 : pointLength[0] - 2;

        for (int wayIndex = 0; wayIndex < numberOfWays; wayIndex++)
        {
            _distances = new float[pCount];

            float pathLength = 0f;

            for (int i = 1; i < pCount; i++)
            {
                Vector3 vector;
                if (loopPath && i == pCount - 1)
                {
                    vector = points[wayIndex, 1] - points[wayIndex, pCount];
                }
                else
                {
                    vector = points[wayIndex, i + 1] - points[wayIndex, i];
                }

                pathLength += vector.magnitude;
                _distances[i] = pathLength;
            }

            bool forward = false;

            switch (direction.ToString())
            {
                case "Forward":
                    forward = true;
                    break;
                case "Backward":
                    forward = false;
                    break;
                case "HugLeft":
                    forward = (wayIndex + 2) % 2 == 0;
                    break;
                case "HugRight":
                    forward = (wayIndex + 2) % 2 != 0;
                    break;
                case "WeaveLeft":
                    forward = wayIndex != 1 && wayIndex != 2 && (wayIndex - 1) % 4 != 0 && (wayIndex - 2) % 4 != 0;
                    break;
                case "WeaveRight":
                    forward = wayIndex == 1 || wayIndex == 2 || (wayIndex - 1) % 4 == 0 || (wayIndex - 2) % 4 == 0;
                    break;
            }

            int peopleCount = Mathf.FloorToInt((Density * pathLength) / _minimalObjectLength * 0.2f);
            float segmentLen = _minimalObjectLength + (pathLength - (peopleCount * _minimalObjectLength)) / peopleCount;

            int[] pickList = CommonUtils.GetRandomPrefabIndexes(peopleCount, ref walkingPrefabs);

            Vector3[] pointArray = new Vector3[_distances.Length];

            for (int i = 1; i < _distances.Length; i++)
            {
                pointArray[i - 1] = points[wayIndex, i];
            }

            pointArray[_distances.Length - 1] = loopPath ? points[wayIndex, 1] : points[wayIndex, _distances.Length];

            for (int peopleIndex = 0; peopleIndex < peopleCount; peopleIndex++)
            {
                var people = gameObject;
                var randomShift = UnityEngine.Random.Range(-segmentLen / 3f, segmentLen / 3f) + (wayIndex * segmentLen);
                var finalRandomDistance = (peopleIndex + 1) * segmentLen + randomShift;

                var routePosition = GetRoutePosition(pointArray, finalRandomDistance, pCount, loopPath);
                Vector3 or;

                RaycastHit[] rrr = Physics.RaycastAll(or = new Vector3(routePosition.x, routePosition.y + 10000, routePosition.z), Vector3.down, Mathf.Infinity);

                bool isSemaphore = false;

                for (int i = 0; i < rrr.Length; i++)
                {
                    if (rrr[i].collider.GetComponent<SemaphoreSimulator>() != null || rrr[i].collider.GetComponent<SemaphorePeople>() != null)
                    {
                        isSemaphore = true;
                    }
                }

                if (isSemaphore) continue;

                float dist = 0;
                int bestCandidate = 0;

                rrr = Physics.RaycastAll(or = new Vector3(routePosition.x, routePosition.y + highToSpawn, routePosition.z), Vector3.down, Mathf.Infinity);

                for (int i = 0; i < rrr.Length; i++)
                {
                    if (dist < Vector3.Distance(rrr[0].point, or))
                    {
                        bestCandidate = i;
                        dist = Vector3.Distance(rrr[0].point, or);
                    }
                }

                if (rrr.Length > 0)
                {
                    routePosition.y = rrr[bestCandidate].point.y;
                }

                people = Instantiate(walkingPrefabs[pickList[peopleIndex]], routePosition, Quaternion.identity) as GameObject;

                var movePath = people.AddComponent<MovePath>();
                var car = people.AddComponent<CarAIController>();
                CarInitialize(ref car);

                people.transform.parent = par.transform;
                movePath.walkPath = this;
                movePath._walkPointThreshold = nextPointThreshold;

                movePath.InitStartPosition(wayIndex,
                    GetRoutePoint((peopleIndex + 1) * segmentLen + randomShift, wayIndex, pCount, forward, loopPath), loopPath, forward);

                movePath.SetLookPosition();

                if (people.GetComponent<AddTrailer>())
                {
                    people.GetComponent<AddTrailer>().Init();
                }
            }
        }
    }

    private void CarInitialize(ref CarAIController carAIController)
    {
        float speed = moveSpeed + Random.Range(moveSpeed * -0.15f, moveSpeed * 0.15f);
        speed = Mathf.Clamp(speed, 0, 15);
        carAIController.MOVE_SPEED = speed;

        carAIController.INCREASE = speadIncrease;
        carAIController.DECREASE = speadDecrease;
        carAIController.TO_CAR = distanceToCar;
        carAIController.TO_SEMAPHORE = distanceToSemaphore;
        carAIController.MaxAngle = maxAngleToMoveBreak;
    }
    public int GetLaneID(int laneIndex)
    {
        int half = numberOfWays / 2;

        if (laneIndex < half)
            return half - laneIndex;      // +3 +2 +1
        else
            return -(laneIndex - half + 1); // -1 -2 -3
    }
    public int GetLaneIndex(int laneID)
    {
        int half = numberOfWays / 2;

        if (laneID > 0)
            return half - laneID;
        else
            return half + (-laneID) - 1;
    }
    public bool IsForwardLane(int laneIndex)
    {
        return GetLaneID(laneIndex) > 0;
    }


#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (points == null)
            DrawCurved(false);

        if (points == null) return;

        int last = points.GetLength(1) - 1;

        for (int w = 0; w < numberOfWays; w++)
        {
            Gizmos.color = Color.white;

            for (int i = 0; i < last; i++)
            {
                Gizmos.DrawLine(points[w, i], points[w, i + 1]);
            }

            // P0
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(points[w, 0], 0.6f);

            // P1
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(points[w, last], 0.6f);
        }

        // ----- P0 CONNECTIONS -----
        Gizmos.color = Color.cyan;

        foreach (var c in p0Connections)
        {
            if (c == null || c.road == null) continue;

            if (c.road.points == null)
                c.road.DrawCurved(false);

            if (c.road.points == null) continue;

            Vector3 target = c.enterAtP0
                ? c.road.points[0, 0]
                : c.road.points[0, c.road.points.GetLength(1) - 1];

            Gizmos.DrawLine(points[0, 0], target);
        }

        // ----- P1 CONNECTIONS -----
        Gizmos.color = Color.yellow;

        foreach (var c in p1Connections)
        {
            if (c == null || c.road == null) continue;

            if (c.road.points == null)
                c.road.DrawCurved(false);

            if (c.road.points == null) continue;

            Vector3 target = c.enterAtP0
                ? c.road.points[0, 0]
                : c.road.points[0, c.road.points.GetLength(1) - 1];

            Gizmos.DrawLine(points[0, last], target);
        }
    }
#endif

}