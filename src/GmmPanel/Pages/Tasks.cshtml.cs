using GmmPanel.Agents;
using GmmPanel.Auth;
using GmmPanel.Data;
using GmmPanel.Protocol;
using Microsoft.AspNetCore.Mvc;

namespace GmmPanel.Pages;

// Tasks: one action on several servers at once (Run now), or on a schedule (once, every day at a time, every few minutes).
// For staff who can manage or administer at least one server. Each server checks the person's flags on every run.
public class TasksModel : PanelPage
{
    private readonly PanelStore _store;
    private readonly AgentRegistry _registry;
    private readonly Jobs _jobs;
    private readonly TaskRunner _runner;
    private readonly AuditLog _audit;

    public TasksModel(PanelStore store, AgentRegistry registry, Jobs jobs, TaskRunner runner, AuditLog audit)
    {
        _store = store;
        _registry = registry;
        _jobs = jobs;
        _runner = runner;
        _audit = audit;
    }

    public static readonly int[] EveryChoices = { 5, 10, 15, 30, 60, 120, 240, 360, 720 };

    // The servers this person can run tasks on (manage or administer there), and the choices they offer together
    public List<ServerView> Servers { get; } = new();
    public List<NamedItem> Modes { get; private set; } = new();
    public List<NamedItem> Maps { get; private set; } = new();
    public List<NamedItem> Settings { get; private set; } = new();
    public List<NamedItem> Actions { get; private set; } = new();
    public List<(string Value, string Label)> Rotation { get; private set; } = new();
    public List<(string Value, string Label)> Values { get; private set; } = new();
    public List<Jobs.Job> List { get; private set; } = new();
    public List<TaskRunner.ServerResult>? Results { get; private set; }
    public string? Problem { get; private set; }
    public string Kind { get; private set; } = "mode";

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
            return Redirect("/denied");
        return Page();
    }

    // The fields for the chosen kind of task
    public async Task<IActionResult> OnGetFieldsAsync(string? kind)
    {
        if (!await LoadAsync())
            return Forbid();
        Kind = TaskKinds.Find(kind)?.Id ?? "mode";
        return Partial("_TaskFields", this);
    }

    public async Task<IActionResult> OnPostRunAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        if (Read(out var kind, out var fields, out var servers) is string problem)
        {
            Problem = problem;
            return Partial("_TaskResults", this);
        }
        Results = await _runner.RunAsync(Admin, kind!, fields, servers, Request.Form["onlyPlayers"] == "true");
        return Partial("_TaskResults", this);
    }

    public async Task<IActionResult> OnPostScheduleAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        if (Read(out var kind, out var fields, out var servers) is string problem)
        {
            Problem = problem;
            return Partial("_TaskResults", this);
        }
        var job = new Jobs.Job
        {
            Kind = kind!.Id, Fields = fields, Servers = servers, When = Request.Form["when"].ToString(),
            OnlyWithPlayers = Request.Form["onlyPlayers"] == "true", BySteamId = Admin.SteamId, ByName = Admin.Name
        };
        switch (job.When)
        {
            case "once":
                if (!DateTime.TryParse(Request.Form["at"], out DateTime at) || at <= DateTime.Now)
                    Problem = "Pick a date and time in the future.";
                else
                    job.At = new DateTimeOffset(at, TimeZoneInfo.Local.GetUtcOffset(at));
                break;
            case "daily":
                job.Time = Request.Form["time"].ToString();
                job.Days = Request.Form["days"].Select(d => int.TryParse(d, out int n) && n is >= 0 and <= 6 ? (DayOfWeek?)n : null).OfType<DayOfWeek>().Distinct().ToList();
                if (!TimeOnly.TryParseExact(job.Time, "HH:mm", out _))
                    Problem = "Pick a time of day.";
                break;
            case "every":
                job.Every = int.TryParse(Request.Form["every"], out int every) && EveryChoices.Contains(every) ? every : 0;
                if (job.Every == 0)
                    Problem = "Choose how often.";
                break;
            default:
                Problem = "Choose when it runs: once, every day, or every few minutes.";
                break;
        }
        if (Problem != null)
            return Partial("_TaskResults", this);
        _jobs.Add(job);
        _audit.Write(Admin.Audit, $"task scheduled: {TaskKinds.Describe(kind, fields)} ({Jobs.Schedule(job)})", null, true, "");
        Response.Headers["HX-Trigger"] = "refreshJobs";
        Problem = null;
        Results = null;
        Scheduled = Jobs.Schedule(job);
        return Partial("_TaskResults", this);
    }

    public string? Scheduled { get; private set; }

    public async Task<IActionResult> OnGetJobsAsync()
    {
        if (!await LoadAsync())
            return Forbid();
        return Partial("_TaskList", this);
    }

    public async Task<IActionResult> OnPostJobAsync(string id, string act)
    {
        if (!await LoadAsync())
            return Forbid();
        Jobs.Job? job = _jobs.Find(id);
        if (job != null && CanChange(job))
        {
            if (act == "delete")
                _jobs.Remove(id);
            else
                _jobs.SetPaused(id, act == "pause");
            _audit.Write(Admin.Audit, $"task {id} {act}", null, true, "");
        }
        List = Visible();
        return Partial("_TaskList", this);
    }

    // The maker, or a panel admin
    public bool CanChange(Jobs.Job job) => job.BySteamId == Admin.SteamId || Admin.IsPanelAdmin;

    public string ServersText(Jobs.Job job) => job.Servers.Count == 0 ? "Every server" : string.Join(", ", job.Servers);

    // The kind, fields and servers from the form; null when they're fine
    private string? Read(out TaskKinds.Kind? kind, out Dictionary<string, string> fields, out List<string> servers)
    {
        kind = TaskKinds.Find(Request.Form["kind"]);
        fields = new();
        servers = new();
        if (kind == null)
            return "Choose what to do.";
        foreach (string field in kind.Fields)
            fields[field] = Request.Form[field].ToString().Trim();
        if (kind.Id == "value")
        {
            string pickedValue = fields.GetValueOrDefault("pick") ?? "";
            if (Values.FirstOrDefault(v => v.Value == pickedValue).Label is string valueLabel)
                fields["pickLabel"] = valueLabel;
            else
                return "Choose a setting and its value.";
        }
        if (kind.Id == "rotation")
        {
            if (Request.Form["save"] == "true")
                fields["save"] = "true";
            string picked = fields.GetValueOrDefault("rotation") ?? "";
            if (Rotation.FirstOrDefault(r => r.Value == picked).Label is string rotationLabel)
                fields["rotationLabel"] = rotationLabel;
            else
                return "Choose a rotation setting.";
        }
        if (kind.Id == "setting" && fields.GetValueOrDefault("on") == "")
            fields["on"] = "true";
        if (TaskKinds.Problem(kind, fields) is string problem)
            return problem;
        // Labels, for the list and the audit
        if (fields.GetValueOrDefault("mode") is { } mode && Modes.FirstOrDefault(m => m.Name == mode) is { } m1) fields["modeLabel"] = m1.Label;
        if (fields.GetValueOrDefault("setting") is { } setting && Settings.FirstOrDefault(s => s.Name == setting) is { } s1) fields["settingLabel"] = s1.Label;
        if (fields.GetValueOrDefault("action") is { } action && Actions.FirstOrDefault(a => a.Name == action) is { } a1) fields["actionLabel"] = a1.Label;

        var mine = Servers.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        servers = Request.Form["servers"].OfType<string>().Where(mine.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (servers.Count == 0)
            return "Tick at least one server.";
        return null;
    }

    private async Task<bool> LoadAsync()
    {
        if (Viewer == null)
            return false;
        foreach (ServerEntry entry in _store.Current.Servers.Where(s => Viewer.CanUse(s.Name)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            Access access = await AccessAsync(entry.Name);
            if (access.CanManage || access.CanAdminister)
                Servers.Add(_registry.Get(entry.Name));
        }
        if (Servers.Count == 0)
            return false;
        var statuses = Servers.Select(s => s.Status).OfType<StatusSnapshot>().ToList();
        Modes = Union(statuses.SelectMany(s => s.Modes));
        Maps = Union(statuses.SelectMany(s => s.Maps));
        Settings = Union(statuses.SelectMany(s => s.Settings));
        Actions = Union(statuses.SelectMany(s => s.Actions));
        Rotation = TaskKinds.RotationChoices(statuses.FirstOrDefault()?.Rotation ?? new RotationInfo());
        // Value settings and their choices, between the servers: "setting|value" and "Label: value"
        Values = statuses.SelectMany(s => s.ValueSettings).GroupBy(v => v.Name).Select(g => g.First())
            .OrderBy(v => v.Label, StringComparer.OrdinalIgnoreCase)
            .SelectMany(v => v.Choices.Select(c => ($"{v.Name}|{c}", $"{v.Label}: {c}"))).ToList();
        List = Visible();
        return true;
    }

    // Panel admins see every task; others see their own
    private List<Jobs.Job> Visible() => _jobs.List().Where(j => Admin.IsPanelAdmin || j.BySteamId == Admin.SteamId).ToList();

    private static List<NamedItem> Union(IEnumerable<NamedItem> items) => items
        .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
        .OrderBy(i => i.Label, StringComparer.OrdinalIgnoreCase).ToList();
}
