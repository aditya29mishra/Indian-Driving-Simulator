using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// Controls: R = start/stop   E = export now
// Saves to Assets/TrafficRecording_[timestamp].csv

public class TrafficDataRecorder : MonoBehaviour
{
    [Header("Recording")]
    public float recordInterval = 0.1f;
    public int maxFrames = 50000;
    public bool recordOnStart = false;

    [Header("Columns to record")]
    public bool col_position = true;
    public bool col_velocity = true;
    public bool col_speed_detail = true;
    public bool col_idm = true;
    public bool col_signal = true;
    public bool col_lane = true;
    public bool col_profile = true;
    public bool col_lane_change = true;
    public bool col_stuck = true;
    public bool col_path = true;

    [Header("Advanced Diagnostics")]
    public bool col_controls = true;
    public bool col_density = true;
    public bool col_safety = true;
    public bool col_leader_geometry = true;

    private bool isRecording = false;
    private float timer = 0f;
    private int frameCount = 0;
    private float sessionTime = 0f;

    private StringBuilder csv = new StringBuilder();

    private Dictionary<TrafficVehicle, int> zeroSpeedFrames = new();
    private Dictionary<TrafficVehicle, float> stuckTimer = new();
    private Dictionary<TrafficVehicle, float> prevSpeed = new();

