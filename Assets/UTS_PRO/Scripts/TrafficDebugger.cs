using UnityEngine;

public static class TrafficDebugger
{
    public static bool ENABLED = true;

    public static bool SPAWN = true;
    public static bool LANE = true;
    public static bool WAYPOINT = false;
    public static bool INTERSECTION = true;
    public static bool CAR_AVOID = true;

    public static void Spawn(string msg)
    {
        if (ENABLED && SPAWN)
            Debug.Log("<color=green>[SPAWN]</color> " + msg);
    }

    public static void Lane(string msg)
    {
        if (ENABLED && LANE)
            Debug.Log("<color=cyan>[LANE]</color> " + msg);
    }

    public static void Waypoint(string msg)
    {
        if (ENABLED && WAYPOINT)
            Debug.Log("<color=white>[WAYPOINT]</color> " + msg);
    }

    public static void Intersection(string msg)
    {
        if (ENABLED && INTERSECTION)
            Debug.Log("<color=yellow>[INTERSECTION]</color> " + msg);
    }

    public static void Avoid(string msg)
    {
        if (ENABLED && CAR_AVOID)
            Debug.Log("<color=red>[CAR AVOID]</color> " + msg);
    }
}
