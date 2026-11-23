using System.Security.Cryptography;
using System.Text.Json;
using GmmPanel.Agents;
using GmmPanel.Auth;
using GmmPanel.Commands;
using GmmPanel.Protocol;

namespace GmmPanel.Data;

// Tasks: one action on several servers, now (bulk) or on a schedule (once, every day at a time, or every few minutes).
// Each run goes through CommandService as the person who made it, so their admin flags on each server are checked again
// every time, refusals come back as on a click, and everything is in the audit log. Kept in data/jobs.json.
public static class TaskKinds
{
    // What a task can do, in the in-game menus' words, with the command it sends and the fields it needs
    public sealed record Kind(string Id, string Label, string Command, string[] Fields);

    public static readonly Kind[] All =
    {
        new("mode", "Change mode", "change_mode", new[] { "mode" }),
        new("nextmode", "Set next mode", "set_next_mode", new[] { "mode" }),
        new("map", "Change map", "change_map", new[] { "map" }),
        new("setting", "Turn a setting on or off", "set_setting", new[] { "setting", "on" }),
        new("value", "Set a setting's value", "set_value", new[] { "pick" }),
        new("action", "Run an action", "run_action", new[] { "action" }),
        new("rotation", "Change a rotation setting", "set_rotation", new[] { "rotation" }),
        new("restart", "Restart the match", "restart_match", Array.Empty<string>()),
        new("say", "Message in chat", "console", new[] { "text" }),
        new("console", "Console command", "console", new[] { "line" })
    };

    public static Kind? Find(string? id) => All.FirstOrDefault(k => k.Id == id);

    // The command's arguments from the task's fields ("say" becomes a console say, checked like any console line)
    public static Dictionary<string, string> Args(Kind kind, Dictionary<string, string> fields)
    {
        var args = kind.Fields.Where(fields.ContainsKey).ToDictionary(f => f, f => fields[f]);
        if (kind.Id == "say")
            args = new() { ["line"] = $"say {fields.GetValueOrDefault("text")}" };
        // "sv_gravity|400": a value setting and the value picked
        if (kind.Id == "value" && fields.GetValueOrDefault("pick") is string chosen && chosen.Split('|', 2) is [var name, var choice])
            args = new() { ["setting"] = name, ["value"] = choice };
        // "modes|true": the Rotation page's key and value; "save" also writes it into GameModeManager.json
        if (kind.Id == "rotation" && fields.GetValueOrDefault("rotation") is string pick && pick.Split('|') is [var key, var value])
        {
            args = new() { ["key"] = key, ["value"] = value };
            if (fields.GetValueOrDefault("save") == "true")
                args["save"] = "true";
        }
        return args;
    }

    // The rotation choices a task offers, as "key|value" and its label (the Rotation page's rows)
    public static List<(string Value, string Label)> RotationChoices(RotationInfo r)
    {
        var list = new List<(string, string)>();
        foreach (var (key, label) in new[] { ("maps", "Map rotation"), ("modes", "Mode rotation"), ("rtv", "RTV and nominate"), ("whenEmpty", "Rotate when empty"), ("countSkipped", "Count skipped maps") })
        {
            list.Add(($"{key}|true", $"{label}: on"));
            list.Add(($"{key}|false", $"{label}: off"));
        }
        list.Add(("modeVote|random", "Mode change: random"));
        list.Add(("modeVote|vote", "Mode change: vote"));
        list.AddRange(r.IntervalChoices.Select(n => ($"interval|{n}", $"Change mode every {n} map{(n == 1 ? "" : "s")}")));
        list.AddRange(r.EmptyChoices.Select(n => ($"emptyMinutes|{n}", $"Rotate after {n} min empty")));
        return list;
    }

    // What's wrong with the fields before anything is sent (each server checks the rest)
    public static string? Problem(Kind kind, Dictionary<string, string> fields)
    {
        foreach (string field in kind.Fields)
            if (string.IsNullOrWhiteSpace(fields.GetValueOrDefault(field)))
                return field switch { "text" => "Write the message.", "line" => "Type the command.", _ => $"Choose the {field}." };
        if (kind.Id == "say")
        {
            string text = fields["text"];
            if (text.Length > 160 || text.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0)
                return "A message is up to 160 characters, without quotes or semicolons.";
        }
        if (kind.Id == "setting" && fields.GetValueOrDefault("on") is not ("true" or "false"))
            return "Choose on or off.";
        return null;
    }

    // "Change mode: Casual" for lists and the audit
    public static string Describe(Kind kind, Dictionary<string, string> fields) => kind.Id switch
    {
        "mode" or "nextmode" => $"{kind.Label}: {fields.GetValueOrDefault("modeLabel") ?? fields.GetValueOrDefault("mode")}",
        "map" => $"{kind.Label}: {fields.GetValueOrDefault("map")}",
        "setting" => $"{fields.GetValueOrDefault("settingLabel") ?? fields.GetValueOrDefault("setting")} {(fields.GetValueOrDefault("on") == "true" ? "on" : "off")}",
        "action" => $"{kind.Label}: {fields.GetValueOrDefault("actionLabel") ?? fields.GetValueOrDefault("action")}",
        "say" => $"Chat: \"{fields.GetValueOrDefault("text")}\"",
        "console" => $"Console: {fields.GetValueOrDefault("line")}",
        "value" => fields.GetValueOrDefault("pickLabel") ?? fields.GetValueOrDefault("pick") ?? kind.Label,
        "rotation" => $"{fields.GetValueOrDefault("rotationLabel") ?? fields.GetValueOrDefault("rotation")}{(fields.GetValueOrDefault("save") == "true" ? " (saved in GameModeManager.json)" : "")}",
        _ => kind.Label
    };
}

