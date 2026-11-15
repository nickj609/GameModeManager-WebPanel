using System.Text.Json;
using System.Threading.Channels;
using GmmPanel.Agents;
using GmmPanel.Protocol;
using GmmPanel.Auth;

namespace GmmPanel.Data;

// Posts to a Discord channel through a webhook: servers going offline and back, new appeals and reports, and (if wanted)
// what staff do through the panel. The webhook address is a secret (anyone with it can post), so it's kept in
// data/discord.json, apart from panel.json, and the Panel page never shows it again. CS2-SimpleAdmin posts its own bans
// and mutes with its own webhook settings.
public sealed class Discord
{
    // Posts leave out what isn't there (no picture, no link): Discord refuses a null thumbnail
    private static readonly JsonSerializerOptions Skip = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly HttpClient _http;
    private readonly ILogger<Discord> _logger;
    private readonly Channel<Message> _queue = Channel.CreateBounded<Message>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly object _lock = new();
    private DiscordSettings _settings;
    private readonly PanelStore _store;
    private readonly IServiceProvider _services;

    public Discord(PanelStore store, HttpClient http, ILogger<Discord> logger, IServiceProvider services)
    {
        _store = store;
        _services = services;
        _path = Path.Combine(store.DataDirectory, "discord.json");
        _http = http;
        _logger = logger;
        _settings = Read();
        _ = Task.Run(SendLoopAsync);
    }

    public sealed class DiscordSettings
    {
        public string Url { get; set; } = "";
        public bool Servers { get; set; } = true;     // Offline (after 2 minutes) and back online
        public bool Tickets { get; set; } = true;     // New appeals and reports
        public bool Actions { get; set; }             // Every command staff send through the panel
        public string PanelUrl { get; set; } = "";    // The panel's address when the settings were saved: links and map pictures
    }

    public DiscordSettings Settings
    {
        get { lock (_lock) return new DiscordSettings { Url = _settings.Url, PanelUrl = _settings.PanelUrl, Servers = _settings.Servers, Tickets = _settings.Tickets, Actions = _settings.Actions }; }
    }

    public bool On => Settings.Url.Length > 0;

    // Only Discord's own webhook addresses
    public static bool IsWebhook(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == "https"
        && uri.Host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com"
        && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal) && url.Length <= 300;

    public void Save(DiscordSettings settings)
    {
        lock (_lock)
        {
            _settings = settings;
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(settings, Json));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
    }

    // The panel's public address, for pictures, the logo and buttons in posts: learnt from panel admins' visits, so it
    // follows a new domain on its own. Discord fetches pictures itself, so a local or private address is never kept, and
    // a public one is always https (behind a tunnel the panel itself only sees http)
    public void Learn(string host)
    {
        string name = host.Split(':')[0].Trim('[', ']').ToLowerInvariant();
        if (name.Length == 0 || !name.Contains('.') || System.Net.IPAddress.TryParse(name, out _)
            || name.EndsWith(".local") || name.EndsWith(".localhost") || name.EndsWith(".lan") || name.EndsWith(".internal") || name.EndsWith(".home.arpa"))
            return;
        string url = "https://" + host.ToLowerInvariant();
        lock (_lock)
        {
            if (_settings.PanelUrl == url)
                return;
        }
        DiscordSettings settings = Settings;
        settings.PanelUrl = url;
        Save(settings);
        _logger.LogInformation("Discord posts link to {Url}", url);
    }

    // Online and offline. Online: how to connect, the map, mode, players and who's on, with the map's picture
    public void Server(string server, bool good)
    {
        if (!Settings.Servers)
            return;
        ServerView? view = View(server);
        StatusSnapshot? status = view?.Status;
        string? address = good ? view?.ConnectAddress : null;
        var facts = new List<(string, string)>();
        if (good && status?.Map.Name is { Length: > 0 } map)
            facts.Add(("🗺️ Map", Pill(map)));
        if (good)
            facts.AddRange(LiveFacts(status, status?.Mode.Label));
        Post(new Card(good ? "Server online" : "Server offline", good ? Green : Red)
        {
            Server = server,
            Change = good ? (address != null ? $"`connect {address}`" : null) : "Offline for 2 minutes.",
            Facts = facts,
            Who = good ? Who(status) : null,
            Picture = good ? MapPicture(status?.Map.Name) : null,
            Connect = address != null
        });
    }

