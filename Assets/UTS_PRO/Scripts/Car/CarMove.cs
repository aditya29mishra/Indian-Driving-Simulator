using System.Collections;
using System.Collections.Generic;
using UnityEngine;

internal enum CarDriveType
{
    FrontWheelDrive,
    RearWheelDrive,
    FourWheelDrive
}

internal enum SpeedType
{
    MPH,
    KPH
}

public class CarMove : MonoBehaviour
{
    private CarWheels carWheels;

    [SerializeField] private CarDriveType m_CarDriveType = CarDriveType.FourWheelDrive;

    //[SerializeField] private WheelEffects[] m_WheelEffects = new WheelEffects[4];
    [SerializeField] private float m_MaximumSteerAngle = 25.0f;
    [Range(0, 1)][SerializeField] private float m_SteerHelper = 0.7f; // 0 is raw physics , 1 the car will grip in the direction it is facing
    [Range(0, 1)][SerializeField] private float m_TractionControl = 1.0f; // 0 is no traction control, 1 is full interference
    [SerializeField] private float m_FullTorqueOverAllWheels = 2000.0f;
    [SerializeField] private float m_ReverseTorque = 150.0f;
    [SerializeField] private float m_MaxHandbrakeTorque = 1e+08f;
    [SerializeField] private float m_Downforce = 100f;
    [SerializeField] private SpeedType m_SpeedType;
    [SerializeField] private float m_Topspeed = 140.0f;
    [SerializeField] private static int NoOfGears = 5;
    [SerializeField] private float m_RevRangeBoundary = 1f;
    [SerializeField] private float m_SlipLimit = 0.4f;
    [SerializeField] private float m_BrakeTorque = 20000.0f;

    private Vector3 m_Prevpos, m_Pos;
    private float m_SteerAngle;
    private int m_GearNum;
    private float m_GearFactor;
    private float m_OldRotation;
    private float m_CurrentTorque;
    private Rigidbody m_Rigidbody;
    private const float k_ReversingThreshold = 0.01f;

    public bool Skidding { get; private set; }
    public float BrakeInput { get; private set; }
    public float CurrentSteerAngle { get { return m_SteerAngle; } }
    public float CurrentSpeed { get { return m_Rigidbody.velocity.magnitude * 2.23693629f; } }
    public float MaxSpeed { get { return m_Topspeed; } }
    public float Revs { get; private set; }
    public float AccelInput { get; private set; }

    private void Awake()
    {
        carWheels = GetComponent<CarWheels>();
    }

    // Use this for initialization
    private void Start()
    {
        m_MaxHandbrakeTorque = float.MaxValue;
        m_Rigidbody = GetComponent<Rigidbody>();
        m_CurrentTorque = m_FullTorqueOverAllWheels - (m_TractionControl * m_FullTorqueOverAllWheels);

        // Validate wheel setup — CarMove expects exactly 4 colliders (0,1 = front, 2,3 = rear).
        // A misconfigured prefab silently returns from Move() every frame; this makes it obvious.
        if (carWheels == null)
            Debug.LogError($"[CarMove] {name}: CarWheels component missing.", this);
        else if (carWheels.WheelColliders == null || carWheels.WheelColliders.Length < 4)
            Debug.LogError($"[CarMove] {name}: WheelColliders needs 4 entries, found {carWheels.WheelColliders?.Length ?? 0}.", this);
        else if (carWheels.tireMeshes == null || carWheels.tireMeshes.Length < 4)
            Debug.LogError($"[CarMove] {name}: tireMeshes needs 4 entries, found {carWheels.tireMeshes?.Length ?? 0}.", this);
    }


    private void GearChanging()
    {
        float f = Mathf.Abs(CurrentSpeed / MaxSpeed);
        float upgearlimit = (1 / (float)NoOfGears) * (m_GearNum + 1);
        float downgearlimit = (1 / (float)NoOfGears) * m_GearNum;

        if (m_GearNum > 0 && f < downgearlimit)
        {
            m_GearNum--;
        }

        if (f > upgearlimit && (m_GearNum < (NoOfGears - 1)))
        {
            m_GearNum++;
        }
    }


    // simple function to add a curved bias towards 1 for a value in the 0-1 range
    private static float CurveFactor(float factor)
    {
        return 1 - (1 - factor) * (1 - factor);
    }


    // unclamped version of Lerp, to allow value to exceed the from-to range
    private static float ULerp(float from, float to, float value)
    {
        return (1.0f - value) * from + value * to;
    }


    private void CalculateGearFactor()
    {
        float f = (1 / (float)NoOfGears);
        // gear factor is a normalised representation of the current speed within the current gear's range of speeds.
        // We smooth towards the 'target' gear factor, so that revs don't instantly snap up or down when changing gear.
        var targetGearFactor = Mathf.InverseLerp(f * m_GearNum, f * (m_GearNum + 1), Mathf.Abs(CurrentSpeed / MaxSpeed));
        m_GearFactor = Mathf.Lerp(m_GearFactor, targetGearFactor, Time.deltaTime * 5f);
    }


