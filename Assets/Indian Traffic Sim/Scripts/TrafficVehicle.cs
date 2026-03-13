using UnityEngine;

public class TrafficVehicle : MonoBehaviour
{
    public TrafficPath path;

    public float speed = 10f;
    public float waypointReachDistance = 2f;

    public float lateralOffset = 0f;

    private int currentWaypoint = 0;

    void Start()
    {
        // random offset inside lane
        lateralOffset = Random.Range(-1.2f, 1.2f);
    }

    void Update()
    {
        if (path == null || path.waypoints.Count == 0)
            return;

        Transform waypoint = path.waypoints[currentWaypoint];

        Vector3 direction = (waypoint.position - transform.position).normalized;

        Vector3 right = Vector3.Cross(Vector3.up, direction);
        Vector3 offset = right * lateralOffset;

        Vector3 target = waypoint.position + offset;

        transform.position += direction * speed * Time.deltaTime;

        transform.forward = Vector3.Lerp(transform.forward, direction, Time.deltaTime * 5f);

        float distance = Vector3.Distance(transform.position, target);

        if (distance < waypointReachDistance)
        {
            currentWaypoint++;

            if (currentWaypoint >= path.waypoints.Count)
                currentWaypoint = 0;
        }
    }
}