    // Appeals and reports: who wrote it, who it's about (a report), the server, and the message quoted; a Steam profile
    // button for the reported player, else the writer
    public void Ticket(string title, string from, string fromId, string message, string? server = null, string? about = null, string? aboutId = null)
    {
        if (!Settings.Tickets)
            return;
        var facts = new List<(string, string)> { ("👤 From", Pill(from)), ("🆔 SteamID", $"**{Plain(fromId)}**") };
        if (about != null)
            facts.Add(("🚩 Reported", Pill(about)));
        if (aboutId != null && aboutId != about)
            facts.Add(("🆔 Their SteamID", $"**{Plain(aboutId)}**"));
        string quoted = string.Join("\n", Plain(message.Trim(), 1000).Split('\n').Select(line => "> " + line));
        string profile = aboutId ?? fromId;
        Post(new Card(title, Warn())
        {
            Server = string.IsNullOrEmpty(server) ? null : server,
            Facts = facts,
            Body = quoted,
            SteamProfile = profile.Length == 17 && profile.All(char.IsAsciiDigit) ? $"https://steamcommunity.com/profiles/{profile}" : null
        });
    }

    // A staff action on a server, worded for people ("Map changed", from de_dust2 to de_inferno), who did it, and each
    // other value. A map or mode change adds the mode, players, who's on and the new map's picture (a mode: the first of
    // its maps that has one).
    public void Action(string staff, string server, string name, IReadOnlyDictionary<string, string> args)
    {
        if (!Settings.Actions)
            return;
        StatusSnapshot? status = View(server)?.Status;   // Still the old map: the change takes a moment
        string what = $"{name} {string.Join(" ", args.Where(a => a.Key != "confirm").Select(a => $"{a.Key}={a.Value}"))}".Trim();
        string? map = args.GetValueOrDefault("map");
        string? mode = args.GetValueOrDefault("mode");
        string? modeLabel = mode == null ? null : status?.Modes.FirstOrDefault(m => m.Name.Equals(mode, StringComparison.OrdinalIgnoreCase))?.Label ?? mode;
        var (title, text, used) = Describe(name, args, status, map, modeLabel, what);
        bool mapOrMode = name is "change_map" or "change_mode" or "set_next_map" or "set_next_mode";
        var facts = mapOrMode ? LiveFacts(status, name == "change_mode" ? modeLabel : status?.Mode.Label, staff) : LiveFacts(null, null, staff);
        string? steamId = name == "moderate" && args.GetValueOrDefault("steamid") is { Length: 17 } id && id.All(char.IsAsciiDigit) ? id : null;
        if (steamId != null)
            facts.Add(("🆔 SteamID", $"**{steamId}**"));
        foreach (var (key, value) in args)
            if (key is not ("confirm" or "kind" or "server" or "source" or "list") && value.Length > 0 && !used.Contains(key))
                facts.Add((key == "reason" ? "📝 Reason" : Words(key), Pill(value, 200)));
        if (map == null && mode != null)
            map = status?.Modes.FirstOrDefault(m => m.Name.Equals(mode, StringComparison.OrdinalIgnoreCase))?.Maps.FirstOrDefault(m => Pictures?.UrlFor(m) != null);
        bool now = name is "change_map" or "change_mode";
        // Bans, mutes, gags and warns go through CS2-SimpleAdmin and count on every server: no server name or page.
        // A kick or slay is on this server.
        bool everywhere = name == "moderate" && args.GetValueOrDefault("action") is not ("kick" or "slay");
        Post(new Card(title, Accent())
        {
            Server = everywhere ? null : server,
            SteamProfile = steamId == null ? null : $"https://steamcommunity.com/profiles/{steamId}",
            Change = text,
            Facts = facts,
            Who = mapOrMode ? Who(status) : null,
            Picture = MapPicture(map),
            Connect = now && View(server)?.ConnectAddress != null
        });
    }