public sealed class Jobs
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private List<Job> _jobs;

    public Jobs(PanelStore store)
    {
        _path = Path.Combine(store.DataDirectory, "jobs.json");
        _jobs = File.Exists(_path) ? JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(_path), Json) ?? new() : new();
    }

    public sealed class Job
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";                         // TaskKinds id
        public Dictionary<string, string> Fields { get; set; } = new();
        public List<string> Servers { get; set; } = new();             // Empty: every server the maker can use
        public string When { get; set; } = "once";                     // once, daily or every
        public DateTimeOffset? At { get; set; }                        // once
        public string Time { get; set; } = "";                         // daily: HH:mm, the panel's own time
        public List<DayOfWeek> Days { get; set; } = new();             // daily: empty = every day
        public int Every { get; set; }                                 // every: minutes
        public bool OnlyWithPlayers { get; set; }                      // Skip a server nobody's on
        public bool Paused { get; set; }
        public string BySteamId { get; set; } = "";
        public string ByName { get; set; } = "";
        public DateTimeOffset Created { get; set; }
        public DateTimeOffset? Next { get; set; }                      // Null: done (a once that ran) or missed
        public DateTimeOffset? LastRun { get; set; }
        public string LastResult { get; set; } = "";

        public Job Clone() => (Job)MemberwiseClone();
    }

    public List<Job> List()
    {
        lock (_lock)
            return _jobs.OrderBy(j => j.Next == null).ThenBy(j => j.Next).Select(j => j.Clone()).ToList();
    }

    public Job? Find(string id)
    {
        lock (_lock)
            return _jobs.FirstOrDefault(j => j.Id == id)?.Clone();
    }

    public void Add(Job job)
    {
        lock (_lock)
        {
            job.Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
            job.Created = DateTimeOffset.UtcNow;
            job.Next = NextRun(job, DateTimeOffset.UtcNow);
            _jobs.Add(job);
            Save();
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            _jobs.RemoveAll(j => j.Id == id);
            Save();
        }
    }

    public void SetPaused(string id, bool paused)
    {
        lock (_lock)
        {
            if (_jobs.FirstOrDefault(j => j.Id == id) is not { } job)
                return;
            job.Paused = paused;
            if (!paused)
                job.Next = NextRun(job, DateTimeOffset.UtcNow);
            Save();
        }
    }

    // The runner: the jobs due now, and afterwards what happened
    public List<Job> Due(DateTimeOffset now)
    {
        lock (_lock)
            return _jobs.Where(j => !j.Paused && j.Next != null && j.Next <= now).Select(j => j.Clone()).ToList();
    }

    public void Ran(string id, DateTimeOffset now, string result, bool missed)
    {
        lock (_lock)
        {
            if (_jobs.FirstOrDefault(j => j.Id == id) is not { } job)
                return;
            job.LastRun = missed ? job.LastRun : now;
            job.LastResult = result;
            job.Next = job.When == "once" ? null : NextRun(job, now.AddSeconds(1));
            // A once that ran goes after a week
            _jobs.RemoveAll(j => j.When == "once" && j.Next == null && (j.LastRun ?? j.Created) < now.AddDays(-7));
            Save();
        }
    }

    // When it runs next, from "after" on, in the panel's own time zone
    public static DateTimeOffset? NextRun(Job job, DateTimeOffset after)
    {
        switch (job.When)
        {
            case "once":
                return job.At is { } at && at > after.AddMinutes(-1) ? at : null;
            case "every":
                if (job.Every <= 0)
                    return null;
                return job.LastRun is { } last && last.AddMinutes(job.Every) > after ? last.AddMinutes(job.Every) : after.AddMinutes(job.Every);
            case "daily":
                if (!TimeOnly.TryParseExact(job.Time, "HH:mm", out TimeOnly time))
                    return null;
                DateTime local = after.ToLocalTime().DateTime;
                for (int day = 0; day <= 7; day++)
                {
                    DateTime candidate = local.Date.AddDays(day).Add(time.ToTimeSpan());
                    if (candidate > local && (job.Days.Count == 0 || job.Days.Contains(candidate.DayOfWeek)))
                        return new DateTimeOffset(candidate, TimeZoneInfo.Local.GetUtcOffset(candidate));
                }
                return null;
            default:
                return null;
        }
    }

    // "Every day at 04:00", "Mon, Wed at 20:00", "Every 30 min", "13 Nov 21:00"
    public static string Schedule(Job job) => job.When switch
    {
        "every" => job.Every >= 60 && job.Every % 60 == 0 ? $"Every {job.Every / 60} h" : $"Every {job.Every} min",
        "daily" => job.Days.Count is 0 or 7 ? $"Every day at {job.Time}"
            : $"{string.Join(", ", job.Days.OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString()[..3]))} at {job.Time}",
        _ => job.At is { } at ? at.ToLocalTime().ToString("d MMM HH:mm") : "Once"
    };

    private void Save()
    {
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_jobs, Json));
        File.Move(_path + ".tmp", _path, overwrite: true);
    }
}

