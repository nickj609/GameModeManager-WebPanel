using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GmmPanel.Data;

// panel.json in the data folder: who may sign in, and which game servers may connect. Edited with the CLI
// (gmmpanel admin add, gmmpanel token new) or by hand; changes are picked up without a restart.
public sealed class PanelData
{
    public List<AdminEntry> Admins { get; set; } = new();
    public List<ServerEntry> Servers { get; set; } = new();
    public string AgentUrl { get; set; } = "";           // Where game servers connect (wss://.../agent), for GameModeManager.json; "" = the panel's own address
    public Dictionary<string, string> Permissions { get; set; } = new();   // Panel area -> admin flag, where it differs from the default (Auth/PanelPermissions.cs)
    public List<LinkEntry> Links { get; set; } = new();   // Icons in the header: the community's Discord, website, store ...
    public SiteLook Site { get; set; } = new();          // How the panel looks for everyone (Site settings, panel admins)
    public Retention Retention { get; set; } = new();    // How long the panel keeps its own records (Panel page)
    public Dictionary<string, string> BackgroundNames { get; set; } = new();   // Site settings: a background file's display name in the picker
    public Dictionary<string, bool> Integrations { get; set; } = new();   // Site settings, Features: "feature:<id>" switched off (false) is hidden
    public string Rules { get; set; } = "";             // Site settings: the server rules, one per line ("## " starts a section); /rules shows them
}

public sealed class AdminEntry
{
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = Roles.Admin;
    public List<string> Servers { get; set; } = new();   // Empty: every server

    public bool CanUse(string server) => Servers.Count == 0 || Servers.Contains(server, StringComparer.OrdinalIgnoreCase);
}

// The site's own look, for everyone: name, logo, footer, colours and a pattern. Empty or null keeps the built-in default.
// Each viewer can still change accent, opacity and blur for themselves on the Appearance page.
public sealed class SiteLook
{
    public string Name { get; set; } = "";          // The tab title and the text logo (when there's no logo picture)
    public string Sub { get; set; } = "";           // The small line under the logo ("GameModeManager")
    public string Footer { get; set; } = "";        // The footer's left side
    public string Logo { get; set; } = "";          // data/branding/<file>, or "" for the built-in logo
    public string FooterLogo { get; set; } = "";    // data/branding/<file>: the footer's own logo, or "" for the logo above
    public string Favicon { get; set; } = "";       // data/branding/<file>: the browser tab's icon, or "" for the panel's crosshair
    public string Accent { get; set; } = "";        // #rrggbb
    public string Warn { get; set; } = "";          // #rrggbb: heads-up states (reports, notes, pauses); "" = orange
    public string Bars { get; set; } = "";          // Header and footer
    public string Panel { get; set; } = "";         // The main panel
    public string Page { get; set; } = "";          // Behind everything (shows round the backdrop scene's edges and while it loads)
    public string Notify { get; set; } = "";        // Pop-ups: the toasts and the bell's list; "" = charcoal
    public string Pattern { get; set; } = "none";   // none, dots, grid, lines
    public int PatternStrength { get; set; } = 6;    // 1 to 20 (percent)
    public int Radius { get; set; } = 8;            // Corner rounding, 0 to 16 px
    public string Background { get; set; } = "";   // The default background: "" = Auto (the map being played, else Dust II), or "own:<file>"; each person can pick another
    public bool Motion { get; set; } = true;        // The backdrop's slow drift and dust, on or off to start with; each person can switch it
    public int Dim { get; set; } = 5;               // How dark the backdrop starts, 0 to 100 percent; each person can change it on Appearance
    public string Language { get; set; } = "";      // The default language (lang/<code>.json); "" = each browser's own
    public string Theme { get; set; } = "dark";     // dark, light, or auto (each visitor's device); a viewer can pick their own on Appearance