    private void CalculateRevs()
    {
        // calculate engine revs (for display / sound)
        // (this is done in retrospect - revs are not used in force/power calculations)
        CalculateGearFactor();
        var gearNumFactor = m_GearNum / (float)NoOfGears;
        var revsRangeMin = ULerp(0f, m_RevRangeBoundary, CurveFactor(gearNumFactor));
        var revsRangeMax = ULerp(m_RevRangeBoundary, 1f, gearNumFactor);
        Revs = ULerp(revsRangeMin, revsRangeMax, m_GearFactor);
    }

    public void SetTopSpeed(float kph)
    {
        m_Topspeed = kph;
        m_SpeedType = SpeedType.KPH;
    }


    public void Move(float accel, float footbrake, float handbrake, float steering = 0)
    {
        if (carWheels == null || carWheels.WheelColliders == null || carWheels.WheelColliders.Length < 4) return;

        //clamp input values
        steering = Mathf.Clamp(steering, -1, 1);
        AccelInput = accel = Mathf.Clamp(accel, 0, 1);
        BrakeInput = footbrake = -1 * Mathf.Clamp(footbrake, -1, 0);
        handbrake = Mathf.Clamp(handbrake, 0, 1);

        //Set the steer on the front wheels.
        //Assuming that wheels 0 and 1 are the front wheels.
        m_SteerAngle = steering * m_MaximumSteerAngle;
        carWheels.WheelColliders[0].steerAngle = m_SteerAngle;
        carWheels.WheelColliders[1].steerAngle = m_SteerAngle;

        SteerHelper();
        ApplyDrive(accel, footbrake);
        CapSpeed();

        //Set the handbrake.
        //Assuming that wheels 2 and 3 are the rear wheels.
        if (handbrake > 0f)
        {
            var hbTorque = handbrake * m_MaxHandbrakeTorque;
            carWheels.WheelColliders[2].brakeTorque = hbTorque;
            carWheels.WheelColliders[3].brakeTorque = hbTorque;
        }


        CalculateRevs();
        GearChanging();

        AddDownForce();
        //CheckForWheelSpin();
        TractionControl();
    }


    private void CapSpeed()
    {
        float speed = m_Rigidbody.velocity.magnitude;
        switch (m_SpeedType)
        {
            case SpeedType.MPH:

                speed *= 2.23693629f;
                if (speed > m_Topspeed)
                    m_Rigidbody.velocity = (m_Topspeed / 2.23693629f) * m_Rigidbody.velocity.normalized;
                break;

            case SpeedType.KPH:
                speed *= 3.6f;
                if (speed > m_Topspeed)
                    m_Rigidbody.velocity = (m_Topspeed / 3.6f) * m_Rigidbody.velocity.normalized;
                break;
        }
    }


    private void ApplyDrive(float accel, float footbrake)
    {
        float thrustTorque;
        switch (m_CarDriveType)
        {
            case CarDriveType.FourWheelDrive:
                thrustTorque = accel * (m_CurrentTorque / 4f);
                for (int i = 0; i < carWheels.WheelColliders.Length; i++)
                    carWheels.WheelColliders[i].motorTorque = thrustTorque;
                break;

            case CarDriveType.FrontWheelDrive:
                thrustTorque = accel * (m_CurrentTorque / 2f);
                carWheels.WheelColliders[0].motorTorque = thrustTorque;
                carWheels.WheelColliders[1].motorTorque = thrustTorque;
                carWheels.WheelColliders[2].motorTorque = 0f;
                carWheels.WheelColliders[3].motorTorque = 0f;
                break;

            case CarDriveType.RearWheelDrive:
                thrustTorque = accel * (m_CurrentTorque / 2f);
                carWheels.WheelColliders[0].motorTorque = 0f;
                carWheels.WheelColliders[1].motorTorque = 0f;
                carWheels.WheelColliders[2].motorTorque = thrustTorque;
                carWheels.WheelColliders[3].motorTorque = thrustTorque;
                break;
        }

        for (int i = 0; i < carWheels.WheelColliders.Length; i++)
        {
            if (footbrake > 0f)
            {
                // Apply brake torque. At speed, only brake (don't fight the motor).
                // At low speed or reversing, also zero motor so braking is clean.
                carWheels.WheelColliders[i].brakeTorque = m_BrakeTorque * footbrake;
                if (CurrentSpeed <= 5f || Vector3.Angle(transform.forward, m_Rigidbody.velocity) >= 50f)
                    carWheels.WheelColliders[i].motorTorque = 0f;
            }
            else
            {
                // No brake input — MUST explicitly clear brakeTorque to 0.
                // WheelCollider.brakeTorque persists across frames; without this
                // the wheels stay locked from the previous braking frame even
                // when throttle is applied (green-light restart deadlock).
                carWheels.WheelColliders[i].brakeTorque = 0f;
            }
        }
    }