    // Mode, players and who did it, each label over its value
    private static List<(string Label, string Value)> LiveFacts(StatusSnapshot? status, string? mode, string? staff = null)
    {
        var facts = new List<(string, string)>();
        if (mode is { Length: > 0 })
            facts.Add(("🕹️ Mode", Pill(mode)));
        if (status != null)
            facts.Add(("👥 Players", $"**{status.Players} / {status.MaxPlayers}**"));
        if (staff != null)
            facts.Add(("🛡️ Staff", Pill(staff)));
        return facts;
    }

    // Who's on: up to 10 by score with kills / assists / deaths (assists when the agent sends them), flags when Player
    // countries is on
    private string? Who(StatusSnapshot? status)
    {
        var roster = (status?.Roster ?? new()).Where(p => p.Team != "Spectator").OrderByDescending(p => p.Score).ToList();
        if (roster.Count == 0)
            return null;
        bool flags = Features.On(_store.Current, "countries");
        var lines = roster.Take(10).Select(p => $"{(flags ? Flag(p.Country) : "")}**{Plain(p.Name)}**  {p.Kills} / {(p.Assists is int a ? $"{a} / " : "")}{p.Deaths}").ToList();
        if (roster.Count > 10)
            lines.Add($"-# +{roster.Count - 10} more");
        return string.Join("\n", lines);
    }

    // A country's flag emoji (two regional letters), with a space after; nothing when unknown
    private static string Flag(string? country) => country is { Length: 2 } c && c.All(char.IsAsciiLetter)
        ? char.ConvertFromUtf32(0x1F1E6 + char.ToUpperInvariant(c[0]) - 'A') + char.ConvertFromUtf32(0x1F1E6 + char.ToUpperInvariant(c[1]) - 'A') + " " : "";
    // A value: bold, its formatting characters escaped
    private static string Pill(string text, int max = 64) => $"**{Plain(text.Replace('\n', ' '), max)}**";
    // Text as Discord shows it, not as formatting: map names' underscores, players' names, reasons
    private static string Plain(string text, int max = 64) => System.Text.RegularExpressions.Regex.Replace(text.Length > max ? text[..max] : text, @"([\\*_`~|>#\[\]()<@:-])", @"\$1");