    public static readonly string[] Patterns = { "none", "dots", "grid", "lines" };
    public static readonly string[] Themes = { "dark", "light", "auto" };
    public static string ThemeOf(string? theme) => Themes.Contains(theme) ? theme! : "dark";
    public static bool IsColour(string? value) => value is { Length: 7 } && value[0] == '#' && value[1..].All(Uri.IsHexDigit);

    // The dark look's text is light, so the colours under it must stay dark enough to read it on; the surface colours are
    // for the dark look only (light mode has its own palette). The accent is in both: dark enough for white button text,
    // and, while the dark look can show, light enough to see on it. The heads-up colour: bright enough on dark, and not so
    // pale that it vanishes on light (light mode darkens it for words). Brightness as WCAG's relative luminance (0 to 1).
    public const double MaxSurface = 0.06, MaxAccent = 0.30, MinAccent = 0.03, MinWarn = 0.15, MaxWarnLight = 0.75;

    public static double Luminance(string hex)
    {
        double Channel(int i)
        {
            double c = Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    // Why a colour can't be used for that part of the site, or null. Whatever the site's theme, each viewer can pick either
    // one on Appearance, so a colour both themes use must suit both.
    public static string? ColourProblem(string field, string? hex)
    {
        if (!IsColour(hex))
            return null;
        double l = Luminance(hex!);
        return field switch
        {
            "accent" when l > MaxAccent => "That accent is too light: white text on buttons wouldn't show. Pick a deeper colour.",
            "accent" when l < MinAccent => "That accent is too dark to see on the dark panel. Pick a brighter colour.",
            "warn" when l < MinWarn => "That notifications colour is too dark to read on the dark panel. Pick a brighter colour.",
            "warn" when l > MaxWarnLight => "That notifications colour is too pale to see on the light panel. Pick a deeper colour.",
            "bars" or "panel" or "page" or "notify" when l > MaxSurface => "Header, panel, page and pop-up colours are for the dark look and must be dark: its text is light and wouldn't show on that.",
            _ => null
        };
    }
}

// A link in the header, as an icon (Panel page)
public sealed class LinkEntry
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
    public string Icon { get; set; } = "link";
    public string Color { get; set; } = "";      // #rrggbb for its icon in the footer, or "" for the site's accent

    public static readonly string[] Icons = { "discord", "globe", "steam", "youtube", "twitch", "store", "heart", "chat", "link" };
}

public sealed class ServerEntry
{
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";   // SHA-256 of the server's token, hex. The token itself is never stored
}

// Panel admins: everyone in panel.json's list (made by the setup wizard, the Panel page or `gmmpanel admin add`). They can do
// everything, even while every server is offline, and only they see the panel's own settings and audit log. Everyone else
// is a player whose rights on each server come from that server's own admin flags and groups (Auth/Access.cs).
// "owner" is still read from older panel.json files: it's the same as "admin".
public static class Roles
{
    public const string Admin = "admin";
    public const string Player = "player";

    public static bool IsValid(string role) => role is "admin" or "owner";
}

public sealed class PanelStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private PanelData _data = new();
    private DateTime _loadedWriteTime = DateTime.MinValue;
    private DateTime _checkedAt = DateTime.MinValue;

