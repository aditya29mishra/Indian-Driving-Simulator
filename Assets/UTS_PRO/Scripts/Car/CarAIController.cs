using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(CarMove))]
public class CarAIController : MonoBehaviour
{
    private Rigidbody rigbody;
    private BoxCollider bc;
    private MovePath movePath;
    private CarMove carMove;
    private Vector3 fwdVector;
    private Vector3 LRVector;
    private float startSpeed;
    [SerializeField] private float curMoveSpeed;
    [SerializeField] private float angleBetweenPoint;
    private float targetSteerAngle;
    private float upTurnTimer;
    private bool moveBrake;
    private bool isACar;
    private bool isABike;
    public bool tempStop;
    private bool insideSemaphore;
    private bool hasTrailer;

    // Prevents HandleIntersection from firing multiple times while still at junction
    private bool _intersectionLock;
    private float _intersectionLockCooldown;
    private const float IntersectionLockDuration = 0.6f;

    [SerializeField][Tooltip("Vehicle Speed / Скорость автомобиля")] private float moveSpeed;
    [SerializeField][Tooltip("Acceleration of the car / Ускорение автомобиля")] private float speedIncrease;
    [SerializeField][Tooltip("Deceleration of the car / Торможение автомобиля")] private float speedDecrease;
    [SerializeField][Tooltip("Distance to the car for braking / Дистанция до автомобиля для торможения")] private float distanceToCar;
    [SerializeField][Tooltip("Distance to the traffic light for braking / Дистанция до светофора для торможения")] private float distanceToSemaphore;
    [SerializeField][Tooltip("Maximum rotation angle for braking / Максимальный угол поворота для притормаживания")] private float maxAngleToMoveBreak = 8.0f;

    public float MOVE_SPEED
    {
        get { return moveSpeed; }
        set { moveSpeed = value; }
    }

    public float INCREASE
    {
        get { return speedIncrease; }
        set { speedIncrease = value; }
    }

    public float DECREASE
    {
        get { return speedDecrease; }
        set { speedDecrease = value; }
    }

    public float START_SPEED
    {
        get { return startSpeed; }
        private set { }
    }

    public float TO_CAR
    {
        get { return distanceToCar; }
        set { distanceToCar = value; }
    }

    public float TO_SEMAPHORE
    {
        get { return distanceToSemaphore; }
        set { distanceToSemaphore = value; }
    }

    public float MaxAngle
    {
        get { return maxAngleToMoveBreak; }
        set { maxAngleToMoveBreak = value; }
    }

    public bool INSIDE
    {
        get { return insideSemaphore; }
        set { insideSemaphore = value; }
    }

    public bool TEMP_STOP
    {
        get { return tempStop; }
        private set { }
    }

    private void Awake()
    {
        rigbody = GetComponent<Rigidbody>();
        movePath = GetComponent<MovePath>();
        carMove = GetComponent<CarMove>();
    }

    private void Start()
    {
        startSpeed = moveSpeed;

        WheelCollider[] wheelColliders = GetComponentsInChildren<WheelCollider>();

        if (wheelColliders.Length > 2)
        {
            isACar = true;
        }
        else
        {
            isABike = true;
        }

        BoxCollider[] box = GetComponentsInChildren<BoxCollider>();
        bc = (isACar) ? box[0] : box[1];

        if (GetComponent<AddTrailer>())
        {
            hasTrailer = true;
        }
    }

    private void Update()
    {
        fwdVector = new Vector3(transform.position.x + (transform.forward.x * bc.size.z / 2 + 0.1f), transform.position.y + 0.5f, transform.position.z + (transform.forward.z * bc.size.z / 2 + 0.1f));
        LRVector = new Vector3(transform.position.x + (transform.forward.x * bc.size.z / 2 + 0.1f), transform.position.y + 0.5f, transform.position.z + (transform.forward.z * bc.size.z / 2 + 0.1f));

        PushRay();

        if (carMove != null && isACar) carMove.Move(curMoveSpeed, 0, 0);
    }

    private void FixedUpdate()
    {
        if (_intersectionLockCooldown > 0f)
        {
            _intersectionLockCooldown -= Time.fixedDeltaTime;
            if (_intersectionLockCooldown <= 0f)
                _intersectionLock = false;
        }

        GetPath();
        Drive();

        if (moveBrake)
        {
            moveSpeed = startSpeed * 0.5f;
        }
    }

    private static int ClampWaypointIndex(WalkPath path, int lane, int index)
    {
        if (path == null) return 0;
        int total = path.getPointsTotal(lane);
        if (total <= 0) return 0;
        return Mathf.Clamp(index, 0, total - 1);
    }

