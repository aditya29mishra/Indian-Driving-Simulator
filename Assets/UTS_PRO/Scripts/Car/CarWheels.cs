using UnityEngine;

public class CarWheels : MonoBehaviour
{
    public WheelCollider[] WheelColliders;
    public Transform[] tireMeshes;
    public bool useCustomCenterOfMass = false;
    public Vector3 centerOfMassOffset;

    private Rigidbody rb;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    void Start()
    {
        CheckCenterOfMass();
    }

    void Update()
    {
        UpdateMeshesPositions();
    }

    private void CheckCenterOfMass()
    {
        if (!useCustomCenterOfMass) return;
        if (rb == null) rb = GetComponent<Rigidbody>();
        if (rb != null)
            rb.centerOfMass = centerOfMassOffset;
    }

    private void UpdateMeshesPositions()
    {
        if (WheelColliders == null || tireMeshes == null) return;

        int count = Mathf.Min(WheelColliders.Length, tireMeshes.Length);
        for (int i = 0; i < count; i++)
        {
            if (WheelColliders[i] == null || tireMeshes[i] == null) continue;

            WheelColliders[i].GetWorldPose(out Vector3 pos, out Quaternion quat);
            tireMeshes[i].position = pos;
            tireMeshes[i].rotation = quat;
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        // Do not call CheckCenterOfMass here — GetComponent is expensive every
        // gizmo draw and throws if Rigidbody is missing on an unfinished prefab.
        // Center of mass is applied at runtime in Start().
    }
#endif
}