    public PanelStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        DataDirectory = dataDirectory;
        _path = Path.Combine(dataDirectory, "panel.json");
    }

    public string DataDirectory { get; }

    // Reread the file when it changed, checked at most every 2 seconds
    public PanelData Current
    {
        get
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _checkedAt < TimeSpan.FromSeconds(2))
                    return _data;
                _checkedAt = DateTime.UtcNow;
                if (!File.Exists(_path))
                    return _data = new PanelData();
                DateTime written = File.GetLastWriteTimeUtc(_path);
                if (written != _loadedWriteTime)
                {
                    _data = JsonSerializer.Deserialize<PanelData>(File.ReadAllText(_path), Json) ?? new PanelData();
                    _loadedWriteTime = written;
                }
                return _data;
            }
        }
    }

    public AdminEntry? FindAdmin(string steamId) =>
        Current.Admins.FirstOrDefault(admin => admin.SteamId == steamId);

    // Which server a token belongs to. Every entry is compared, in fixed time, so timing says nothing about the hashes.
    public ServerEntry? FindServerByToken(string token)
    {
        byte[] hash = Convert.FromHexString(HashToken(token));
        ServerEntry? found = null;
        foreach (ServerEntry server in Current.Servers)
        {
            byte[] stored;
            try { stored = Convert.FromHexString(server.TokenHash); }
            catch (FormatException) { continue; }
            if (CryptographicOperations.FixedTimeEquals(hash, stored))
                found = server;
        }
        return found;
    }

    public void Save(PanelData data)
    {
        lock (_lock)
        {
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, Json));
            File.Move(temp, _path, overwrite: true);
            _checkedAt = DateTime.MinValue;
        }
    }

    public PanelData LoadForEdit()
    {
        lock (_lock)
        {
            _checkedAt = DateTime.MinValue;
        }
        PanelData current = Current;
        return JsonSerializer.Deserialize<PanelData>(JsonSerializer.Serialize(current, Json), Json)!;
    }

    public static bool IsServerName(string name) => name.Length is > 0 and <= 40 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // Adds the server, or gives it a new token (the old one stops working). Returns the token: shown once, only its hash kept.
    public string NewServerToken(string server)
    {
        string token = NewToken();
        PanelData data = LoadForEdit();
        data.Servers.RemoveAll(entry => entry.Name.Equals(server, StringComparison.OrdinalIgnoreCase));
        data.Servers.Add(new ServerEntry { Name = server, TokenHash = HashToken(token) });
        Save(data);
        return token;
    }

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

// How long the panel keeps what it records itself, in days (0 = keep it all). A cleanup runs every 6 hours (DataCleanup.cs);
// the Privacy page shows these numbers. Plugins' own data (bans, ranks ...) isn't the panel's to delete.
public sealed class Retention
{
    public int AuditDays { get; set; } = 365;     // audit.jsonl: sign-ins, refusals, commands
    public int ChatDays { get; set; } = 30;       // history/<server>/chat
    public int CountDays { get; set; } = 400;     // history/<server>/counts-*.csv: the graphs
    public int PlayerDays { get; set; } = 0;      // history/players.json: players not seen for this long are forgotten
    public int TicketDays { get; set; } = 365;    // Answered appeals and reports
    public int LogDays { get; set; } = 14;        // logs/panel-<date>.log: the panel's own log

    // The choices the Panel page offers for each, in days (0 = keep it all)
    public static readonly int[] Choices = { 7, 14, 30, 60, 90, 180, 365, 400, 730, 0 };

    public static int Clamp(int days, int fallback) => days == 0 || Choices.Contains(days) ? days : fallback;
}

// The panel's own features the owner can switch off (Site settings, Features): off hides the page, its links and buttons;
// what's saved (old reports, the rules) is kept. Stored as "feature:<id>" in Integrations (its old name); missing means on.
public static class Features
{
    public static readonly (string Id, string Label, string What)[] All =
    {
        ("profiles", "Public profiles", "Each player's page (/u/...), linked from names everywhere; players can still hide theirs"),
        ("reports", "Player reports", "Signed-in players report others; staff answer them under Moderate"),
        ("appeals", "Ban appeals", "Banned players appeal on My profile; staff answer them under Moderate"),
        ("rules", "Rules page", "The rules from the Rules section, linked in the footer and on the report form"),
        ("playtime", "Play-time leaderboards", "Most played, This week and Newest on Stats (from the panel's player history)"),
        ("countries", "Player countries", "A world map and the top countries on each server's page and on Stats (each player's country from GameModeManager; never their IP)"),
        ("widget", "Status widget", "The embeddable server card and /status.json for websites and bots")
    };

    public static bool On(PanelData data, string id) => !data.Integrations.TryGetValue("feature:" + id, out bool on) || on;
}

