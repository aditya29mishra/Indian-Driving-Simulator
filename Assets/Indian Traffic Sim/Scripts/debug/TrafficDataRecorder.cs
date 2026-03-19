using System.IO;
using System.Text;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficDataRecorder — CSV frame and event logging, 24-column schema
//
// Records vehicle telemetry and events to CSV format. Manages recording
// sessions and file export. Receives data from VehicleRecorder components.
//
// Talks to: VehicleRecorder (data source), TrafficVehicle (events)
// ─────────────────────────────────────────────────────────────────────────────

public class TrafficDataRecorder : MonoBehaviour
{
    /// <summary>Gets whether recording is currently active.</summary>
    public bool IsRecording => isRecording;

    [Header("Recording")]
    public float recordInterval = 0.1f;
    public int maxFrames = 50000;
    public bool recordOnStart = false;

    private bool isRecording = false;
    private int frameCount = 0;
    private float sessionTime = 0f;

    private StringBuilder csv = new StringBuilder(100000);

    void Start()
    {
        if (recordOnStart) StartRec();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.R))
        {
            if (isRecording) StopRec();
            else StartRec();
        }

        if (Input.GetKeyDown(KeyCode.E) && csv.Length > 0)
            Export();

        if (!isRecording) return;

        sessionTime += Time.deltaTime;
    }

    // Called by VehicleRecorder each time a frame row is actually written.
    // The old Update-based counter ran on a fixed timer regardless of vehicle
    // count — 10 vehicles each logging at 0.1s = 10x more frames than counted.
    /// <summary>Increments the frame count and stops recording if max frames reached.</summary>
    public void IncrementFrameCount()
    {
        frameCount++;
        if (frameCount >= maxFrames)
            StopRec();
    }

    // ─────────────────────────────────────────────
    // EVENT LOGGING (GLOBAL ENTRY POINT)
    // ─────────────────────────────────────────────
    /// <summary>Logs an event to the CSV data.</summary>
    /// <param name="vehicle">Vehicle identifier.</param>
    /// <param name="evt">Event type.</param>
    /// <param name="pathId">Path identifier.</param>
    /// <param name="intersectionId">Intersection identifier.</param>
    /// <param name="decision">Decision made.</param>
    /// <param name="probability">Probability of decision.</param>
    /// <param name="routeTrace">Route trace string.</param>
    public void LogEvent(
    string vehicle,
    string evt,
    string pathId = "",
    string intersectionId = "",
    string decision = "",
    float probability = -1f,
    string routeTrace = ""
)
    {
        if (!isRecording) return;

        StringBuilder sb = new StringBuilder(256);

        sb.Append(vehicle).Append(',');
        sb.Append(sessionTime.ToString("F2")).Append(',');
        sb.Append(evt).Append(',');

        // skip px,py,pz,speed,desiredSpeed,maxSpeed,lane  (7 columns)
        sb.Append(",,,,,,,");

        sb.Append(pathId).Append(',');
        sb.Append(intersectionId).Append(',');

        // dist fields
        sb.Append(",,,");

        // decision + probability
        sb.Append(decision).Append(',');
        sb.Append(probability >= 0 ? probability.ToString("F2") : "").Append(',');

        // 🔥 ADD THIS
        sb.Append(evt == "STATE_MOTION" ? decision : "").Append(',');
        sb.Append(evt == "STATE_SIGNAL" ? decision : "").Append(',');
        sb.Append(evt == "STATE_MANEUVER" ? decision : "").Append(',');

        sb.Append(",,,");

        // route trace
        sb.Append(routeTrace);

        csv.AppendLine(sb.ToString());
    }
    // ─────────────────────────────────────────────
    // FRAME LOGGING
    // ─────────────────────────────────────────────
    /// <summary>Logs a frame of vehicle data to the CSV.</summary>
    /// <param name="vehicle">Vehicle identifier.</param>
    /// <param name="px">Position X.</param>
    /// <param name="py">Position Y.</param>
    /// <param name="pz">Position Z.</param>
    /// <param name="speed">Current speed.</param>
    /// <param name="desiredSpeed">Desired speed.</param>
    /// <param name="maxSpeed">Maximum speed.</param>
    /// <param name="lane">Lane name.</param>
    /// <param name="pathId">Path identifier.</param>
    /// <param name="intersectionId">Intersection identifier.</param>
    /// <param name="dist">Distance travelled.</param>
    /// <param name="pathLen">Path length.</param>
    /// <param name="pathPct">Path percentage.</param>
    /// <param name="motionState">Motion state.</param>
    /// <param name="signalState">Signal state.</param>
    /// <param name="maneuverState">Maneuver state.</param>
    /// <param name="isBlocked">Is blocked flag.</param>
    /// <param name="isStuck">Is stuck flag.</param>
    /// <param name="isInQueue">Is in queue flag.</param>
    /// <param name="routeTrace">Route trace string.</param>
    public void LogFrame(
    string vehicle,
    float px, float py, float pz,
    float speed,
    float desiredSpeed,
    float maxSpeed,
    string lane,
    string pathId,
    string intersectionId,
    float dist,
    float pathLen,
    float pathPct,

    // 🔥 ADD THESE
    string motionState,
    string signalState,
    string maneuverState,
    bool isBlocked,
    bool isStuck,
    bool isInQueue,

    string routeTrace
)
    {
        if (!isRecording) return;

        StringBuilder sb = new StringBuilder(256);

        sb.Append(vehicle).Append(',');
        sb.Append(sessionTime.ToString("F2")).Append(',');
        sb.Append("FRAME").Append(',');

        sb.Append(px.ToString("F2")).Append(',');
        sb.Append(py.ToString("F2")).Append(',');
        sb.Append(pz.ToString("F2")).Append(',');

        sb.Append(speed.ToString("F2")).Append(',');
        sb.Append(desiredSpeed.ToString("F2")).Append(',');
        sb.Append(maxSpeed.ToString("F2")).Append(',');

        sb.Append(lane).Append(',');
        sb.Append(pathId).Append(',');
        sb.Append(intersectionId).Append(',');

        sb.Append(dist.ToString("F2")).Append(',');
        sb.Append(pathLen.ToString("F2")).Append(',');
        sb.Append(pathPct.ToString("F3")).Append(',');

        sb.Append(",,"); // decision, probability

        sb.Append(motionState).Append(',');
        sb.Append(signalState).Append(',');
        sb.Append(maneuverState).Append(',');
        sb.Append(isBlocked ? "1" : "0").Append(',');
        sb.Append(isStuck ? "1" : "0").Append(',');
        sb.Append(isInQueue ? "1" : "0").Append(',');

        sb.Append(routeTrace);
        csv.AppendLine(sb.ToString());
    }

    // ─────────────────────────────────────────────
    void StartRec()
    {
        csv.Clear();

        frameCount = 0;
        sessionTime = 0f;

        isRecording = true;

        BuildHeader();

        Debug.Log("[Recorder] Started");
    }

    void StopRec()
    {
        isRecording = false;
        Debug.Log($"[Recorder] Stopped ({frameCount} frames)");
        Export();
    }

    void BuildHeader()
    {
        csv.AppendLine(
            "vehicle,time,event," +
            "px,py,pz," +
            "speed,desiredSpeed,maxSpeed," +
            "lane,pathId,intersectionId," +
            "distTravelled,pathLength,pathPct," +
            "decision,probability," +
            "motionState,signalState,maneuverState,isBlocked,isStuck,isInQueue," +
            "routeTrace"
        );
    }

    void Export()
    {
        string path =
            Application.dataPath +
            "/TrafficRecording_" +
            System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") +
            ".csv";

        File.WriteAllText(path, csv.ToString());

        Debug.Log("[Recorder] Saved: " + path);
    }
}