    private void SteerHelper()
    {
        for (int i = 0; i < carWheels.WheelColliders.Length; i++)
        {
            WheelHit wheelhit;
            carWheels.WheelColliders[i].GetGroundHit(out wheelhit);
            if (wheelhit.normal == Vector3.zero)
                return; // wheels arent on the ground so dont realign the rigidbody velocity
        }

        // this if is needed to avoid gimbal lock problems that will make the car suddenly shift direction
        if (Mathf.Abs(m_OldRotation - transform.eulerAngles.y) < 10f)
        {
            var turnadjust = (transform.eulerAngles.y - m_OldRotation) * m_SteerHelper;
            Quaternion velRotation = Quaternion.AngleAxis(turnadjust, Vector3.up);
            m_Rigidbody.velocity = velRotation * m_Rigidbody.velocity;
            
        }
        m_OldRotation = transform.eulerAngles.y;
    }


    // this is used to add more grip in relation to speed
    private void AddDownForce()
    {
        if (m_Rigidbody == null) return;
        if (carWheels == null || carWheels.WheelColliders == null || carWheels.WheelColliders.Length == 0) return;
        // Use the cached m_Rigidbody directly — WheelCollider.attachedRigidbody is null
        // until the physics scene processes the newly spawned GameObject (can be null on frame 0).
        m_Rigidbody.AddForce(-transform.up * m_Downforce * m_Rigidbody.velocity.magnitude);
    }


    // checks if the wheels are spinning and is so does three things
    // 1) emits particles
    // 2) plays tiure skidding sounds
    // 3) leaves skidmarks on the ground
    // these effects are controlled through the WheelEffects class
    /*private void CheckForWheelSpin()
    {
        // loop through all wheels
        for (int i = 0; i < 4; i++)
        {
            WheelHit wheelHit;
            m_WheelColliders[i].GetGroundHit(out wheelHit);

            // is the tire slipping above the given threshhold
            if (Mathf.Abs(wheelHit.forwardSlip) >= m_SlipLimit || Mathf.Abs(wheelHit.sidewaysSlip) >= m_SlipLimit)
            {
                m_WheelEffects[i].EmitTyreSmoke();

                // avoiding all four tires screeching at the same time
                // if they do it can lead to some strange audio artefacts
                if (!AnySkidSoundPlaying())
                {
                    m_WheelEffects[i].PlayAudio();
                }
                continue;
            }

            // if it wasnt slipping stop all the audio
            if (m_WheelEffects[i].PlayingAudio)
            {
                m_WheelEffects[i].StopAudio();
            }
            // end the trail generation
            m_WheelEffects[i].EndSkidTrail();
        }
    }*/

    // crude traction control that reduces the power to wheel if the car is wheel spinning too much
    private void TractionControl()
    {
        WheelHit wheelHit;
        switch (m_CarDriveType)
        {
            case CarDriveType.FourWheelDrive:
                // loop through all wheels
                for (int i = 0; i < carWheels.WheelColliders.Length; i++)
                {
                    carWheels.WheelColliders[i].GetGroundHit(out wheelHit);

                    AdjustTorque(wheelHit.forwardSlip);
                }
                break;

            case CarDriveType.RearWheelDrive:
                carWheels.WheelColliders[2].GetGroundHit(out wheelHit);
                AdjustTorque(wheelHit.forwardSlip);

                carWheels.WheelColliders[3].GetGroundHit(out wheelHit);
                AdjustTorque(wheelHit.forwardSlip);
                break;

            case CarDriveType.FrontWheelDrive:
                carWheels.WheelColliders[0].GetGroundHit(out wheelHit);
                AdjustTorque(wheelHit.forwardSlip);

                carWheels.WheelColliders[1].GetGroundHit(out wheelHit);
                AdjustTorque(wheelHit.forwardSlip);
                break;
        }
    }


    private void AdjustTorque(float forwardSlip)
    {
        if (forwardSlip >= m_SlipLimit && m_CurrentTorque >= 0)
        {
            m_CurrentTorque -= 10 * m_TractionControl;
        }
        else
        {
            m_CurrentTorque += 10 * m_TractionControl;
            if (m_CurrentTorque > m_FullTorqueOverAllWheels)
            {
                m_CurrentTorque = m_FullTorqueOverAllWheels;
            }
        }
    }


    /*private bool AnySkidSoundPlaying()
    {
        for (int i = 0; i < 4; i++)
        {
            if (m_WheelEffects[i].PlayingAudio)
            {
                return true;
            }
        }
        return false;
    }*/
}