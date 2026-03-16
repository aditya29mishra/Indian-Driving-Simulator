using UnityEngine;

public class TrafficStressTester : MonoBehaviour
{
    public VehicleSpawner[] spawners = new VehicleSpawner[0];

    private string activeTest = "";
    private float activeTestClearTime;

    void Update()
    {
        if (spawners == null) return;

        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            SpawnAcrossSpawners(2);
            activeTest = "Spawned 2";
            activeTestClearTime = Time.unscaledTime + 2f;
        }
        else if (Input.GetKeyDown(KeyCode.Alpha2))
        {
            SpawnAcrossSpawners(10);
            activeTest = "Spawned 10";
            activeTestClearTime = Time.unscaledTime + 2f;
        }
        else if (Input.GetKeyDown(KeyCode.Alpha3))
        {
            SpawnAcrossSpawners(25);
            activeTest = "Spawned 25";
            activeTestClearTime = Time.unscaledTime + 2f;
        }
        else if (Input.GetKeyDown(KeyCode.Alpha4))
        {
            SpawnAcrossSpawners(50);
            activeTest = "Spawned 50";
            activeTestClearTime = Time.unscaledTime + 2f;
        }
        else if (Input.GetKeyDown(KeyCode.S))
        {
            foreach (var sig in Object.FindObjectsOfType<TrafficSignal>())
                sig.ForceNextPhase();
            activeTest = "Signals next phase";
            activeTestClearTime = Time.unscaledTime + 2f;
        }
        else if (Input.GetKeyDown(KeyCode.L))
        {
            foreach (var v in Object.FindObjectsOfType<TrafficVehicle>())
                v.CheckLaneChange();
            activeTest = "Lane change forced";
            activeTestClearTime = Time.unscaledTime + 2f;
        }
        else if (Input.GetKeyDown(KeyCode.R))
        {
            foreach (var v in Object.FindObjectsOfType<TrafficVehicle>())
                Destroy(v.gameObject);
            activeTest = "All vehicles destroyed";
            activeTestClearTime = Time.unscaledTime + 2f;
        }

        if (Time.unscaledTime > activeTestClearTime)
            activeTest = "";
    }

    void SpawnAcrossSpawners(int count)
    {
        if (spawners == null || spawners.Length == 0) return;
        int perSpawner = Mathf.Max(1, count / spawners.Length);
        int remainder = count - (perSpawner * spawners.Length);
        for (int i = 0; i < spawners.Length; i++)
        {
            if (spawners[i] == null) continue;
            int n = perSpawner + (i < remainder ? 1 : 0);
            spawners[i].ForceSpawn(n);
        }
    }

    void OnGUI()
    {
        int vehicleCount = Object.FindObjectsOfType<TrafficVehicle>().Length;
        string msg = "Vehicles: " + vehicleCount;
        if (!string.IsNullOrEmpty(activeTest))
            msg += "  |  " + activeTest;
        GUI.Label(new Rect(Screen.width - 320f, 10f, 310f, 24f), msg);
    }
}