    // What staff did, in words: a title ("Setting changed"), a line saying what changed (the old value plain, the new one
    // bold, On 🟢 and Off 🔴) and the values the line already shows (left out of the details)
    private (string Title, string? Text, string[] Used) Describe(string name, IReadOnlyDictionary<string, string> a, StatusSnapshot? status,
        string? map, string? mode, string what)
    {
        string? Get(string key) => a.GetValueOrDefault(key);
        static string New(string text) => Pill(text);
        static string Change(string? from, string to) => from is { Length: > 0 } && !from.Equals(to, StringComparison.OrdinalIgnoreCase) ? $"{Plain(from)}  →  {New(to)}" : New(to);
        static string OnOff(string? v) => v == "true" ? "🟢 **On**" : "🔴 **Off**";
        static string Team(string? t) => t == "ct" ? "**CT**" : t == "t" ? "**T**" : New(t ?? "");
        const string Dot = "  ·  ";
        (string, string?, string[]) Say(string title, string? line, params string[] used) => (title, line, used);
        switch (name)
        {
            case "change_map" when map != null: return Say("Map changed", Change(status?.Map.Name, map), "map");
            case "set_next_map" when map != null: return Say("Next map set", Change(status?.NextMap, map), "map");
            case "change_mode" when mode != null: return Say("Mode changed", Change(status?.Mode.Label, mode), "mode");
            case "set_next_mode" when mode != null: return Say("Next mode set", New(mode), "mode");
            case "random_map": return Say("Random map", "A random map from " + New(status?.Mode.Label ?? "this mode"));
            case "random_mode": return Say("Random mode", "A random mode and one of its maps");
            case "start_vote":
                return Say("Vote started", Get("kind") switch
                {
                    "map" when map != null => $"Change the map to {New(map)}?",
                    "mode" when mode != null => $"Change the mode to {New(mode)}?",
                    _ => "Random map vote"
                }, "map", "mode");
            case "set_setting":
            {
                SettingInfo? setting = status?.Settings.FirstOrDefault(x => x.Name == Get("setting"));
                string to = OnOff(Get("on"));
                bool changed = setting?.State is "On" or "Off" && setting.State != (Get("on") == "true" ? "On" : "Off");
                return Say("Setting changed", New(setting?.Label ?? Get("setting") ?? "") + Dot + (changed ? OnOff(setting!.State == "On" ? "true" : "false") + "  →  " + to : to), "setting", "on");
            }
            case "set_value":
            {
                ValueSettingInfo? setting = status?.ValueSettings.FirstOrDefault(x => x.Name == Get("setting"));
                return Say("Setting changed", New(setting?.Label ?? Get("setting") ?? "") + Dot + Change(setting?.Value, Get("value") ?? ""), "setting", "value");
            }
            case "run_action": return Say("Action run", New(status?.Actions.FirstOrDefault(x => x.Name == Get("action"))?.Label ?? Get("action") ?? ""), "action");
            case "start_warmup": return Say("Warmup started", Get("mode") is { } w ? New(w) : null, "mode");
            case "schedule_warmup": return Say("Warmup scheduled", (Get("mode") is { } w2 ? New(w2) + Dot : "") + "After this map", "mode");
            case "end_warmup": return Say("Warmup ended", null);
            case "pause": return Say("Pause called", null);
            case "cancel_pause": return Say("Pause cancelled", null);
            case "unpause": return Say("Match resumed", null);
            case "timeout": return Say("Timeout", "For " + Team(Get("team")), "team");
            case "replay_round": return Say("Round replayed", null);
            case "replay_last": return Say("Last round replayed", null);
            case "restore_round": return Say("Round restored", "Round " + New(Get("round") ?? ""), "round");
            case "freeze": return Say(Get("on") == "true" ? "Players frozen" : "Players unfrozen", null, "on");
            case "end_round": return Say("Round ended", null);
            case "restart_match": return Say("Match restarted", null);
            case "set_rotation":
            {
                string key = Get("key") ?? "", value = Get("value") ?? "";
                string label = key switch { "maps" => "Map rotation", "modes" => "Mode rotation", "interval" => "Rotation interval", "rtv" => "Rock the vote", _ => Words(key) };
                return Say("Rotation changed", New(label) + Dot + (value is "true" or "false" ? OnOff(value) : New(value)), "key", "value");
            }
            case "matchzy": return Say("MatchZy", New(Words(Get("action") ?? "")), "action");
            case "console": return Say("Console command", "```\n" + (Get("line") ?? "").Replace("```", "'''") + "\n```", "line");
            case "set_hostname": return Say("Hostname changed", New(Get("value") ?? ""), "value");
            case "set_teamname": return Say("Team name changed", Team(Get("team")) + Dot + New(Get("value") is { Length: > 0 } v ? v : "Default"), "team", "value");
            case "plugin":
                return Say(Get("action") switch { "load" => "Plugin loaded", "unload" => "Plugin unloaded", "reload" => "Plugin reloaded", _ => "Plugin" },
                    New(Get("plugin") ?? ""), "plugin", "action");
            case "moderate":
            {
                string action = Get("action") ?? "";
                string title = action switch
                {
                    "ban" => "Player banned", "unban" => "Player unbanned", "mute" => "Player muted", "unmute" => "Player unmuted",
                    "gag" => "Player gagged", "ungag" => "Player ungagged", "silence" => "Player silenced", "unsilence" => "Player unsilenced",
                    "kick" => "Player kicked", "warn" => "Player warned", "slay" => "Player slain", _ => AuditText.Describe(what).Title
                };
                string? id = Get("steamid");
                string? player = Get("name") is { Length: > 0 } typed ? typed : id == null ? null : _services.GetService<SteamProfile>()?.KnownName(id) ?? id;
                string? line = player == null ? null : New(player);
                if (Get("minutes") is { Length: > 0 } minutes)
                    line = (line == null ? "" : line + Dot) + "⏱️ " + Pill(minutes == "0" ? "Permanent" : Length(minutes));
                return Say(title, line, "action", "steamid", "name", "minutes");
            }
            default: return Say(AuditText.Describe(what).Title, null);
        }
    }

