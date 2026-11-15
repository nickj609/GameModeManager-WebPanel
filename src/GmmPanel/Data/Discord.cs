using System.Text.Json;
using System.Threading.Channels;

namespace GmmPanel.Data;

// Posts to a Discord channel through a webhook: servers going offline and back, new appeals and reports, and (if wanted)
// what staff do through the panel. The webhook address is a secret (anyone with it can post), so it's kept in
// data/discord.json, apart from panel.json, and the Panel page never shows it again. CS2-SimpleAdmin posts its own bans
// and mutes with its own webhook settings.
public sealed class Discord
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly HttpClient _http;
    private readonly ILogger<Discord> _logger;
    private readonly Channel<object> _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly object _lock = new();
    private DiscordSettings _settings;

    public Discord(PanelStore store, HttpClient http, ILogger<Discord> logger)
    {
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
    }

    public DiscordSettings Settings
    {
        get { lock (_lock) return new DiscordSettings { Url = _settings.Url, Servers = _settings.Servers, Tickets = _settings.Tickets, Actions = _settings.Actions }; }
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

    public void Server(string text, bool good) { if (Settings.Servers) Post(good ? "Server online" : "Server offline", text, good ? 0x3BA55C : 0xED4245); }
    public void Ticket(string title, string text) { if (Settings.Tickets) Post(title, text, 0xF5A623); }
    // A staff action on a server: its title as the Audit log words it ("Change map"), then the server, who, and the parameters
    public void Action(string staff, string server, string what)
    {
        if (!Settings.Actions)
            return;
        AuditText.Action action = AuditText.Describe(what);
        string text = $"**Server:** {server}\n**Staff:** {staff}" + (action.Code != null ? $"\n**Parameters:** `{action.Code.Replace('`', '\'')}`" : "");
        Post(action.Title, text, 0x5865F2);
    }
    public void Test() => Post("Webhook connected", "The panel's posts will appear in this channel.", 0x5865F2);

    private void Post(string title, string text, int color)
    {
        if (!On)
            return;
        // No @everyone or role pings from anything a player typed
        _queue.Writer.TryWrite(new
        {
            username = "GameModeManager",
            allowed_mentions = new { parse = Array.Empty<string>() },
            embeds = new[] { new { title, description = text.Length > 1500 ? text[..1500] : text, color, timestamp = DateTimeOffset.UtcNow } }
        });
    }

    // One at a time, a second apart: Discord's webhook limit is about 30 a minute
    private async Task SendLoopAsync()
    {
        await foreach (object message in _queue.Reader.ReadAllAsync())
        {
            string url = Settings.Url;
            if (url.Length == 0)
                continue;
            try
            {
                using var content = new StringContent(JsonSerializer.Serialize(message), System.Text.Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await _http.PostAsync(url, content);
                if (!response.IsSuccessStatusCode)
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
