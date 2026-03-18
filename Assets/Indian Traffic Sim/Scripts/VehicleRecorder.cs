using UnityEngine;

[RequireComponent(typeof(TrafficVehicle))]
public class VehicleRecorder : MonoBehaviour
{
    private TrafficVehicle v;
    private TrafficDataRecorder manager;

    private float timer = 0f;

    void Awake()
    {
        v = GetComponent<TrafficVehicle>();
        manager = FindObjectOfType<TrafficDataRecorder>();

        if (manager == null)
            Debug.LogError("[VehicleRecorder] No TrafficDataRecorder found!");
    }

    void FixedUpdate()
    {
        if (manager == null || !manager.IsRecording) return;

        timer += Time.fixedDeltaTime;

        if (timer < manager.recordInterval) return;
        timer = 0f;

        manager.IncrementFrameCount();
        RecordFrame();
    }

    void RecordFrame()
{
    if (v == null) return;

    float speed = v.CurrentSpeed;

    float dist = v.DistanceTravelled;
    float len = v.CurrentPath ? v.CurrentPath.TotalLength : 1f;
    float pct = len > 0.01f ? dist / len : 0f;

    Vector3 p = v.transform.position;

    manager.LogFrame(
    v.GetVehicleId(),
    p.x, p.y, p.z,
    speed,
    v.DesiredSpeed,
    v.MaxSpeed,
    v.currentLane ? v.currentLane.name : "none",
    v.GetPathId(),
    v.GetIntersectionId(),
    dist,
    len,
    pct,
    
    // 🔥 ADD STATES HERE
    v.CurrentMotionState.ToString(),
    v.CurrentSignalState.ToString(),
    v.CurrentManeuverState.ToString(),
    v.IsBlocked,
    v.IsStuck,
    v.IsInQueue,

    v.GetRouteTrace()
);
}
}