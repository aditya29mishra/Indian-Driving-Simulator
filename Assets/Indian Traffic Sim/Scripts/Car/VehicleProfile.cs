using UnityEngine;

public enum VehicleType { Bike, Rickshaw, Car, Bus, Truck }
public enum CarDriveTypePublic { FrontWheelDrive, RearWheelDrive, FourWheelDrive }

[System.Serializable]
public class PhysicalSpec
{
    public float width, length, height, turningRadius, mass;
    public int   wheelCount;
    public CarDriveTypePublic driveType;
    public PhysicalSpec(float width, float length, float height, float turningRadius,
                        float mass, int wheelCount, CarDriveTypePublic driveType)
    { this.width=width; this.length=length; this.height=height; this.turningRadius=turningRadius;
      this.mass=mass; this.wheelCount=wheelCount; this.driveType=driveType; }
}

[System.Serializable]
public class AgilitySpec
{
    public float maxSpeedKph, desiredSpeedMs, acceleration, braking;
    [Range(0f,1f)] public float lateralAgility;
    public float maxLateralOffsetM, minPassableGapM, maxSteerAngle;
    public bool canLaneSplit, canGapThread;
    public AgilitySpec(float maxSpeedKph, float desiredSpeedMs, float acceleration, float braking,
                       float lateralAgility, float maxLateralOffsetM, float minPassableGapM,
                       bool canLaneSplit, bool canGapThread, float maxSteerAngle)
    { this.maxSpeedKph=maxSpeedKph; this.desiredSpeedMs=desiredSpeedMs; this.acceleration=acceleration;
      this.braking=braking; this.lateralAgility=lateralAgility; this.maxLateralOffsetM=maxLateralOffsetM;
      this.minPassableGapM=minPassableGapM; this.canLaneSplit=canLaneSplit; this.canGapThread=canGapThread;
      this.maxSteerAngle=maxSteerAngle; }
}

[System.Serializable]
public class SocialSpec
{
    [Range(0f,1f)] public float assertiveness;
    public float yieldGapThresholdM, socialWeight;
    [Range(0f,1f)] public float honkProbability;
    public bool creepsAtSignal, jumpsPrelightGreen, ignoresLaneLines, canRoadsideStop;
    public float timeHeadway, minimumGap;
    public SocialSpec(float assertiveness, float yieldGapThresholdM, float socialWeight,
                      float honkProbability, bool creepsAtSignal, bool jumpsPrelightGreen,
                      bool ignoresLaneLines, bool canRoadsideStop, float timeHeadway, float minimumGap)
    { this.assertiveness=assertiveness; this.yieldGapThresholdM=yieldGapThresholdM; this.socialWeight=socialWeight;
      this.honkProbability=honkProbability; this.creepsAtSignal=creepsAtSignal; this.jumpsPrelightGreen=jumpsPrelightGreen;
      this.ignoresLaneLines=ignoresLaneLines; this.canRoadsideStop=canRoadsideStop;
      this.timeHeadway=timeHeadway; this.minimumGap=minimumGap; }
}

public class VehicleProfile
{
    public VehicleType   Type     { get; private set; }
    public PhysicalSpec  Physical { get; private set; }
    public AgilitySpec   Agility  { get; private set; }
    public SocialSpec    Social   { get; private set; }
    public VehicleProfile(VehicleType type, PhysicalSpec physical, AgilitySpec agility, SocialSpec social)
    { Type=type; Physical=physical; Agility=agility; Social=social; }
    public float Width            => Physical.width;
    public float Length           => Physical.length;
    public float MinPassableGap   => Agility.minPassableGapM;
    public float LateralAgility   => Agility.lateralAgility;
    public float MaxLateralOffset => Agility.maxLateralOffsetM;
    public bool  CanLaneSplit     => Agility.canLaneSplit;
    public bool  CanGapThread     => Agility.canGapThread;
    public bool  IgnoresLaneLines => Social.ignoresLaneLines;
    public float Assertiveness    => Social.assertiveness;
    public float TimeHeadway      => Social.timeHeadway;
    public float MinimumGap       => Social.minimumGap;
}

public static class VehicleProfileFactory
{
    public static VehicleProfile Create(VehicleType type)
    {
        switch (type)
        {
            case VehicleType.Bike:     return CreateBike();
            case VehicleType.Rickshaw: return CreateRickshaw();
            case VehicleType.Car:      return CreateCar();
            case VehicleType.Bus:      return CreateBus();
            case VehicleType.Truck:    return CreateTruck();
            default:                   return CreateCar();
        }
    }
    static VehicleProfile CreateBike() => new VehicleProfile(VehicleType.Bike,
        new PhysicalSpec(0.85f,2.0f,1.2f,3.5f,180f,2,CarDriveTypePublic.RearWheelDrive),
        new AgilitySpec(70f,11f,4.0f,7.0f,0.95f,1.4f,0.8f,true,true,40f),
        new SocialSpec(0.80f,0.5f,0.3f,0.75f,true,true,true,false,0.8f,0.8f));
    static VehicleProfile CreateRickshaw() => new VehicleProfile(VehicleType.Rickshaw,
        new PhysicalSpec(1.3f,3.0f,1.7f,4.5f,350f,3,CarDriveTypePublic.RearWheelDrive),
        new AgilitySpec(50f,8f,2.0f,5.5f,0.80f,1.0f,1.4f,false,true,45f),
        new SocialSpec(0.85f,1.0f,0.5f,0.90f,true,true,true,true,1.0f,1.2f));
    static VehicleProfile CreateCar() => new VehicleProfile(VehicleType.Car,
        new PhysicalSpec(1.9f,4.5f,1.5f,5.5f,1200f,4,CarDriveTypePublic.FourWheelDrive),
        new AgilitySpec(80f,13f,2.5f,6.0f,0.30f,0.4f,2.2f,false,false,25f),
        new SocialSpec(0.55f,2.5f,1.0f,0.40f,false,false,false,false,1.5f,2.0f));
    static VehicleProfile CreateBus() => new VehicleProfile(VehicleType.Bus,
        new PhysicalSpec(2.5f,11.0f,3.2f,12.0f,8000f,4,CarDriveTypePublic.RearWheelDrive),
        new AgilitySpec(60f,10f,1.2f,4.0f,0.05f,0.15f,3.0f,false,false,20f),
        new SocialSpec(0.70f,3.0f,4.0f,0.30f,false,false,false,true,2.5f,4.0f));
    static VehicleProfile CreateTruck() => new VehicleProfile(VehicleType.Truck,
        new PhysicalSpec(2.5f,8.5f,3.5f,14.0f,12000f,4,CarDriveTypePublic.RearWheelDrive),
        new AgilitySpec(50f,9f,0.9f,3.5f,0.02f,0.1f,3.2f,false,false,18f),
        new SocialSpec(0.65f,3.5f,6.0f,0.25f,false,false,false,false,3.0f,5.0f));
}