    // "60" reads "1 hour", "1440" reads "1 day"
    private static string Length(string minutes) => !int.TryParse(minutes, out int m) ? minutes
        : m % 1440 == 0 ? $"{m / 1440} day{(m == 1440 ? "" : "s")}" : m % 60 == 0 ? $"{m / 60} hour{(m == 60 ? "" : "s")}" : $"{m} minutes";

    public void Test() => Post(new Card("Webhook connected", Accent()) { Change = "The panel's posts will appear in this channel." });

    private const int Green = 0x3BA55C, Red = 0xED4245;
    private int Accent() => Colour(_store.Current.Site.Accent, 0xD4801A);
    private int Warn() => Colour(_store.Current.Site.Warn, 0xF5A623);
    private static int Colour(string hex, int fallback) => SiteLook.IsColour(hex) ? Convert.ToInt32(hex[1..7], 16) : fallback;

    // "steamid" reads "SteamID", "reason" reads "Reason", "duration_minutes" reads "Duration minutes"
    private static string Words(string key) => key.Equals("steamid", StringComparison.OrdinalIgnoreCase) ? "SteamID"
        : key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key[1..].Replace('_', ' ');

    private ServerView? View(string server) => _services.GetService<AgentRegistry>()?.Find(server);
    private MapPictures? Pictures => _services.GetService<MapPictures>();
    // Absolute addresses: only once the panel's address is known (saved with the settings). Discord fetches pictures
    // itself, so they need an address it can reach (not localhost)
    private string? Absolute(string? path) => path == null || PanelAddress() is not { } root ? null : root + path;
    // Learnt from admins' visits (Learn), else the agent address saved in Site settings or setup (wss://host/agent)
    private string? PanelAddress()
    {
        if (Settings.PanelUrl.Length > 0)
            return Settings.PanelUrl.TrimEnd('/');
        return Uri.TryCreate(_store.Current.AgentUrl, UriKind.Absolute, out Uri? agent) && agent.Host.Contains('.') ? $"https://{agent.Authority}" : null;
    }
    private string? MapPicture(string? map) => Absolute(Pictures?.UrlFor(map));

    private sealed record Card(string Title, int Color)
    {
        public string? Server { get; init; }
        public string? Change { get; init; }          // Under the title: what changed
        public List<(string Label, string Value)>? Facts { get; init; }   // Each a small grey label over its value
        public string? Body { get; init; }            // A ticket's message
        public string? Who { get; init; }             // Who's on
        public string? Picture { get; init; }         // The map picture, full width
        public bool Connect { get; init; }
        public string? SteamProfile { get; init; }    // A Steam profile button (moderation posts)
    }