    void Start()
    {
        if (recordOnStart) StartRec();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.R)) { if (isRecording) StopRec(); else StartRec(); }
        if (Input.GetKeyDown(KeyCode.E) && csv.Length > 0) Export();
        if (!isRecording) return;

        sessionTime += Time.deltaTime;
        timer += Time.deltaTime;

        if (timer < recordInterval) return;

        timer = 0f;
        frameCount++;

        if (frameCount > maxFrames)
        {
            StopRec();
            return;
        }

        RecordFrame();
    }

    void RecordFrame()
    {
        var vehicles = Object.FindObjectsOfType<TrafficVehicle>();

        foreach (var v in vehicles)
        {
            if (v == null) continue;

            float spd = v.CurrentSpeed;

            if (!zeroSpeedFrames.ContainsKey(v)) zeroSpeedFrames[v] = 0;
            if (!stuckTimer.ContainsKey(v)) stuckTimer[v] = 0f;
            if (!prevSpeed.ContainsKey(v)) prevSpeed[v] = 0f;

            if (spd < 0.1f) zeroSpeedFrames[v]++;
            else zeroSpeedFrames[v] = 0;

            if (spd < 0.1f && v.leaderVehicle != null)
                stuckTimer[v] += recordInterval;
            else
                stuckTimer[v] = 0f;

            float accel = (spd - prevSpeed[v]) / recordInterval;
            prevSpeed[v] = spd;

            var sb = new StringBuilder();

            sb.Append(v.name).Append(',');
            sb.Append(sessionTime.ToString("F2")).Append(',');
            sb.Append("FRAME").Append(',');

            if (col_position)
            {
                var p = v.transform.position;
                sb.Append(p.x.ToString("F1")).Append(',');
                sb.Append(p.y.ToString("F1")).Append(',');
                sb.Append(p.z.ToString("F1")).Append(',');
            }

            if (col_velocity)
            {
                Vector3 vel = v.GetVelocity();
                sb.Append(vel.x.ToString("F2")).Append(',');
                sb.Append(vel.z.ToString("F2")).Append(',');

                float heading =
                    Vector3.SignedAngle(Vector3.forward, v.transform.forward, Vector3.up);

                sb.Append(heading.ToString("F1")).Append(',');
                sb.Append(accel.ToString("F2")).Append(',');
            }

            if (col_speed_detail)
            {
                sb.Append(spd.ToString("F2")).Append(',');
                sb.Append(v.DesiredSpeed.ToString("F2")).Append(',');
                sb.Append(v.MaxSpeed.ToString("F2")).Append(',');
            }

            if (col_idm)
            {
                float leaderDist = -1f;
                float leaderSpd = -1f;
                float closingSpd = 0f;
                float gapNeeded = -1f;
                string leaderName = "none";

                if (v.leaderVehicle != null)
                {
                    leaderDist = Vector3.Distance(v.transform.position,
                        v.leaderVehicle.transform.position);

                    leaderSpd = v.leaderVehicle.CurrentSpeed;
                    closingSpd = spd - leaderSpd;
                    gapNeeded = v.MinimumGap + spd * v.TimeHeadway;
                    leaderName = v.leaderVehicle.name;
                }

                sb.Append(leaderName).Append(',');
                sb.Append(leaderDist.ToString("F1")).Append(',');
                sb.Append(leaderSpd.ToString("F2")).Append(',');
                sb.Append(closingSpd.ToString("F2")).Append(',');
                sb.Append(gapNeeded.ToString("F1")).Append(',');
            }

            if (col_leader_geometry)
            {
                float relX = 0f;
                float relZ = 0f;

                if (v.leaderVehicle != null)
                {
                    Vector3 rel =
                        v.transform.InverseTransformPoint(
                            v.leaderVehicle.transform.position);

                    relX = rel.x;
                    relZ = rel.z;
                }

                sb.Append(relX.ToString("F2")).Append(',');
                sb.Append(relZ.ToString("F2")).Append(',');
            }

            if (col_signal)
            {
                string sigState = "none";
                bool mustStop = false;
                float distStop = -1f;
                string stopId = "none";

                if (v.currentSignal != null && v.currentLane != null)
                {
                    var state = v.currentSignal.GetStateForLane(v.currentLane);
                    sigState = state.ToString();

                    if (v.CurrentPath != null &&
                        v.currentLane.path != null &&
                        v.currentLane.path.waypoints.Count > 0)
                    {
                        Vector3 stopLine =
                            v.currentLane.path.waypoints[^1].position;

                        distStop = Vector3.Distance(v.transform.position, stopLine);

                        float dynDist = Mathf.Max(
                            v.StopLineDistance,
                            (spd * spd) / (2f * Mathf.Max(4f, 1f)) + 8f);

                        mustStop =
                            state != TrafficSignal.SignalState.Green &&
                            distStop <= dynDist;

                        stopId = v.currentLane.name + "_stop";
                    }
                }

                sb.Append(sigState).Append(',');
                sb.Append(mustStop ? "1" : "0").Append(',');
                sb.Append(distStop.ToString("F1")).Append(',');
                sb.Append(stopId).Append(',');
            }

            if (col_lane)
            {
                string lane = v.currentLane ? v.currentLane.name : "none";
                string left = v.leftLane ? v.leftLane.name : "none";
                string right = v.rightLane ? v.rightLane.name : "none";

                bool onTurn =
                    v.CurrentPath != null &&
                    v.currentLane != null &&
                    v.CurrentPath != v.currentLane.path;

                sb.Append(lane).Append(',');
                sb.Append(left).Append(',');
                sb.Append(right).Append(',');
                sb.Append(onTurn ? "1" : "0").Append(',');
            }

            if (col_controls)
            {
                var cm = v.GetComponent<CarMove>();

                float throttle = cm ? cm.AccelInput : 0f;
                float brake = cm ? cm.BrakeInput : 0f;
                float steer = cm ? cm.CurrentSteerAngle : 0f;

                sb.Append(throttle.ToString("F2")).Append(',');
                sb.Append(brake.ToString("F2")).Append(',');
                sb.Append(steer.ToString("F2")).Append(',');
            }

            if (col_density)
            {
                int d10 =
                    Physics.OverlapSphere(v.transform.position, 10f).Length - 1;

                int d20 =
                    Physics.OverlapSphere(v.transform.position, 20f).Length - 1;

                sb.Append(d10).Append(',');
                sb.Append(d20).Append(',');
            }

            if (col_safety)
            {
                float headway = -1f;
                float ttc = -1f;

                if (v.leaderVehicle != null)
                {
                    float dist =
                        Vector3.Distance(v.transform.position,
                            v.leaderVehicle.transform.position);

                    float closing = spd - v.leaderVehicle.CurrentSpeed;

                    if (spd > 0.1f)
                        headway = dist / spd;

                    if (closing > 0.1f)
                        ttc = dist / closing;
                }

                sb.Append(headway.ToString("F2")).Append(',');
                sb.Append(ttc.ToString("F2")).Append(',');
            }

            if (col_profile)
            {
                sb.Append(v.driverProfile.ToString()).Append(',');
                sb.Append(v.TimeHeadway.ToString("F2")).Append(',');
                sb.Append(v.MinimumGap.ToString("F1")).Append(',');
            }

            if (col_lane_change)
            {
                sb.Append(v.IsChangingLane ? "1" : "0").Append(',');
                sb.Append(v.LaneChangeProgress.ToString("F2")).Append(',');
            }

            if (col_stuck)
            {
                sb.Append(stuckTimer[v].ToString("F1")).Append(',');
                sb.Append(zeroSpeedFrames[v]).Append(',');
            }

            if (col_path)
            {
                float pathLen =
                    v.CurrentPath != null ? v.CurrentPath.TotalLength : -1f;

                sb.Append(v.DistanceTravelled.ToString("F1")).Append(',');
                sb.Append(pathLen.ToString("F1")).Append(',');

                float pct = pathLen > 0
                    ? v.DistanceTravelled / pathLen
                    : -1f;

                sb.Append(pct.ToString("F2")).Append(',');
            }

            csv.AppendLine(sb.ToString().TrimEnd(','));
        }
    }

    void BuildHeader()
    {
        var h = new StringBuilder();

        h.Append("vehicle,time,event");

        if (col_position) h.Append(",px,py,pz");
        if (col_velocity) h.Append(",vx,vz,heading_deg,accel");
        if (col_speed_detail) h.Append(",speed,desiredSpeed,maxSpeed");
        if (col_idm) h.Append(",leader,leaderDist,leaderSpeed,closingSpeed,gapNeeded");
        if (col_leader_geometry) h.Append(",leaderRelX,leaderRelZ");
        if (col_signal) h.Append(",signal,mustStop,distToStop,stopId");
        if (col_lane) h.Append(",lane,leftLane,rightLane,onTurnPath");
        if (col_controls) h.Append(",throttle,brake,steer");
        if (col_density) h.Append(",density10m,density20m");
        if (col_safety) h.Append(",timeHeadway,timeToCollision");
        if (col_profile) h.Append(",profile,headway,minGap");
        if (col_lane_change) h.Append(",isChangingLane,lcProgress");
        if (col_stuck) h.Append(",stuckTimer,zeroSpeedFrames");
        if (col_path) h.Append(",distTravelled,pathLength,pathPct");

        csv.AppendLine(h.ToString());
    }

    void StartRec()
    {
        csv.Clear();
        zeroSpeedFrames.Clear();
        stuckTimer.Clear();
        prevSpeed.Clear();

        frameCount = 0;
        sessionTime = 0f;
        timer = 0f;

        isRecording = true;

        BuildHeader();

        Debug.Log("[Recorder] Started. R=stop  E=export");
    }

    void StopRec()
    {
        isRecording = false;

        Debug.Log($"[Recorder] Stopped. {frameCount} frames.");

        Export();
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

    void OnGUI()
    {
        string status =
            isRecording
                ? $"<color=red>● REC</color> {sessionTime:F1}s {frameCount} frames"
                : csv.Length > 0
                    ? "<color=yellow>■ READY</color> E=export"
                    : "<color=grey>○ IDLE</color> R=record";

        var style = new GUIStyle(GUI.skin.label);
        style.richText = true;
        style.fontSize = 13;
        style.normal.textColor = Color.white;

        GUI.Label(new Rect(10, Screen.height - 50, 400, 24), status, style);

        if (isRecording)
        {
            GUI.Label(
                new Rect(10, Screen.height - 30, 400, 24),
                $"Tracking {Object.FindObjectsOfType<TrafficVehicle>().Length} vehicles",
                style);
        }
    }
}