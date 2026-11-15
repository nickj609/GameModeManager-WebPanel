using System.Security.Cryptography;
using System.Text.Json;
using GmmPanel.Agents;
using GmmPanel.Auth;
using static GmmPanel.Data.Lang;

namespace GmmPanel.Data;

// A signed-in person's own notices, for the bell: a new ban, mute, gag or warning (from CS2-SimpleAdmin's records, however
// it was given), one that ended or was lifted, a new role (or losing one), an appeal or report answered, and a kick
// done through the panel (a kick in game leaves no record the panel can read). Checked when they open a page, at most every
// 2 minutes; the first check only notes where things stand, so nobody gets a pile of old news. data/notices.json.
public sealed class Notices
{
    private const int Keep = 30;   // Newest notices kept per person
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, Person> _people;
    private readonly PublicData _data;
    private readonly Tickets _tickets;

    public Notices(PanelStore store, PublicData data, Tickets tickets)
    {
        _path = Path.Combine(store.DataDirectory, "notices.json");
        _data = data;
        _tickets = tickets;
        _people = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, Person>>(File.ReadAllText(_path), Json) ?? new() : new();
    }

    public sealed class Person
    {
        public DateTimeOffset? Checked { get; set; }
        public string Role { get; set; } = "";
        public Dictionary<string, string> Records { get; set; } = new();   // "bans|MUTE|<created>|<reason>" -> status
        public List<Notice> Items { get; set; } = new();
    }

    // Kind: ban, mute, warn, ended, role, ticket, kick. Text is in English and translated when shown (T), with Value
    // filled into its {0}.
    public sealed record Notice(string Id, DateTimeOffset At, string Kind, string Text, string Value, string Link, bool Read);

    public List<Notice> For(string steamId)
    {
        lock (_lock)
            return _people.TryGetValue(steamId, out Person? p) ? p.Items.OrderByDescending(n => n.At).ToList() : new();
    }

    public int Unread(string steamId)
    {
        lock (_lock)
            return _people.TryGetValue(steamId, out Person? p) ? p.Items.Count(n => !n.Read) : 0;
    }

    public void MarkRead(string steamId)
    {
        lock (_lock)
        {
            if (!_people.TryGetValue(steamId, out Person? p) || p.Items.All(n => n.Read))
                return;
            p.Items = p.Items.Select(n => n with { Read = true }).ToList();
            Save();
        }
    }

    // From the panel's own commands: a kick it sent (CommandService)
    public void Add(string steamId, string kind, string text, string value, string link)
    {
        lock (_lock)
        {
            if (!_people.TryGetValue(steamId, out Person? p))
                return;   // Only people who use the panel get notices
            Push(p, kind, text, value, link);
            Save();
        }
    }

    // What's new for this person since the last check (their role from their access on the selected server)
    public async Task CheckAsync(PanelUser user, Access access)
    {
        Person p;
        lock (_lock)
        {
            if (!_people.TryGetValue(user.SteamId, out p!))
                _people[user.SteamId] = p = new Person();
            if (p.Checked is { } last && DateTimeOffset.UtcNow - last < Every)
                return;
        }
        bool first = p.Checked == null;
        DateTimeOffset since = p.Checked ?? DateTimeOffset.UtcNow;

        // CS2-SimpleAdmin's records about them: new ones, and ones that stopped being active
        var records = new Dictionary<string, (string List, string Type, string Status, string Length, string Reason)>();
        foreach (string list in new[] { "bans", "mutes", "warns" })
        {
            if (!_data.Offers("simpleadmin", list))
                continue;
            CommandResult result = await _data.OwnAsync("simpleadmin", list, user.SteamId);
            foreach (var row in result.Data?.Rows ?? new())
            {
                string type = row.GetValueOrDefault("type") ?? "";
                string key = $"{list}|{type}|{row.GetValueOrDefault("created")}|{row.GetValueOrDefault("reason")}";
                records[key] = (list, type, (row.GetValueOrDefault("status") ?? "").ToUpperInvariant(), row.GetValueOrDefault("length") ?? "", row.GetValueOrDefault("reason") ?? "");
            }
        }

        lock (_lock)
        {
            if (!first)
            {
                foreach (var (key, r) in records)
                {
                    string what = r.List switch { "bans" => "ban", "warns" => "warn", _ => r.Type is "GAG" ? "gag" : r.Type is "SILENCE" ? "silence" : "mute" };
                    string reason = r.Reason.Length > 0 ? r.Reason : "-";
                    if (!p.Records.TryGetValue(key, out string? before))
                        Push(p, r.List == "bans" ? "ban" : r.List == "warns" ? "warn" : "mute", what switch
                        {
                            "ban" => "You were banned ({0}).",
                            "gag" => "You were gagged ({0}).",
                            "silence" => "You were silenced ({0}).",
                            "warn" => "You were warned ({0}).",
                            _ => "You were muted ({0})."
                        }, $"{r.Length}: {reason}".Trim(' ', ':'), "/profile");
                    else if (before == "ACTIVE" && r.Status != "ACTIVE")
                        Push(p, "ended", what == "ban" ? "Your ban is over." : "Your mute or gag is over.", "", "/profile");
                }
            }
            p.Records = records.ToDictionary(r => r.Key, r => r.Value.Status);

            // Role
            string role = access.RoleName is "Visitor" ? "Player" : access.RoleName;
            if (!first && p.Role.Length > 0 && role != p.Role)
                Push(p, "role", role == "Player" ? "You're no longer {0}." : "You're now {0}.", role == "Player" ? p.Role : role, "/profile");
            p.Role = role;

            // Their appeals and reports, answered since the last check
            if (!first)
                foreach (var t in _tickets.By(user.SteamId).Where(t => t.SteamId == user.SteamId && t.HandledAt > since))
                    Push(p, "ticket", t.Kind == "appeal"
                        ? (t.Status == "ACCEPTED" ? "Your appeal was accepted." : "Your appeal was rejected.")
                        : "Your report was answered.", "", "/profile#tickets");

            p.Checked = DateTimeOffset.UtcNow;
            Save();
        }
    }

    private static void Push(Person p, string kind, string text, string value, string link)
    {
        p.Items.Add(new Notice(Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant(), DateTimeOffset.UtcNow, kind, text, value, link, false));
        if (p.Items.Count > Keep)
            p.Items = p.Items.OrderByDescending(n => n.At).Take(Keep).ToList();
    }

    // In the reader's language
    public static string Show(Notice n) => n.Value.Length > 0 ? F(n.Text, n.Kind == "role" ? T(n.Value) : n.Value) : T(n.Text);

    private void Save()
    {
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_people, Json));
        File.Move(_path + ".tmp", _path, overwrite: true);
    }
}
