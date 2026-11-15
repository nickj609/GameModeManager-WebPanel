using System.Text.Json;

namespace GmmPanel.Data;

// audit.jsonl in the data folder: one line per sign-in, refusal and command. Never holds tokens or secrets.
public sealed record AuditEntry(DateTimeOffset At, string Who, string What, string? Server, bool Ok, string Detail);

public sealed class AuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<AuditLog> _logger;

    public AuditLog(PanelStore store, ILogger<AuditLog> logger)
    {
        _path = Path.Combine(store.DataDirectory, "audit.jsonl");
        _logger = logger;
    }

    public void Write(string who, string what, string? server, bool ok, string detail)
    {
        var entry = new AuditEntry(DateTimeOffset.UtcNow, who, what, server, ok, detail);
        _logger.LogInformation("Audit: {Who} {What} on {Server}: {Ok} {Detail}", who, what, server ?? "-", ok ? "ok" : "refused", detail);
        lock (_lock)
        {
            File.AppendAllText(_path, JsonSerializer.Serialize(entry, Json) + "\n");
        }
    }

    // Drops entries older than the cut (DataCleanup, from Retention.AuditDays). Returns how many went.
    public int Prune(DateTimeOffset cut)
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
                return 0;
            var kept = new List<string>();
            int dropped = 0;
            foreach (string line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                AuditEntry? entry = null;
                try { entry = JsonSerializer.Deserialize<AuditEntry>(line, Json); } catch (JsonException) { }
                if (entry != null && entry.At < cut)
                    dropped++;
                else
                    kept.Add(line);
            }
            if (dropped > 0)
            {
                File.WriteAllLines(_path + ".tmp", kept);
                File.Move(_path + ".tmp", _path, overwrite: true);
            }
            return dropped;
        }
    }

    // Everything since a time, oldest first (the audit page's export)
    public List<AuditEntry> Since(DateTimeOffset since)
    {
        var all = new List<AuditEntry>();
        lock (_lock)
        {
            if (!File.Exists(_path))
                return all;
            foreach (string line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    if (JsonSerializer.Deserialize<AuditEntry>(line, Json) is { } entry && entry.At >= since)
                        all.Add(entry);
                }
                catch (JsonException) { }
            }
        }
        return all;
    }

    public List<AuditEntry> Latest(int count)
    {
        var latest = new Queue<AuditEntry>(count);
        lock (_lock)
        {
            if (!File.Exists(_path))
                return new List<AuditEntry>();
            foreach (string line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                AuditEntry? entry;
                try { entry = JsonSerializer.Deserialize<AuditEntry>(line, Json); }
                catch (JsonException) { continue; }
                if (entry == null)
                    continue;
                if (latest.Count == count)
                    latest.Dequeue();
                latest.Enqueue(entry);
            }
        }
        return latest.Reverse().ToList();
    }
}