    // The post, in Discord's newer layout (components v2): one card with the accent down its side; the server's name small
    // over a heading; what changed; a divider; the details (each a small grey label over its value), the message and
    // who's on; the map picture; link buttons (Connect opens Steam through the panel's /connect page); a divider; and the
    // site's name with the time (in each reader's own time zone)
    private void Post(Card card)
    {
        if (!On)
            return;
        string name = _store.Current.Site.Name is { Length: > 0 } site ? site : "GameModeManager";
        string? logo = _services.GetService<SiteTheme>() is { LogoIsSvg: false, LogoUrl: { } path } ? Absolute(path) : null;
        string? page = card.Server == null ? null : Absolute($"/server/{Uri.EscapeDataString(card.Server)}");
        static object Text(string content) => new { type = 10, content = content.Length > 1900 ? content[..1900] : content };
        static object Divider() => new { type = 14, divider = true, spacing = 1 };

        var parts = new List<object>();
        parts.Add(Text((card.Server != null ? $"-# {Plain(ServerNames.Label(card.Server)).ToUpperInvariant()}\n" : "") + $"## {card.Title}"
            + (card.Change != null ? $"\n{card.Change}" : "")));
        var middle = new List<string>();
        if (card.Facts is { Count: > 0 } facts)
            middle.Add(string.Join("\n", facts.Select(f => $"-# {f.Label}\n{f.Value}")));
        if (card.Body != null)
            middle.Add(card.Body);
        if (card.Who != null)
            middle.Add("-# 📋 Who's on\n" + card.Who);
        if (middle.Count > 0)
        {
            parts.Add(Divider());
            parts.Add(Text(string.Join("\n\n", middle)));
        }
        if (card.Picture != null)
            parts.Add(new { type = 12, items = new[] { new { media = new { url = card.Picture } } } });
        var buttons = new List<object>();
        if (card.Connect && card.Server != null && Absolute($"/connect/{Uri.EscapeDataString(card.Server)}") is { } connect)
            buttons.Add(new { type = 2, style = 5, label = "Connect", url = connect });
        if (card.SteamProfile != null)
            buttons.Add(new { type = 2, style = 5, label = "Steam profile", url = card.SteamProfile });
        if (page != null)
            buttons.Add(new { type = 2, style = 5, label = "View server", url = page });
        if (buttons.Count > 0)
            parts.Add(new { type = 1, components = buttons });
        parts.Add(Divider());
        parts.Add(Text($"-# {Plain(name)}  ·  <t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:f>"));

        // No @everyone or role pings from anything a player typed. Flag 32768: the message is made of components only.
        _queue.Writer.TryWrite(new Message(new
        {
            username = name.Length > 80 ? name[..80] : name,
            avatar_url = logo,
            flags = 32768,
            allowed_mentions = new { parse = Array.Empty<string>() },
            components = new[] { new { type = 17, accent_color = card.Color, components = parts } }
        }, $"**{card.Title}**" + (card.Server != null ? $" · {Plain(ServerNames.Label(card.Server))}" : "") + (card.Change != null ? $"\n{card.Change}" : "")));
    }

    // A post and a plain-text stand-in, sent if Discord refuses the card
    private sealed record Message(object Card, string Fallback);

    // One at a time, a second apart: Discord's webhook limit is about 30 a minute
    private async Task SendLoopAsync()
    {
        await foreach (Message message in _queue.Reader.ReadAllAsync())
        {
            string url = Settings.Url;
            if (url.Length == 0)
                continue;
            try
            {
                // A webhook the panel doesn't own sends components only with with_components
                using var content = new StringContent(JsonSerializer.Serialize(message.Card, Skip), System.Text.Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await _http.PostAsync(url + (url.Contains('?') ? "&" : "?") + "with_components=true", content);
                if ((int)response.StatusCode == 400)
                {
                    _logger.LogWarning("Discord refused a post: {Answer}", await response.Content.ReadAsStringAsync());
                    using var plain = new StringContent(JsonSerializer.Serialize(new { content = message.Fallback, allowed_mentions = new { parse = Array.Empty<string>() } }), System.Text.Encoding.UTF8, "application/json");
                    using HttpResponseMessage second = await _http.PostAsync(url, plain);
                }
                else if (!response.IsSuccessStatusCode)
                    _logger.LogWarning("Discord answered {Status} to a post", (int)response.StatusCode);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning("Discord post failed: {Error}", error.Message);
            }
            await Task.Delay(1000);
        }
    }

    private DiscordSettings Read()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<DiscordSettings>(File.ReadAllText(_path), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