    private void GetPath()
    {
        if (movePath.walkPath == null) return;

        Vector3 targetPos = new Vector3(movePath.finishPos.x, rigbody.transform.position.y, movePath.finishPos.z);
        var richPointDistance = Vector3.Distance(Vector3.ProjectOnPlane(rigbody.transform.position, Vector3.up),
            Vector3.ProjectOnPlane(movePath.finishPos, Vector3.up));

        int w = movePath.w;
        WalkPath path = movePath.walkPath;

        if (richPointDistance < 5.0f && ((movePath.loop) || (!movePath.loop && movePath.targetPoint > 0 && movePath.targetPoint < movePath.targetPointsTotal)))
        {
            if (movePath.forward)
            {
                if (movePath.targetPoint < movePath.targetPointsTotal)
                {
                    int nextIdx = ClampWaypointIndex(path, w, movePath.targetPoint + 1);
                    targetPos = path.getNextPoint(w, nextIdx);
                }
                else
                {
                    targetPos = path.getNextPoint(w, ClampWaypointIndex(path, w, 0));
                }

                targetPos.y = rigbody.transform.position.y;
            }
            else
            {
                if (movePath.targetPoint > 0)
                {
                    int prevIdx = ClampWaypointIndex(path, w, movePath.targetPoint - 1);
                    targetPos = path.getNextPoint(w, prevIdx);
                }
                else
                {
                    int lastIdx = ClampWaypointIndex(path, w, movePath.targetPointsTotal);
                    targetPos = path.getNextPoint(w, lastIdx);
                }

                targetPos.y = rigbody.transform.position.y;
            }
        }

        if (!isACar)
        {
            Vector3 targetVector = targetPos - rigbody.transform.position;

            if (targetVector != Vector3.zero)
            {
                Quaternion look = Quaternion.identity;

                look = Quaternion.Lerp(rigbody.transform.rotation, Quaternion.LookRotation(targetVector),
                    Time.fixedDeltaTime * 4f);

                look.x = rigbody.transform.rotation.x;
                look.z = rigbody.transform.rotation.z;

                rigbody.transform.rotation = look;
            }
        }

        if (richPointDistance < 10.0f)
        {
            if (movePath.nextFinishPos != Vector3.zero)
            {
                Vector3 targetDirection = movePath.nextFinishPos - transform.position;
                angleBetweenPoint = (Mathf.Abs(Vector3.SignedAngle(targetDirection, transform.forward, Vector3.up)));

                if (angleBetweenPoint > maxAngleToMoveBreak)
                {
                    moveBrake = true;
                }
            }
        }
        else
        {
            moveBrake = false;
        }

        if (richPointDistance > movePath._walkPointThreshold)
        {
            if (Time.deltaTime > 0)
            {
                Vector3 velocity = movePath.finishPos - rigbody.transform.position;

                if (!isACar)
                {
                    velocity.y = rigbody.velocity.y;
                    rigbody.velocity = new Vector3(velocity.normalized.x * curMoveSpeed, velocity.y, velocity.normalized.z * curMoveSpeed);
                }
                else
                {
                    velocity.y = rigbody.velocity.y;
                }
            }
        }
        else if (richPointDistance <= movePath._walkPointThreshold && movePath.forward)
        {
            if (movePath.targetPoint != movePath.targetPointsTotal)
            {
                movePath.targetPoint++;
                movePath.targetPoint = Mathf.Min(movePath.targetPoint, movePath.targetPointsTotal);
                int idx = ClampWaypointIndex(path, w, movePath.targetPoint);
                movePath.finishPos = path.getNextPoint(w, idx);
                TrafficDebugger.Waypoint(
                    $"{name} lane {movePath.w} -> waypoint {movePath.targetPoint}"
                );

                if (movePath.targetPoint != movePath.targetPointsTotal)
                {
                    int nextIdx = ClampWaypointIndex(path, w, movePath.targetPoint + 1);
                    movePath.nextFinishPos = path.getNextPoint(w, nextIdx);
                }
            }
            else if (movePath.targetPoint == movePath.targetPointsTotal)
            {
                if (movePath.loop)
                {
                    movePath.finishPos = movePath.walkPath.getStartPoint(movePath.w);
                    movePath.targetPoint = 0;
                }
                else
                {
                    HandleIntersection();
                }
            }
        }
        else if (richPointDistance <= movePath._walkPointThreshold && !movePath.forward)
        {
            if (movePath.targetPoint > 0)
            {
                movePath.targetPoint--;
                movePath.finishPos = path.getNextPoint(w, ClampWaypointIndex(path, w, movePath.targetPoint));

                if (movePath.targetPoint > 0)
                {
                    movePath.nextFinishPos = path.getNextPoint(w, ClampWaypointIndex(path, w, movePath.targetPoint - 1));
                }
            }
            else if (movePath.targetPoint == 0)
            {
                if (movePath.loop)
                {
                    int lastIdx = ClampWaypointIndex(path, w, movePath.targetPointsTotal);
                    movePath.finishPos = path.getNextPoint(w, lastIdx);
                    movePath.targetPoint = movePath.targetPointsTotal;
                }
                else
                {
                    HandleIntersection();
                }
            }
            TrafficDebugger.Waypoint(
                $"{name} Lane {movePath.w} -> Next point {movePath.targetPoint}"
            );
        }
    }
    void HandleIntersection()
    {
        if (_intersectionLock)
            return;

        CarWalkPath currentRoad = movePath.walkPath as CarWalkPath;
        if (currentRoad == null)
        {
            Destroy(gameObject);
            return;
        }

        List<CarWalkPath.RoadConnection> connections =
            movePath.targetPoint == movePath.targetPointsTotal
                ? currentRoad.p1Connections
                : currentRoad.p0Connections;

        if (connections == null || connections.Count == 0)
        {
            TrafficDebugger.Intersection($"{name} reached dead-end road");
            Destroy(gameObject);
            return;
        }

        var next = connections[Random.Range(0, connections.Count)];
        CarWalkPath nextRoad = next.road;
        if (nextRoad == null)
        {
            TrafficDebugger.Intersection($"{name} connection has no road");
            Destroy(gameObject);
            return;
        }

        _intersectionLock = true;
        _intersectionLockCooldown = IntersectionLockDuration;

        TrafficDebugger.Intersection($"{name} switching road → {nextRoad.name}");

        MovePath mp = GetComponent<MovePath>();
        mp.walkPath = nextRoad;

        bool forward = next.enterAtP0;
        int laneID = currentRoad.GetLaneID(movePath.w);
        int targetLane = nextRoad.GetLaneIndex(laneID);
        targetLane = Mathf.Clamp(targetLane, 0, nextRoad.numberOfWays - 1);

        // All lanes share same point count; use lane 0 (WalkPath only sets pointLength[0])
        int totalPoints = nextRoad.getPointsTotal(0);
        if (totalPoints < 2)
        {
            TrafficDebugger.Intersection($"{name} target road has too few points");
            _intersectionLock = false;
            Destroy(gameObject);
            return;
        }

        // Last valid waypoint index for driving (points 0 and totalPoints-1 are duplicates of 1 and totalPoints-2)
        int targetPointsTotal = Mathf.Max(0, totalPoints - 2);

        int startPoint;
        if (forward)
            startPoint = totalPoints >= 3 ? 1 : 0;
        else
            startPoint = Mathf.Min(targetPointsTotal, totalPoints - 2);

        startPoint = Mathf.Clamp(startPoint, 0, totalPoints - 1);

        mp.w = targetLane;
        mp.forward = forward;
        mp.loop = nextRoad.loopPath;
        mp.targetPoint = startPoint;
        mp.targetPointsTotal = targetPointsTotal;

        int finishIdx = ClampWaypointIndex(nextRoad, targetLane, startPoint);
        mp.finishPos = nextRoad.getNextPoint(targetLane, finishIdx);

        if (forward)
        {
            int nextIdx = ClampWaypointIndex(nextRoad, targetLane, startPoint + 1);
            mp.nextFinishPos = nextIdx != finishIdx
                ? nextRoad.getNextPoint(targetLane, nextIdx)
                : mp.finishPos;
        }
        else
        {
            int nextIdx = ClampWaypointIndex(nextRoad, targetLane, startPoint - 1);
            mp.nextFinishPos = nextIdx != finishIdx
                ? nextRoad.getNextPoint(targetLane, nextIdx)
                : mp.finishPos;
        }

        transform.position = mp.finishPos;
        Vector3 dir = mp.nextFinishPos - mp.finishPos;
        if (dir.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.LookRotation(dir);

        moveBrake = false;

        TrafficDebugger.Lane(
            $"{name} entered lane {targetLane} on {nextRoad.name} | Forward {forward} | Waypoint {startPoint}/{targetPointsTotal}"
        );
    }




    private void Drive()
    {
        CarWheels wheels = GetComponent<CarWheels>();

        if (tempStop)
        {
            if (hasTrailer)
            {
                curMoveSpeed = Mathf.Lerp(curMoveSpeed, 0.0f, Time.fixedDeltaTime * (speedDecrease * 2.5f));
            }
            else
            {
                curMoveSpeed = Mathf.Lerp(curMoveSpeed, 0, Time.fixedDeltaTime * speedDecrease);
            }

            if (curMoveSpeed < 0.15f)
            {
                curMoveSpeed = 0.0f;
            }
        }
        else
        {
            curMoveSpeed = Mathf.Lerp(curMoveSpeed, moveSpeed, Time.fixedDeltaTime * speedIncrease);
        }

        CarOverturned();

        if (isACar)
        {
            for (int wheelIndex = 0; wheelIndex < wheels.WheelColliders.Length; wheelIndex++)
            {
                if (wheels.WheelColliders[wheelIndex].transform.localPosition.z > 0)
                {
                    wheels.WheelColliders[wheelIndex].steerAngle = Mathf.Clamp(CarWheelsRotation.AngleSigned(transform.forward, movePath.finishPos - transform.position, transform.up), -30.0f, 30.0f);
                }
            }
        }

        if (rigbody.velocity.magnitude > curMoveSpeed)
        {
            rigbody.velocity = rigbody.velocity.normalized * curMoveSpeed;
        }
    }

    private void CarOverturned()
    {
        WheelCollider[] wheels = GetComponent<CarWheels>().WheelColliders;

        bool removal = false;
        int number = wheels.Length;

        foreach (var item in wheels)
        {
            if (!item.isGrounded)
            {
                number--;
            }
        }

        if (number == 0)
        {
            removal = true;
        }

        if (removal)
        {
            upTurnTimer += Time.deltaTime;
        }
        else
        {
            upTurnTimer = 0;
        }

        if (upTurnTimer > 3)
        {
            Destroy(gameObject);
        }
    }

    private void PushRay()
    {
        RaycastHit hit;

        Ray fwdRay = new Ray(fwdVector, transform.forward * 20);
        Ray LRay = new Ray(LRVector - transform.right, transform.forward * 20);
        Ray RRay = new Ray(LRVector + transform.right, transform.forward * 20);

        if (Physics.Raycast(fwdRay, out hit, 20) || Physics.Raycast(LRay, out hit, 20) || Physics.Raycast(RRay, out hit, 20))
        {
            float distance = Vector3.Distance(fwdVector, hit.point);

            if (hit.transform.CompareTag("Car"))
            {
                GameObject car = (hit.transform.GetComponentInChildren<ParentOfTrailer>()) ? hit.transform.GetComponent<ParentOfTrailer>().PAR : hit.transform.gameObject;

                if (car != null)
                {
                    MovePath MP = car.GetComponent<MovePath>();

                    if (MP.w == movePath.w)
                    {
                        ReasonsStoppingCars.CarInView(car, rigbody, distance, startSpeed, ref moveSpeed, ref tempStop, distanceToCar);
                    }
                }
            }
            else if (hit.transform.CompareTag("Bcycle"))
            {
                ReasonsStoppingCars.BcycleGyroInView(hit.transform.GetComponentInChildren<BcycleGyroController>(), rigbody, distance, startSpeed, ref moveSpeed, ref tempStop);
            }
            else if (hit.transform.CompareTag("PeopleSemaphore"))
            {
                ReasonsStoppingCars.SemaphoreInView(hit.transform.GetComponent<SemaphorePeople>(), distance, startSpeed, insideSemaphore, ref moveSpeed, ref tempStop, distanceToSemaphore);
            }
            else if (hit.transform.CompareTag("Player") || hit.transform.CompareTag("People"))
            {
                ReasonsStoppingCars.PlayerInView(hit.transform, distance, startSpeed, ref moveSpeed, ref tempStop);
            }
            else
            {
                if (!moveBrake)
                {
                    moveSpeed = startSpeed;
                }
                tempStop = false;
            }
        }
        else
        {
            if (!moveBrake)
            {
                moveSpeed = startSpeed;
            }

            tempStop = false;
        }
    }

    /*private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;

        if (bc != null)
        {
            Gizmos.DrawRay(new Vector3(transform.position.x + transform.forward.x * bc.size.z / 2, transform.position.y + 0.5f, transform.position.z + transform.forward.z * bc.size.z / 2), transform.forward * 20);
            Gizmos.DrawRay(new Vector3(transform.position.x + transform.forward.x * bc.size.z / 2, transform.position.y + 0.5f, transform.position.z + transform.forward.z * bc.size.z / 2) + transform.right, transform.forward * 20);
            Gizmos.DrawRay(new Vector3(transform.position.x + transform.forward.x * bc.size.z / 2, transform.position.y + 0.5f, transform.position.z + transform.forward.z * bc.size.z / 2) - transform.right, transform.forward * 20);
        }
    }*/
}