// Runs one task on its servers, for the Tasks page (Run now) and the schedule alike
public sealed class TaskRunner
{
    private readonly CommandService _commands;
    private readonly AgentRegistry _registry;
    private readonly PanelStore _store;

    public TaskRunner(CommandService commands, AgentRegistry registry, PanelStore store)
    {
        _commands = commands;
        _registry = registry;
        _store = store;
    }

    public sealed record ServerResult(string Server, bool Ok, bool Skipped, string Message);

    public async Task<List<ServerResult>> RunAsync(PanelUser user, TaskKinds.Kind kind, Dictionary<string, string> fields, IEnumerable<string> servers, bool onlyWithPlayers)
    {
        var results = new List<ServerResult>();
        var args = TaskKinds.Args(kind, fields);
        foreach (string server in servers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ServerView? view = _registry.Find(server);
            if (onlyWithPlayers && (view?.Online != true || (view.Status?.Players ?? 0) == 0))
            {
                results.Add(new(server, true, true, "Skipped: nobody on."));
                continue;
            }
            CommandResult result = await _commands.RunAsync(user, server, kind.Command, new Dictionary<string, string>(args));
            results.Add(new(server, result.Ok, false, result.Message));
        }
        return results;
    }

    // A job's servers: the ones it names, or (empty) every server its maker can use
    public List<string> ServersOf(Jobs.Job job, PanelUser user) => job.Servers.Count > 0
        ? job.Servers
        : _store.Current.Servers.Where(s => user.CanUse(s.Name)).Select(s => s.Name).ToList();

    // A last run as a status, as job dashboards show it: Done (every server), "1 of 2 done" (some skipped or failed),
    // Failed (none), Missed (the panel wasn't running); Summary's full text goes on hover
    public static (string Css, string Label) Outcome(string result)
    {
        if (result.StartsWith("Missed"))
            return ("bad", "Missed");
        var m = System.Text.RegularExpressions.Regex.Match(result, @"^(\d+) of (\d+) done");
        if (!m.Success)
            return ("off", result);
        int ok = int.Parse(m.Groups[1].Value), all = int.Parse(m.Groups[2].Value);
        return ok == all ? ("ok", "Done") : ok == 0 ? ("bad", "Failed") : ("warn", $"{ok} of {all} done");
    }

    public static string Summary(List<ServerResult> results)
    {
        int ok = results.Count(r => r.Ok && !r.Skipped), skipped = results.Count(r => r.Skipped);
        var failed = results.Where(r => !r.Ok).ToList();
        string text = $"{ok} of {results.Count} done";
        if (skipped > 0) text += $", {skipped} skipped (nobody on)";
        if (failed.Count > 0) text += ". " + string.Join(" ", failed.Select(f => $"{f.Server}: {f.Message}"));
        return text.Length > 400 ? text[..397] + "..." : text;
    }
}

// The schedule's clock: every 15 seconds, runs what's due as the person who made it. A run missed by more than 10 minutes
// (the panel was off) is skipped, not run late, and says so.
public sealed class JobClock : BackgroundService
{
    private readonly Jobs _jobs;
    private readonly TaskRunner _runner;
    private readonly PanelStore _store;
    private readonly ILogger<JobClock> _logger;

    public JobClock(Jobs jobs, TaskRunner runner, PanelStore store, ILogger<JobClock> logger)
    {
        _jobs = jobs;
        _runner = runner;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stop))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (Jobs.Job job in _jobs.Due(now))
            {
                try
                {
                    if (job.Next < now.AddMinutes(-10))
                    {
                        _jobs.Ran(job.Id, now, $"Missed at {job.Next?.ToLocalTime():d MMM HH:mm}: the panel wasn't running.", missed: true);
                        continue;
                    }
                    if (TaskKinds.Find(job.Kind) is not { } kind)
                    {
                        _jobs.Ran(job.Id, now, "This kind of task isn't offered any more.", missed: true);
                        continue;
                    }
                    PanelUser user = PanelUser.ForTask(job.BySteamId, job.ByName, _store);
                    var results = await _runner.RunAsync(user, kind, job.Fields, _runner.ServersOf(job, user), job.OnlyWithPlayers);
                    _jobs.Ran(job.Id, now, TaskRunner.Summary(results), missed: false);
                }
                catch (Exception error)
                {
                    _logger.LogWarning(error, "Scheduled task {Id} failed", job.Id);
                    _jobs.Ran(job.Id, now, "It failed: see the panel's log.", missed: false);
                }
            }
        }
    }
}
