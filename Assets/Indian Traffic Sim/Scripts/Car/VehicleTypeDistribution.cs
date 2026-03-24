using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class VehicleTypeDistribution
{
    [Header("Spawn Weights")]
    [Range(0f,1f)] public float bikeWeight     = 0.55f;
    [Range(0f,1f)] public float rickshawWeight = 0.12f;
    [Range(0f,1f)] public float carWeight      = 0.25f;
    [Range(0f,1f)] public float busWeight      = 0.05f;
    [Range(0f,1f)] public float truckWeight    = 0.03f;

    [Header("Prefab Pools")]
    public GameObject[] bikePrefabs;
    public GameObject[] rickshawPrefabs;
    public GameObject[] carPrefabs;
    public GameObject[] busPrefabs;
    public GameObject[] truckPrefabs;

    public VehicleType PickType()
    {
        float total = bikeWeight + rickshawWeight + carWeight + busWeight + truckWeight;
        if (total <= 0f) return VehicleType.Car;
        float r = Random.value * total;
        if ((r -= bikeWeight)    <= 0f) return VehicleType.Bike;
        if ((r -= rickshawWeight)<= 0f) return VehicleType.Rickshaw;
        if ((r -= carWeight)     <= 0f) return VehicleType.Car;
        if ((r -= busWeight)     <= 0f) return VehicleType.Bus;
        return VehicleType.Truck;
    }

    public GameObject PickPrefab(VehicleType type)
    {
        GameObject[] pool = GetPool(type);
        if (pool == null || pool.Length == 0)
        {
            if (carPrefabs != null && carPrefabs.Length > 0)
                return carPrefabs[Random.Range(0, carPrefabs.Length)];
            return null;
        }
        return pool[Random.Range(0, pool.Length)];
    }

    GameObject[] GetPool(VehicleType type)
    {
        switch (type)
        {
            case VehicleType.Bike:     return bikePrefabs;
            case VehicleType.Rickshaw: return rickshawPrefabs;
            case VehicleType.Car:      return carPrefabs;
            case VehicleType.Bus:      return busPrefabs;
            case VehicleType.Truck:    return truckPrefabs;
            default:                   return carPrefabs;
        }
    }

    public void Validate()
    {
        Check(VehicleType.Bike,     bikeWeight,     bikePrefabs);
        Check(VehicleType.Rickshaw, rickshawWeight, rickshawPrefabs);
        Check(VehicleType.Car,      carWeight,      carPrefabs);
        Check(VehicleType.Bus,      busWeight,      busPrefabs);
        Check(VehicleType.Truck,    truckWeight,    truckPrefabs);
    }
    void Check(VehicleType type, float weight, GameObject[] pool)
    {
        if (weight > 0f && (pool == null || pool.Length == 0))
            Debug.LogWarning($"[VehicleTypeDistribution] {type} weight={weight:F2} but no prefabs assigned — will fallback to car prefabs.");
    }
}