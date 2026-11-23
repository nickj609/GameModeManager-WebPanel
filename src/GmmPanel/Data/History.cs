using System.Text.Json;
using GmmPanel.Protocol;

namespace GmmPanel.Data;

// What the panel remembers about the servers, in data/history: player counts (a sample every 5 minutes, for the graphs),
// every player seen (names, first and last seen, play time: from each status's roster) and the chat log (as the agent sends
// it). Plain files, no database: counts-<yyyy-MM>.csv and chat/<yyyy-MM-dd>.jsonl per server, players.json for all.
// How long chat, counts and players are kept is the Panel page's Retention (panel.json); Prune applies it.
public sealed class History : IDisposable
{
    private readonly PanelStore _store;
    public int ChatDays => _store.Current.Retention.ChatDays;
    private static readonly TimeSpan SampleEvery = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly ILogger<History> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, PlayerRecord> _players;
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> _online = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _sampledAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private bool _dirty;

    public History(PanelStore store, ILogger<History> logger)
    {
        _store = store;
        _root = Path.Combine(store.DataDirectory, "history");
        _logger = logger;
        Directory.CreateDirectory(_root);
        _players = Load();
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));
    }

    public sealed class PlayerRecord
    {
        public string SteamId { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> Names { get; set; } = new();         // Up to 10, newest first
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public long Seconds { get; set; }                       // Finished sessions only; add the running one with Online
        public int Sessions { get; set; }
        public List<string> Servers { get; set; } = new();
        public string Week { get; set; } = "";              // The ISO week WeekSeconds counts (2026-W46)
        public long WeekSeconds { get; set; }               // Finished sessions this week
        public string Country { get; set; } = "";           // The last country the agent looked up (ISO alpha-2), for "Player countries"
    }

    public sealed record ChatLine(DateTimeOffset At, string SteamId, string Name, string Team, string Text, bool TeamOnly, string Server = "");

    // ---------- From the agents ----------

    public void OnStatus(string server, StatusSnapshot status)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (!_sampledAt.TryGetValue(server, out DateTimeOffset last) || now - last >= SampleEvery)
            {
                _sampledAt[server] = now;
                Append(Path.Combine(ServerDir(server), $"counts-{now:yyyy-MM}.csv"), $"{now.ToUnixTimeSeconds()},{status.Players},{status.MaxPlayers}\n");
            }
            TrackMap(server, status, now);
            if (status.Roster == null)
                return;

            if (!_online.TryGetValue(server, out var online))
                _online[server] = online = new();
            var here = status.Roster.Where(p => IsSteamId(p.SteamId)).GroupBy(p => p.SteamId).ToDictionary(g => g.Key, g => g.First());
            foreach (var (steamId, entry) in here)
            {
                PlayerRecord record = Record(steamId, now);
                if (!online.ContainsKey(steamId))
                {
                    online[steamId] = now;
                    record.Sessions++;
                }
                record.LastSeen = now;
                Rename(record, entry.Name);
                if (entry.Country is { Length: 2 } country && country.All(char.IsAsciiLetter))
                    record.Country = country.ToUpperInvariant();
                if (!record.Servers.Contains(server, StringComparer.OrdinalIgnoreCase))
                    record.Servers.Add(server);
            }
            foreach (string gone in online.Keys.Where(id => !here.ContainsKey(id)).ToList())
                End(server, gone, now);
            _dirty = true;
        }
    }

    // ---------- Recent maps: each map a server played, how long and its busiest moment (maps.jsonl per server) ----------

    public sealed record MapPlay(string Map, string Label, string Mode, DateTimeOffset Start, DateTimeOffset? End, int Peak);
    private readonly Dictionary<string, MapPlay> _mapNow = new(StringComparer.OrdinalIgnoreCase);
    private const int KeepMaps = 200;

    // Called under _lock: the map changed (the one before is written down), or the current one's peak rose
    private void TrackMap(string server, StatusSnapshot status, DateTimeOffset now)
    {
        string map = status.Map.Name;
        if (string.IsNullOrEmpty(map))
            return;
        if (_mapNow.TryGetValue(server, out MapPlay? current) && current.Map.Equals(map, StringComparison.OrdinalIgnoreCase))
        {
            if (status.Players > current.Peak)
                _mapNow[server] = current with { Peak = status.Players };
            return;
        }
        if (current != null)
            WriteMap(server, current with { End = now });
        _mapNow[server] = new MapPlay(map, status.Map.Label, status.Mode.Label, now, null, status.Players);
    }

    private void WriteMap(string server, MapPlay play)
    {
        string file = Path.Combine(ServerDir(server), "maps.jsonl");
        Append(file, JsonSerializer.Serialize(play, Json) + "\n");
        // Kept short: the newest KeepMaps
        try
        {
            string[] lines = File.ReadAllLines(file);
            if (lines.Length > KeepMaps * 2)
                File.WriteAllLines(file, lines[^KeepMaps..]);
        }
        catch (IOException) { }
    }

    // The newest first: the one playing now (no End), then the last ones played
    public List<MapPlay> RecentMaps(string server, int count)
    {
        var list = new List<MapPlay>();
        lock (_lock)
        {
            if (_mapNow.TryGetValue(server, out MapPlay? now))
                list.Add(now);
            string file = Path.Combine(ServerDir(server), "maps.jsonl");
            if (File.Exists(file))
            {
                try
                {
                    foreach (string line in File.ReadAllLines(file).Reverse().Take(count))
                        if (JsonSerializer.Deserialize<MapPlay>(line, Json) is { } play)
                            list.Add(play);
                }
                catch (Exception error) when (error is IOException or JsonException) { }
            }
        }
        return list.Take(count).ToList();
    }

    // Stats' Maps board: every map in the servers' recent map logs (the newest ~200 plays each, and the ones playing now),
    // with its time played, how many times, its busiest moment and when it last ran
    public sealed record MapTotal(string Map, string Label, long Seconds, int Plays, int Peak, DateTimeOffset Last);

    public List<MapTotal> MapTotals()
    {
        var plays = new List<MapPlay>();
        IEnumerable<string> servers;
        lock (_lock)
            servers = (Directory.Exists(_root) ? Directory.GetDirectories(_root).Select(d => Path.GetFileName(d)) : Array.Empty<string>()).ToList();
        foreach (string server in servers)
            plays.AddRange(RecentMaps(server, KeepMaps * 2));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return plays.GroupBy(p => p.Map, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MapTotal(g.Key, g.First().Label, (long)g.Sum(p => ((p.End ?? now) - p.Start).TotalSeconds), g.Count(), g.Max(p => p.Peak), g.Max(p => p.End ?? now)))
            .ToList();
    }

    public void OnOffline(string server)
    {
        lock (_lock)
        {
            if (_online.TryGetValue(server, out var online))
                foreach (string steamId in online.Keys.ToList())
                    End(server, steamId, DateTimeOffset.UtcNow);
            _dirty = true;
        }
    }

    public void OnChat(string server, ChatMessage message)
    {
        if (!IsSteamId(message.SteamId) || message.Text.Length == 0)
            return;
        var line = new ChatLine(message.At == default ? DateTimeOffset.UtcNow : message.At, message.SteamId, Clip(message.Name, 64), Clip(message.Team, 12), Clip(message.Text, 256), message.TeamOnly);
        lock (_lock)
            Append(Path.Combine(ServerDir(server), "chat", $"{line.At:yyyy-MM-dd}.jsonl"), JsonSerializer.Serialize(line, Json) + "\n");
    }

    // ---------- For the pages ----------

    // Samples since a time, oldest first
    public List<(DateTimeOffset At, int Players, int Max)> Counts(string server, DateTimeOffset since)
    {
        var result = new List<(DateTimeOffset, int, int)>();
        lock (_lock)
        {
            for (DateTimeOffset month = new(since.Year, since.Month, 1, 0, 0, 0, TimeSpan.Zero); month <= DateTimeOffset.UtcNow; month = month.AddMonths(1))
            {
                string file = Path.Combine(ServerDir(server), $"counts-{month:yyyy-MM}.csv");
                if (!File.Exists(file))
                    continue;
                foreach (string line in File.ReadLines(file))
                {
                    string[] parts = line.Split(',');
                    if (parts.Length >= 3 && long.TryParse(parts[0], out long unix) && int.TryParse(parts[1], out int players) && int.TryParse(parts[2], out int max))
                    {
                        DateTimeOffset at = DateTimeOffset.FromUnixTimeSeconds(unix);
                        if (at >= since)
                            result.Add((at, players, max));
                    }
                }
            }
        }
        return result;
    }

    public PlayerRecord? Player(string steamId)
    {
        lock (_lock)
            return _players.TryGetValue(steamId, out PlayerRecord? record) ? Copy(record) : null;
    }

    // When this player's current session started, on any server (null: not on now)
    public (string Server, DateTimeOffset Since)? OnlineNow(string steamId)
    {
        lock (_lock)
        {
            foreach (var (server, online) in _online)
                if (online.TryGetValue(steamId, out DateTimeOffset since))
                    return (server, since);
            return null;
        }
    }

    public Dictionary<string, DateTimeOffset> OnlineOn(string server)
    {
        lock (_lock)
            return _online.TryGetValue(server, out var online) ? new(online) : new();
    }

    public static string WeekOf(DateTimeOffset at) => $"{System.Globalization.ISOWeek.GetYear(at.UtcDateTime)}-W{System.Globalization.ISOWeek.GetWeekOfYear(at.UtcDateTime):D2}";

    private static DateTimeOffset StartOfWeek(DateTimeOffset at)
    {
        DateTime day = at.UtcDateTime.Date;
        int back = ((int)day.DayOfWeek + 6) % 7;   // Monday
        return new DateTimeOffset(day.AddDays(-back), TimeSpan.Zero);
    }

    // Play time this week, the running session included
    public long WeekSeconds(PlayerRecord record)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long done = record.Week == WeekOf(now) ? record.WeekSeconds : 0;
        if (OnlineNow(record.SteamId) is { } on)
            done += (long)(now - (on.Since > StartOfWeek(now) ? on.Since : StartOfWeek(now))).TotalSeconds;
        return done;
    }

    // The week's most played, and the people seen for the first time since a time (newest first)
    public List<(PlayerRecord Player, long Seconds)> TopThisWeek(int count)
    {
        List<PlayerRecord> all;
        lock (_lock)
            all = _players.Values.Where(p => p.LastSeen >= StartOfWeek(DateTimeOffset.UtcNow)).Select(Copy).ToList();
        return all.Select(p => (p, WeekSeconds(p))).Where(x => x.Item2 > 0).OrderByDescending(x => x.Item2).Take(count).ToList();
    }

    public (int Count, List<PlayerRecord> Latest) NewSince(DateTimeOffset since, int count)
    {
        lock (_lock)
        {
            var fresh = _players.Values.Where(p => p.FirstSeen >= since).OrderByDescending(p => p.FirstSeen).ToList();
            return (fresh.Count, fresh.Take(count).Select(Copy).ToList());
        }
    }

    // Player countries: players seen since a time (on one server, or all), counted by country, most first.
    // Countries with fewer than MinPerCountry players fold into "" (Other), so no one stands out on a small server.
    public const int MinPerCountry = 3;
    public (List<(string Country, int Players)> Countries, int Other, int Total) Countries(string? server, DateTimeOffset since)
    {
        List<string> codes;
        lock (_lock)
            codes = _players.Values
                .Where(p => p.LastSeen >= since && p.Country.Length == 2 && (server == null || p.Servers.Contains(server, StringComparer.OrdinalIgnoreCase)))
                .Select(p => p.Country).ToList();
        var counted = codes.GroupBy(c => c).Select(g => (Country: g.Key, Players: g.Count())).OrderByDescending(c => c.Players).ThenBy(c => c.Country).ToList();
        var shown = counted.Where(c => c.Players >= MinPerCountry).ToList();
        return (shown, codes.Count - shown.Sum(c => c.Players), codes.Count);
    }

    // How many different players were seen since a time (the dashboard's "this week")
    public int SeenSince(DateTimeOffset since)
    {
        lock (_lock)
            return _players.Values.Count(p => p.LastSeen >= since);
    }

    // Total play time, the running session included
    public long Seconds(PlayerRecord record) =>
        record.Seconds + (OnlineNow(record.SteamId) is { } now ? (long)(DateTimeOffset.UtcNow - now.Since).TotalSeconds : 0);

    // Everyone seen, newest first, by name (any they've used) or SteamID
    // Everyone seen, for the Stats page's leaderboards (copies; the caller sorts and pages)
    public List<PlayerRecord> All()
    {
        lock (_lock)
            return _players.Values.Select(Copy).ToList();
    }

    // allow: the servers the viewer may see (null: all); a player shows when seen on one of them
    public (List<PlayerRecord> Rows, int Total) Players(string search, int page, int pageSize, Func<string, bool>? allow = null, string sort = "", string onlyServer = "")
    {
        lock (_lock)
        {
            IEnumerable<PlayerRecord> rows = _players.Values;
            if (allow != null)
                rows = rows.Where(p => p.Servers.Any(allow));
            if (search.Length > 0)
                rows = rows.Where(p => p.SteamId == search || p.Names.Any(n => n.Contains(search, StringComparison.OrdinalIgnoreCase)));
            // Moderate's server picker: people seen on that one
            if (onlyServer.Length > 0)
                rows = rows.Where(p => p.Servers.Contains(onlyServer, StringComparer.OrdinalIgnoreCase));
            // Last seen first, unless a header asked for another order ("-key" = descending; Player history's headers)
            bool desc = sort.StartsWith('-');
            var list = sort.TrimStart('-') switch
            {
                "name" => (desc ? rows.OrderByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)).ToList(),
                "first" => (desc ? rows.OrderByDescending(p => p.FirstSeen) : rows.OrderBy(p => p.FirstSeen)).ToList(),
                "played" => (desc ? rows.OrderByDescending(p => Seconds(p)) : rows.OrderBy(p => Seconds(p))).ToList(),
                "last" when !desc => rows.OrderBy(p => p.LastSeen).ToList(),
                _ => rows.OrderByDescending(p => p.LastSeen).ToList()
            };
            return (list.Skip((page - 1) * pageSize).Take(pageSize).Select(Copy).ToList(), list.Count);
        }
    }

    // The chat log, newest first: one server or all, a SteamID or a text search. Read a day at a time from the newest
    // (one file per server per day): once the page is full, the older days are only counted, by their lines (kept per
    // file until it changes), not read line by line, so a page stays quick however much chat the servers keep. A search
    // or a SteamID still reads every day, as only the lines can say what matches.
    private readonly Dictionary<string, (long Length, DateTime Written, int Lines)> _chatCounts = new();

    public (List<ChatLine> Rows, int Total) Chat(string? server, string? steamId, string search, int page, int pageSize, Func<string, bool>? allow = null, DateTimeOffset? since = null)
    {
        var found = new List<ChatLine>();
        int total = 0, needed = page * pageSize;
        bool everyLine = steamId != null || search.Length > 0;
        lock (_lock)
        {
            IEnumerable<string> servers = server != null ? new[] { server } : Directory.Exists(_root) ? Directory.GetDirectories(_root).Select(d => Path.GetFileName(d)) : Array.Empty<string>();
            if (allow != null)
                servers = servers.Where(allow);
            // Every server's day files, grouped by day, newest day first (the file's name is its UTC date)
            var days = servers.SelectMany(name => Directory.Exists(Path.Combine(ServerDir(name), "chat"))
                    ? Directory.GetFiles(Path.Combine(ServerDir(name), "chat"), "*.jsonl").Select(file => (Server: name, File: file, Day: Path.GetFileNameWithoutExtension(file)))
                    : Enumerable.Empty<(string Server, string File, string Day)>())
                .GroupBy(f => f.Day).OrderByDescending(g => g.Key, StringComparer.Ordinal);
            string? firstDay = since?.UtcDateTime.ToString("yyyy-MM-dd");
            foreach (var day in days)
            {
                // A time range skips the days before it; its first day is read line by line (part of it is in range)
                if (firstDay != null && string.CompareOrdinal(day.Key, firstDay) < 0)
                    break;
                bool whole = firstDay == null || string.CompareOrdinal(day.Key, firstDay) > 0;
                if (!everyLine && whole && found.Count >= needed)
                {
                    total += day.Sum(f => LineCount(f.File));
                    continue;
                }
                // A day is kept whole when the page still needs lines: its file runs oldest first, so stopping part way would
                // keep the day's oldest lines, not its newest
                bool keep = everyLine || found.Count < needed;
                foreach (var (name, file, _) in day)
                    foreach (string text in File.ReadLines(file))
                    {
                        ChatLine? line;
                        try { line = JsonSerializer.Deserialize<ChatLine>(text, Json); }
                        catch (JsonException) { continue; }
                        if (line == null || (steamId != null && line.SteamId != steamId) || (since != null && line.At < since))
                            continue;
                        if (search.Length > 0 && !line.Text.Contains(search, StringComparison.OrdinalIgnoreCase) && !line.Name.Contains(search, StringComparison.OrdinalIgnoreCase) && line.SteamId != search)
                            continue;
                        total++;
                        if (keep)
                            found.Add(line with { Server = name });
                    }
            }
        }
        found = found.OrderByDescending(l => l.At).ToList();
        return (found.Skip((page - 1) * pageSize).Take(pageSize).ToList(), total);
    }

    // A chat file's lines, counted once until it changes (today's file grows; older days don't). Called under _lock.
    private int LineCount(string file)
    {
        var info = new FileInfo(file);
        if (_chatCounts.TryGetValue(file, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc)
            return known.Lines;
        int lines = 0;
        foreach (string text in File.ReadLines(file))
            if (text.Length > 0)
                lines++;
        _chatCounts[file] = (info.Length, info.LastWriteTimeUtc, lines);
        return lines;
    }

    // ---------- Inside ----------

    private PlayerRecord Record(string steamId, DateTimeOffset now)
    {
        if (!_players.TryGetValue(steamId, out PlayerRecord? record))
            _players[steamId] = record = new PlayerRecord { SteamId = steamId, FirstSeen = now, LastSeen = now };
        return record;
    }

    private static void Rename(PlayerRecord record, string name)
    {
        name = Clip(name, 64);
        if (name.Length == 0)
            return;
        record.Name = name;
        record.Names.Remove(name);
        record.Names.Insert(0, name);
        if (record.Names.Count > 10)
            record.Names.RemoveRange(10, record.Names.Count - 10);
    }

    private void End(string server, string steamId, DateTimeOffset now)
    {
        if (!_online.TryGetValue(server, out var online) || !online.Remove(steamId, out DateTimeOffset since))
            return;
        if (_players.TryGetValue(steamId, out PlayerRecord? record))
        {
            record.Seconds += (long)Math.Max(0, (now - since).TotalSeconds);
            string week = WeekOf(now);
            if (record.Week != week)
            {
                record.Week = week;
                record.WeekSeconds = 0;
            }
            record.WeekSeconds += (long)Math.Max(0, (now - (since > StartOfWeek(now) ? since : StartOfWeek(now))).TotalSeconds);
            record.LastSeen = now;
        }
    }

    private void Tick()
    {
        try
        {
            lock (_lock)
            {
                if (_dirty)
                {
                    string path = Path.Combine(_root, "players.json");
                    File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_players.Values, Json));
                    File.Move(path + ".tmp", path, overwrite: true);
                    _dirty = false;
                }
            }
            Prune();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("History couldn't be saved: {Error}", error.Message);
        }
    }

    // What the Retention settings say has had its time: chat and count files by date, players by when they were last seen.
    // 0 keeps it all.
    private void Prune()
    {
        Retention keep = _store.Current.Retention;
        DateTime chatCut = keep.ChatDays > 0 ? DateTime.UtcNow.AddDays(-keep.ChatDays) : DateTime.MinValue;
        DateTime countCut = keep.CountDays > 0 ? DateTime.UtcNow.AddDays(-keep.CountDays) : DateTime.MinValue;
        lock (_lock)
        {
            if (keep.PlayerDays > 0)
            {
                DateTimeOffset playerCut = DateTimeOffset.UtcNow.AddDays(-keep.PlayerDays);
                var gone = _players.Values.Where(p => p.LastSeen < playerCut).Select(p => p.SteamId).ToList();
                foreach (string id in gone)
                    _players.Remove(id);
                if (gone.Count > 0)
                    _dirty = true;
            }
            foreach (string dir in Directory.GetDirectories(_root))
            {
                string chat = Path.Combine(dir, "chat");
                if (Directory.Exists(chat))
                    foreach (string file in Directory.GetFiles(chat, "*.jsonl"))
                        if (DateTime.TryParse(Path.GetFileNameWithoutExtension(file), out DateTime day) && day < chatCut.Date)
                            File.Delete(file);
                foreach (string file in Directory.GetFiles(dir, "counts-*.csv"))
                    if (DateTime.TryParse(Path.GetFileNameWithoutExtension(file)["counts-".Length..] + "-01", out DateTime month) && month.AddMonths(1) < countCut)
                        File.Delete(file);
            }
        }
    }

    private Dictionary<string, PlayerRecord> Load()
    {
        string path = Path.Combine(_root, "players.json");
        try
        {
            if (File.Exists(path))
                return (JsonSerializer.Deserialize<List<PlayerRecord>>(File.ReadAllText(path), Json) ?? new()).Where(p => IsSteamId(p.SteamId)).ToDictionary(p => p.SteamId);
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            _logger.LogWarning("history/players.json couldn't be read ({Error}); starting a new one", error.Message);
        }
        return new();
    }

    private string ServerDir(string server)
    {
        string dir = Path.Combine(_root, PanelStore.IsServerName(server) ? server : "_");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void Append(string file, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, text);
        }
        catch (IOException error)
        {
            _logger.LogWarning("History couldn't write {File}: {Error}", Path.GetFileName(file), error.Message);
        }
    }

    private static PlayerRecord Copy(PlayerRecord r) => new()
    {
        SteamId = r.SteamId, Name = r.Name, Names = r.Names.ToList(), FirstSeen = r.FirstSeen, LastSeen = r.LastSeen,
        Seconds = r.Seconds, Sessions = r.Sessions, Servers = r.Servers.ToList(), Week = r.Week, WeekSeconds = r.WeekSeconds, Country = r.Country
    };

    private static string Clip(string? text, int max)
    {
        text = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length > max ? text[..max] : text;
    }

    public static bool IsSteamId(string value) =>
        // 17 digits, from the first real account (76561197960265728) up
        value.Length == 17 && value.StartsWith("7656119", StringComparison.Ordinal) && value.All(char.IsAsciiDigit)
        && string.CompareOrdinal(value, "76561197960265728") >= 0;

    public void Dispose()
    {
        _timer.Dispose();
        Tick();
    }
}
