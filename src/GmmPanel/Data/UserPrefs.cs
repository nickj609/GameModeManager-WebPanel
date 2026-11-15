using System.Text.Json;

namespace GmmPanel.Data;

// What people set about themselves (data/users.json, by SteamID): their Discord username, shown on their profile card and,
// for staff, on their player page; and whether their public profile (/u/<steamid>) is hidden. Nobody else can change them.
public sealed class UserPrefs
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, Prefs> _users;

    public UserPrefs(PanelStore store)
    {
        _path = Path.Combine(store.DataDirectory, "users.json");
        try
        {
            _users = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, Prefs>>(File.ReadAllText(_path), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            _users = new();
        }
    }

    public sealed class Prefs
    {
        public string Discord { get; set; } = "";
        public bool Hidden { get; set; }        // Their public profile shows only that it's private (staff still see everything)
    }

    public bool IsHidden(string steamId)
    {
        lock (_lock)
            return _users.TryGetValue(steamId, out Prefs? prefs) && prefs.Hidden;
    }

    public void SetHidden(string steamId, bool hidden) => Change(steamId, prefs => prefs.Hidden = hidden);

    public string? Discord(string steamId)
    {
        lock (_lock)
            return _users.TryGetValue(steamId, out Prefs? prefs) && prefs.Discord.Length > 0 ? prefs.Discord : null;
    }

    // Discord's usernames: 2 to 32 characters, lowercase letters, digits, _ and . (the old name#1234 too)
    public static bool IsDiscordName(string name) =>
        name.Length is >= 2 and <= 37 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '#');

    public void SetDiscord(string steamId, string name) => Change(steamId, prefs => prefs.Discord = name);

    // One person's preferences changed and saved; someone with nothing set is dropped from the file
    private void Change(string steamId, Action<Prefs> change)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(steamId, out Prefs? prefs))
                _users[steamId] = prefs = new Prefs();
            change(prefs);
            if (prefs.Discord.Length == 0 && !prefs.Hidden)
                _users.Remove(steamId);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_users, Json));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
    }
}
