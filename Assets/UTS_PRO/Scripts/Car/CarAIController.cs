using UnityEngine;

[RequireComponent(typeof (CarMove))]
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

    [SerializeField] [Tooltip("Vehicle Speed / Скорость автомобиля")] private float moveSpeed;
    [SerializeField] [Tooltip("Acceleration of the car / Ускорение автомобиля")] private float speedIncrease;
    [SerializeField] [Tooltip("Deceleration of the car / Торможение автомобиля")] private float speedDecrease;
    [SerializeField] [Tooltip("Distance to the car for braking / Дистанция до автомобиля для торможения")] private float distanceToCar;
    [SerializeField] [Tooltip("Distance to the traffic light for braking / Дистанция до светофора для торможения")] private float distanceToSemaphore;
    [SerializeField] [Tooltip("Maximum rotation angle for braking / Максимальный угол поворота для притормаживания")] private float maxAngleToMoveBreak = 8.0f;

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
        get{return distanceToCar;}
        set{distanceToCar = value;}
    }

    public float TO_SEMAPHORE
    {
        get{return distanceToSemaphore;}
        set{distanceToSemaphore = value;}
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

        if(carMove != null && isACar) carMove.Move(curMoveSpeed, 0, 0);
    }

    private void FixedUpdate()
    {
        GetPath();
        Drive();

        if(moveBrake)
        {
            moveSpeed = startSpeed * 0.5f;
        }
    }

    private void GetPath()
    {
        Vector3 targetPos = new Vector3(movePath.finishPos.x, rigbody.transform.position.y, movePath.finishPos.z);
        var richPointDistance = Vector3.Distance(Vector3.ProjectOnPlane(rigbody.transform.position, Vector3.up),
            Vector3.ProjectOnPlane(movePath.finishPos, Vector3.up));

        if (richPointDistance < 5.0f && ((movePath.loop) || (!movePath.loop && movePath.targetPoint > 0 && movePath.targetPoint < movePath.targetPointsTotal)))
        {
            if (movePath.forward)
            {
                if (movePath.targetPoint < movePath.targetPointsTotal)
                {
                    targetPos = movePath.walkPath.getNextPoint(movePath.w, movePath.targetPoint + 1);
                }
                else
                {
                    targetPos = movePath.walkPath.getNextPoint(movePath.w, 0);
                }

                targetPos.y = rigbody.transform.position.y;
            }
            else
            {
                if (movePath.targetPoint > 0)
                {
                    targetPos = movePath.walkPath.getNextPoint(movePath.w, movePath.targetPoint - 1);
                }
                else
                {
                    targetPos = movePath.walkPath.getNextPoint(movePath.w, movePath.targetPointsTotal);
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

        if(richPointDistance < 10.0f)
        {
            if(movePath.nextFinishPos != Vector3.zero)
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
                movePath.finishPos = movePath.walkPath.getNextPoint(movePath.w, movePath.targetPoint);

                if(movePath.targetPoint != movePath.targetPointsTotal)
                {
                    movePath.nextFinishPos = movePath.walkPath.getNextPoint(movePath.w, movePath.targetPoint + 1);
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
                    movePath.walkPath.SpawnPoints[movePath.w].AddToSpawnQuery(new MovePathParams { });
                    Destroy(gameObject);
                }
            }

        }
        else if (richPointDistance <= movePath._walkPointThreshold && !movePath.forward)
        {
            if (movePath.targetPoint > 0)
            {
                movePath.targetPoint--;

                movePath.finishPos = movePath.walkPath.getNextPoint(movePath.w, movePath.targetPoint);

                if(movePath.targetPoint > 0)
                {
                    movePath.nextFinishPos = movePath.walkPath.getNextPoint(movePath.w, movePath.targetPoint - 1);
                }
            }
            else if (movePath.targetPoint == 0)
            {
                if (movePath.loop)
                {
                    movePath.finishPos = movePath.walkPath.getNextPoint(movePath.w, movePath.targetPointsTotal);

                    movePath.targetPoint = movePath.targetPointsTotal;
                }
                else
                {
                    movePath.walkPath.SpawnPoints[movePath.w].AddToSpawnQuery(new MovePathParams { });
                    Destroy(gameObject);
                }
            }
        }
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

        Ray fwdRay = new Ray(fwdVector, transform.forward * 20) ;
        Ray LRay = new Ray(LRVector - transform.right, transform.forward * 20);
        Ray RRay = new Ray(LRVector + transform.right, transform.forward * 20);

        if(Physics.Raycast(fwdRay, out hit, 20) || Physics.Raycast(LRay, out hit, 20) || Physics.Raycast(RRay, out hit, 20))
        {
            float distance = Vector3.Distance(fwdVector, hit.point);

            if (hit.transform.CompareTag("Car"))
            {        
                GameObject car = (hit.transform.GetComponentInChildren<ParentOfTrailer>()) ? hit.transform.GetComponent<ParentOfTrailer>().PAR : hit.transform.gameObject;

                if(car != null)
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
                if(!moveBrake)
                {
                    moveSpeed = startSpeed;
                }
                tempStop = false;
            }
        }
        else
        {
            if(!moveBrake)
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