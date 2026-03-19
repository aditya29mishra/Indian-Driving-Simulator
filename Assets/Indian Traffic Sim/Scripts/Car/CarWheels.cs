using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// CarWheels — wheel collider and mesh sync, center of mass
//
// Synchronizes WheelCollider poses with tire mesh transforms each frame.
// Optionally applies custom center of mass offset to Rigidbody.
//
// Talks to: CarMove (provides WheelColliders array)
// ─────────────────────────────────────────────────────────────────────────────

public class CarWheels : MonoBehaviour
{
    /// <summary>Array of WheelCollider components (expects 4: front-left, front-right, rear-left, rear-right).</summary>
    public WheelCollider[] WheelColliders;
    /// <summary>Array of tire mesh transforms to sync with colliders.</summary>
    public Transform[] tireMeshes;
    /// <summary>Whether to use a custom center of mass offset.</summary>
    public bool useCustomCenterOfMass = false;
    /// <summary>Offset for the center of mass when useCustomCenterOfMass is true.</summary>
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