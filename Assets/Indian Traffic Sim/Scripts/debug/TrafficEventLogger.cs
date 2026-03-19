using System.Collections.Generic;
using System.IO;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TrafficEventLogger — singleton text log, in-memory entry store
//
// Maintains a list of log entries in memory and writes to a text file.
// Provides methods to retrieve recent entries for specific vehicles.
//
// Talks to: TrafficVehicle (event logging)
// ─────────────────────────────────────────────────────────────────────────────

public class TrafficEventLogger : MonoBehaviour
{
    /// <summary>Gets the singleton instance of the logger.</summary>
    public static TrafficEventLogger Instance { get; private set; }

    private struct LogEntry
    {
        public string timeStamp;
        public string vehicleName;
        public string eventType;
        public string detail;
        public string fullLine;
        public LogEntry(string ts, string v, string e, string d, string line)
        {
            timeStamp = ts;
            vehicleName = v;
            eventType = e;
            detail = d;
            fullLine = line;
        }
    }

    private List<LogEntry> entries = new List<LogEntry>();
    private StreamWriter writer;
    private string logPath;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        logPath = Application.dataPath + "/TrafficLog.txt";
        try
        {
            writer = new StreamWriter(logPath, append: true);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("TrafficEventLogger: could not open log file: " + e.Message);
        }
    }

    /// <summary>Logs an event with the specified details.</summary>
    /// <param name="vehicleName">Name of the vehicle.</param>
    /// <param name="eventType">Type of the event.</param>
    /// <param name="detail">Additional details.</param>
    public static void Log(string vehicleName, string eventType, string detail)
    {
        if (Instance == null) return;

        string ts = System.DateTime.Now.ToString("HH:mm:ss.fff");
        string line = "[" + ts + "] [" + (vehicleName ?? "") + "] [" + (eventType ?? "") + "] " + (detail ?? "");
        Instance.entries.Add(new LogEntry(ts, vehicleName ?? "", eventType ?? "", detail ?? "", line));

        if (Instance.writer != null)
        {
            try
            {
                Instance.writer.WriteLine(line);
                Instance.writer.Flush();
            }
            catch (System.Exception) { }
        }
    }

    /// <summary>Flushes the log writer to ensure all entries are written to file.</summary>
    public void FlushLog()
    {
        if (writer != null)
        {
            try { writer.Flush(); } catch (System.Exception) { }
        }
    }

    /// <summary>Gets the most recent log entries for a specific vehicle.</summary>
    /// <param name="vehicleName">Name of the vehicle.</param>
    /// <param name="count">Number of entries to retrieve.</param>
    /// <returns>List of log entry strings.</returns>
    public List<string> GetRecentEntriesForVehicle(string vehicleName, int count)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(vehicleName) || count <= 0) return list;
        for (int i = entries.Count - 1; i >= 0 && list.Count < count; i--)
        {
            if (entries[i].vehicleName == vehicleName)
                list.Add(entries[i].fullLine);
        }
        return list;
    }

    void OnDestroy()
    {
        FlushLog();
        if (writer != null)
        {
            try { writer.Close(); } catch (System.Exception) { }
            writer = null;
        }
        if (Instance == this)
            Instance = null;
    }
}
