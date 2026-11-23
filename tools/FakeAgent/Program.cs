// A stand-in for GameModeManager's agent, for trying the panel without a game server. It connects like the real agent,
// sends docs/samples/status.json, and answers commands by changing that status.
//
//   dotnet run --project tools/FakeAgent -- ws://127.0.0.1:5080/agent <token> [server name]
//
// Optional: GMMPANEL_ACCESS_ID and GMMPANEL_ACCESS_SECRET are sent as Cloudflare Access headers, to test through the tunnel.

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: FakeAgent <ws url> <token> [server name]");
    return 1;
}

string url = args[0];
string token = args[1];
string name = args.ElementAtOrDefault(2) ?? "Fake";
// Optional, to look like a different server: mode, map, hostname, max players (the demo in tools/demo.sh)
string? fakeMode = args.ElementAtOrDefault(3), fakeMap = args.ElementAtOrDefault(4), fakeHost = args.ElementAtOrDefault(5);
int fakeMax = int.TryParse(args.ElementAtOrDefault(6), out int max) ? max : 0;
var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

using Stream sample = typeof(Program).Assembly.GetManifestResourceStream("status.json")!;
JsonNode message = JsonNode.Parse(sample)!;
JsonObject status = message["status"]!.AsObject();
if (fakeMode != null) { status["mode"] = new JsonObject { ["name"] = fakeMode, ["displayName"] = fakeMode }; }
if (fakeMap != null) { status["map"] = new JsonObject { ["name"] = fakeMap, ["displayName"] = fakeMap }; }
if (fakeHost != null) { status["server"]!["hostname"] = fakeHost; status["server"]!["address"] = null; }
if (fakeMax > 0) { status["maxPlayers"] = fakeMax; }

// Players come and go, and some chat, so the panel's player history, graphs and chat log fill up
string[] fakeNames = { "Alex", "Sam", "Jordan", "Riley", "Casey", "Morgan", "Taylor", "Jamie", "Avery", "Quinn", "Drew", "Rowan", "Dev Player" };
string[] fakeLines = { "gg", "nice shot", "anyone up for another map?", "rtv", "lag?", "wp all", "who has the bomb", "!rank", "ez", "glhf" };
var rng = new Random();
var roster = new List<(string SteamId, string Name, string Team, int Score)> { ("76561198000000003", "Dev Player", "CT", 12) };
void Churn()
{
    if (roster.Count > 0 && rng.Next(3) == 0)
        roster.RemoveAt(rng.Next(roster.Count));
    if (roster.Count < 10 && rng.Next(2) == 0)
    {
        int i = rng.Next(fakeNames.Length);
        string id = i == fakeNames.Length - 1 ? "76561198000000003" : $"76561198000{1000000 + i * 7919:D7}"[..17];
        if (roster.All(p => p.SteamId != id))
            roster.Add((id, fakeNames[i], rng.Next(2) == 0 ? "CT" : "T", rng.Next(30)));
    }
    status["players"] = roster.Count;
    status["roster"] = new JsonArray(roster.Select(p => (JsonNode)new JsonObject { ["steamId"] = p.SteamId, ["name"] = p.Name, ["team"] = p.Team, ["score"] = p.Score, ["kills"] = p.Score / 2, ["deaths"] = Math.Max(0, p.Score / 3 - 1), ["ping"] = 20 + (int)(ulong.Parse(p.SteamId) % 60), ["country"] = FakeCountry(p.SteamId) }).ToArray());
}
Churn();

using var socket = new ClientWebSocket();
socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
if (Environment.GetEnvironmentVariable("GMMPANEL_ACCESS_ID") is { Length: > 0 } accessId)
{
    socket.Options.SetRequestHeader("CF-Access-Client-Id", accessId);
    socket.Options.SetRequestHeader("CF-Access-Client-Secret", Environment.GetEnvironmentVariable("GMMPANEL_ACCESS_SECRET") ?? "");
}
socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

try
{
    await socket.ConnectAsync(new Uri(url), CancellationToken.None);
}
catch (WebSocketException exception)
{
    Console.Error.WriteLine($"Couldn't connect: {exception.Message}");
    return 1;
}

await Send(new { type = "hello", protocol = 1, server = name, gmmVersion = "2.4.0", cssVersion = "1.0.379" });
string? welcome = await Receive();
Console.WriteLine($"Panel: {welcome}");
if (welcome == null || !welcome.Contains("\"welcome\""))
    return 1;

await Send(message);

// Every 30 s a fresh status, like the real agent's timer
_ = Task.Run(async () =>
{
    while (socket.State == WebSocketState.Open)
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        Churn();
        await Send(message);
    }
});

_ = Task.Run(async () =>
{
    while (socket.State == WebSocketState.Open)
    {
        await Task.Delay(TimeSpan.FromSeconds(12));
        if (roster.Count == 0)
            continue;
        var who = roster[rng.Next(roster.Count)];
        await Send(new { type = "chat", steamId = who.SteamId, name = who.Name, team = who.Team, text = fakeLines[rng.Next(fakeLines.Length)], teamOnly = rng.Next(4) == 0, at = DateTimeOffset.UtcNow });
    }
});

while (true)
{
    string? text = await Receive();
    if (text == null)
        break;
    JsonNode command = JsonNode.Parse(text)!;
    if ((string?)command["type"] != "command")
        continue;

    string id = (string)command["id"]!;
    string what = (string)command["name"]!;
    JsonObject given = command["args"]?.AsObject() ?? new JsonObject();
    string Arg(string key) => (string?)given[key] ?? "";
    Console.WriteLine($"Command from {command["adminName"]}: {what} {given.ToJsonString()}");

    object? followUp = null;
    string? output = null;
    object? data = null;
    (bool ok, string reply, string? happened) = what switch
    {
        "change_mode" => Set("mode", Item("modes", Arg("mode")), $"Changing mode to {Arg("mode")}.", $"Mode changed to {Arg("mode")}"),
        "set_next_mode" => SetNext(Arg("mode"), $"The next mode is {Arg("mode")}."),
        "random_mode" => Set("mode", Item("modes", "Deathmatch"), "Changing to a random mode: Deathmatch.", "Random mode: Deathmatch"),
        "change_map" => Set("map", Item("maps", Arg("map")), $"Changing map to {Arg("map")}.", $"Map changing to {Arg("map")}"),
        "set_next_map" => SetNext(Arg("map"), $"The next map is {Arg("map")}."),
        "random_map" => Set("map", Item("maps", "de_inferno"), "Changing to a random map: Inferno.", "Random map: Inferno"),
        "start_vote" => Vote(Arg("kind")),
        "set_setting" => SetSetting(Arg("setting"), Arg("on") == "true", out followUp),
        "set_value" => SetValue(Arg("setting"), Arg("value"), out followUp),
        "run_action" => (true, $"Ran {ActionLabel(Arg("action"))}.", $"Action {ActionLabel(Arg("action"))} run"),
        "start_warmup" => StartWarmup(Arg("mode")),
        "schedule_warmup" => Warmup(Arg("mode")),
        "end_warmup" => EndWarmup(),
        "pause" => Pause("Pending", "The match pauses at the next freeze time."),
        "cancel_pause" => Pause("Running", "The pending pause is cancelled."),
        "unpause" => Pause("Running", "Match unpaused."),
        "timeout" => (true, $"{Arg("team").ToUpperInvariant()} timeout called.", $"{Arg("team").ToUpperInvariant()} timeout"),
        "replay_round" => (true, "Replaying this round.", "Round replayed"),
        "replay_last" => (true, "Going back to the last round.", "Last round replayed"),
        "restore_round" => (true, $"Going back to round {Arg("round")}.", $"Round {Arg("round")} restored"),
        "freeze" => Toggle(status["rounds"]!, "frozen", Arg("on") == "true", "Players frozen.", "Players unfrozen."),
        "end_round" => (true, "Round ended as a draw.", "Round ended"),
        "restart_match" => (true, "Restarting the match.", "Match restarted"),
        "set_rotation" => SetRotation(Arg("key"), Arg("value"), Arg("save") == "true"),
        "matchzy" => MatchZy(Arg("action")),
        "console" => RunConsole(Arg("line"), out output),
        "set_hostname" => SetServer("hostname", Arg("value"), $"Hostname set to {Arg("value")}. It lasts until the server restarts."),
        "set_teamname" => SetServer(Arg("team") == "ct" ? "teamCt" : "teamT", Arg("value"), $"{Arg("team").ToUpperInvariant()} team name set."),
        "plugin" => Plugin(Arg("action"), Arg("plugin")),
        "query" => Query(given, out data),
        "moderate" => Moderate(given),
        "whois" => Whois(Arg("steamid"), out data),
        _ => (false, "Unknown command.", null)
    };

    await Send(new { type = "result", id, ok, message = reply, followUp, output, data });
    if (happened != null)
        await Send(new { type = "event", name = what, text = $"{happened} by {command["adminName"]}", at = DateTimeOffset.UtcNow });
    await Send(message);
}

Console.WriteLine("Disconnected.");
return 0;

JsonNode? Item(string list, string key) => status[list]!.AsArray().FirstOrDefault(item => (string?)item!["name"] == key)?.DeepClone();

(bool, string, string?) Set(string field, JsonNode? item, string reply, string happened)
{
    if (item == null)
        return (false, $"There's no {field} called that.", null);
    JsonObject value = item.AsObject();
    value.Remove("maps");
    status[field] = value;
    return (true, reply, happened);
}

(bool, string, string?) SetNext(string value, string reply)
{
    status["nextMap"] = value;
    return (true, reply, $"Next set to {value}");
}

// Like GameModeManager: a change that applies after a restart or next round comes back with the question its menus ask
object? FollowUpFor(JsonNode setting)
{
    string applies = (string?)setting["applies"] ?? "now";
    string name = (string?)setting["displayName"] ?? "";
    var rounds = status["rounds"]!;
    int current = (int)rounds["currentRound"]!;
    bool canReplay = rounds["backups"]!.AsArray().Any(round => (int)round! == current);
    return applies switch
    {
        "restart" => new { kind = "restart", settings = name, canReplay = false, canEndRound = false },
        "round" => new { kind = "round", settings = name, canReplay, canEndRound = (bool)rounds["canEndRound"]! },
        _ => null
    };
}

(bool, string, string?) SetSetting(string key, bool on, out object? followUp)
{
    followUp = null;
    JsonNode? setting = status["settings"]!.AsArray().FirstOrDefault(item => (string?)item!["name"] == key);
    if (setting == null)
        return (false, "There's no setting called that.", null);
    setting["state"] = on ? "On" : "Off";
    followUp = FollowUpFor(setting);
    return (true, $"{setting["displayName"]} is {(on ? "on" : "off")}.", $"{setting["displayName"]} turned {(on ? "on" : "off")}");
}

(bool, string, string?) SetValue(string key, string value, out object? followUp)
{
    followUp = null;
    JsonNode? setting = status["valueSettings"]!.AsArray().FirstOrDefault(item => (string?)item!["name"] == key);
    if (setting == null)
        return (false, "There's no setting called that.", null);
    setting["value"] = value;
    followUp = FollowUpFor(setting);
    return (true, $"{setting["displayName"]} is {value}.", $"{setting["displayName"]} set to {value}");
}

(bool, string, string?) Vote(string kind)
{
    status["voteInProgress"] = true;
    status["votes"]!["canStart"] = false;
    status["votes"]!["unavailable"] = "A vote is running.";
    return (true, $"A {kind} vote started.", $"{kind} vote started");
}

(bool, string, string?) StartWarmup(string mode)
{
    status["warmup"]!["running"] = true;
    status["warmup"]!["mode"] = mode;
    return (true, $"Warmup started: {mode}.", $"Warmup started: {mode}");
}

(bool, string, string?) Toggle(JsonNode node, string field, bool on, string onText, string offText)
{
    node[field] = on;
    return (true, on ? onText : offText, on ? onText : offText);
}

(bool, string, string?) SetRotation(string key, string value, bool save)
{
    var rotation = status["rotation"]!;
    rotation[key] = key switch
    {
        "interval" or "emptyMinutes" => JsonValue.Create(int.Parse(value)),
        "modeVote" => JsonValue.Create(value),
        _ => JsonValue.Create(value == "true")
    };
    // Worded as GameModeManager's PanelCommands words it
    bool on = value == "true";
    int.TryParse(value, out int number);
    string said = key switch
    {
        "maps" => $"Map rotation is {(on ? "on" : "off")}",
        "modes" => $"Mode rotation is {(on ? "on" : "off")}",
        "countSkipped" => $"Count skipped maps is {(on ? "on" : "off")}",
        "rtv" => $"RTV / Nominate is {(on ? "on" : "off")}",
        "whenEmpty" => $"Rotate when empty is {(on ? "on" : "off")}",
        "interval" => $"Change mode every {number} map{(number == 1 ? "" : "s")}",
        "emptyMinutes" => $"Empty server wait is {number} minute{(number == 1 ? "" : "s")}",
        "modeVote" => value == "vote" ? "Mode change is by player vote" : "Mode change is random",
        _ => $"{key} is now {value}"
    };
    return (true, save ? $"{said}, and saved in GameModeManager.json." : $"{said}. It lasts until the server restarts.", $"Rotation {key} set to {value}");
}

(bool, string, string?) MatchZy(string action)
{
    var mz = status["matchZy"]!;
    switch (action)
    {
        case "start": mz["live"] = true; mz["readyUp"] = false; break;
        case "end": mz["live"] = false; mz["paused"] = false; mz["readyUp"] = true; break;
        case "pause": mz["paused"] = true; break;
        case "unpause": mz["paused"] = false; break;
        case "practice": mz["practice"] = true; mz["readyUp"] = false; break;
        case "exitpractice": mz["practice"] = false; mz["readyUp"] = true; break;
        case "knife": mz["knifeRound"] = !(bool)mz["knifeRound"]!; break;
        case "playout": mz["playout"] = !(bool)mz["playout"]!; break;
        case "whitelist": mz["whitelist"] = !(bool)mz["whitelist"]!; break;
    }
    return (true, $"MatchZy: {action}.", $"MatchZy {action}");
}

(bool, string, string?) RunConsole(string line, out string? output)
{
    output = line.Trim().ToLowerInvariant() switch
    {
        "status" => "hostname: Example Community | Casual\nversion : 1.40.8.1/14081 secure\nudp/ip  : 0.0.0.0:27015\nmap     : de_dust2\nplayers : 7 humans, 0 bots (16 max)\n# userid name   ping\n#  2 \"Alex\"      24\n#  3 \"Sam\"       31",
        "css_plugins list" => "  [#1:LOADED]: \"GameModeManager\" (2.4.0)\n  [#2:LOADED]: \"MenuManagerCore\" (1.3.01)\n  [#3:LOADED]: \"GG1MapChooser\" (1.9.4)",
        _ => ""
    };
    return (true, "Sent.", null);
}

(bool, string, string?) SetServer(string field, string value, string reply)
{
    status["server"]![field] = value;
    return (true, reply, reply.TrimEnd('.'));
}

(bool, string, string?) Plugin(string action, string name)
{
    JsonNode? plugin = status["plugins"]!.AsArray().FirstOrDefault(p => (string?)p!["name"] == name);
    if (plugin == null)
        return (false, "That plugin isn't on this server.", null);
    plugin["state"] = action == "unload" ? "unloaded" : "loaded";
    return (true, $"css_plugins {action} {name}: done.", $"Plugin {name} {action}ed");
}

// Sample rows for the Moderation page, so its tables, search and paging can be tried without a database
(bool, string, string?) Query(JsonObject a, out object? page)
{
    page = null;
    string source = (string?)a["source"] ?? "", list = (string?)a["list"] ?? "";
    string search = ((string?)a["search"] ?? "").ToLowerInvariant();
    string filter = (string?)a["filter"] ?? "active";
    int number = int.TryParse((string?)a["page"], out int n) ? n : 1;
    var all = SampleRows(source, list, out var columns)
        .Where(row => search.Length == 0 || row.Values.Any(v => v != null && v.ToLowerInvariant().Contains(search)))
        .Where(row => filter != "active" || !row.ContainsKey("status") || row["status"] == "ACTIVE")
        .ToList();
    if (columns.Count == 0)
        return (false, "That list isn't offered.", null);
    const int size = 25;
    page = new { columns, rows = all.Skip((number - 1) * size).Take(size), page = number, pageSize = size, total = all.Count };
    return (true, "", null);
}

List<Dictionary<string, string?>> SampleRows(string source, string list, out List<object> columns)
{
    object Col(string key, string label, string kind = "text") => new { key, label, kind };
    string[] names = { "Alex", "Sam", "Jordan", "Riley", "Casey", "Morgan", "Taylor", "Jamie", "Avery", "Quinn", "Drew", "Rowan" };
    string[] reasons = { "Cheating", "Toxic chat", "Mic spam", "Team killing", "Griefing", "Advertising", "" };
    string Steam(int i) => $"7656119800{(1000000 + i * 7919) % 10000000:D7}";
    string When(int i) => DateTime.UtcNow.AddHours(-i * 9).ToString("yyyy-MM-dd HH:mm");
    columns = new List<object>();
    var rows = new List<Dictionary<string, string?>>();
    switch ($"{source}/{list}")
    {
        case "server/players":
            columns.AddRange(new[] { Col("name", "Player"), Col("steamid", "SteamID", "steamid"), Col("team", "Team"), Col("score", "Score", "number"), Col("kills", "K", "number"), Col("deaths", "D", "number"), Col("ping", "Ping", "number") });
            foreach (var p in roster)
                rows.Add(new() { ["name"] = p.Name, ["steamid"] = p.SteamId, ["team"] = p.Team, ["score"] = p.Score.ToString(), ["kills"] = (p.Score / 2).ToString(), ["deaths"] = (p.Score % 7).ToString(), ["ping"] = (20 + p.Score * 3 % 60).ToString() });
            break;
        case "server/admins":
            columns.AddRange(new[] { Col("name", "Admin"), Col("steamid", "SteamID", "steamid"), Col("groups", "Groups"), Col("flags", "Flags"), Col("immunity", "Immunity", "number") });
            rows.Add(new() { ["name"] = "", ["steamid"] = "76561198000000002", ["groups"] = "#admin", ["flags"] = "@css/ban @css/changemap @css/cvar @css/generic @css/kick @css/rcon", ["immunity"] = "50" });
            rows.Add(new() { ["name"] = "", ["steamid"] = "76561198000000001", ["groups"] = "#moderator", ["flags"] = "@css/chat @css/generic @css/kick", ["immunity"] = "20" });
            break;
        case "server/groups":
            columns.AddRange(new[] { Col("group", "Group"), Col("immunity", "Immunity", "number"), Col("flags", "Flags") });
            rows.Add(new() { ["group"] = "#admin", ["immunity"] = "50", ["flags"] = "@css/ban @css/changemap @css/cvar @css/generic @css/kick @css/rcon" });
            rows.Add(new() { ["group"] = "#moderator", ["immunity"] = "20", ["flags"] = "@css/chat @css/generic @css/kick" });
            break;
        case "simpleadmin/bans":
        case "simpleadmin/warns":
        case "simpleadmin/mutes":
            bool mutes = list == "mutes";
            columns.AddRange(new[] { Col("name", "Player"), Col("steamid", "SteamID", "steamid") });
            if (mutes) columns.Add(Col("type", "Type"));
            columns.AddRange(new[] { Col("reason", "Reason"), Col("admin", "Admin"), Col("created", "Issued", "time"), Col("length", "Length"), Col("status", "Status", "status") });
            for (int i = 0; i < (list == "bans" ? 63 : list == "mutes" ? 31 : 12); i++)
            {
                var row = new Dictionary<string, string?>
                {
                    ["name"] = names[i % names.Length] + (i >= names.Length ? (i / names.Length).ToString() : ""),
                    ["steamid"] = Steam(i),
                    ["reason"] = reasons[i % reasons.Length],
                    ["admin"] = i % 3 == 0 ? "Console" : "Admin",
                    ["created"] = When(i),
                    ["length"] = i % 5 == 0 ? "Permanent" : (i % 4 + 1) == 1 ? "1 hour" : $"{i % 4 + 1} hours",   // As the agent writes them (PluginDatabase.Minutes)
                    ["status"] = i % 4 == 3 ? (mutes ? "UNMUTED" : list == "bans" ? "UNBANNED" : "EXPIRED") : i % 4 == 2 ? "EXPIRED" : "ACTIVE"
                };
                if (mutes) row["type"] = new[] { "MUTE", "GAG", "SILENCE" }[i % 3];
                // Bans: as the agent sends it, from the ban's IP. Mutes and warnings: a live server's players were on when muted, so
                // the panel's history has their country; these made-up players were never on, so the demo sends it
                row["country"] = FakeCountry(row["steamid"]!);
                rows.Add(row);
            }
            // The sample player (...003) has an active ban, so My profile's appeal form can be tried
            if (list == "bans")
                rows.Insert(0, new() { ["name"] = "Dev Player", ["steamid"] = "76561198000000003", ["reason"] = "Toxic chat", ["admin"] = "Admin", ["created"] = When(0), ["length"] = "1 day", ["status"] = "ACTIVE" });
            break;
        case "simpleadmin/admins":
            columns.AddRange(new[] { Col("name", "Admin"), Col("steamid", "SteamID", "steamid"), Col("group", "Group"), Col("flags", "Flags"), Col("immunity", "Immunity", "number"), Col("ends", "Ends", "time") });
            for (int i = 0; i < 6; i++)
                rows.Add(new() { ["name"] = names[(i + 5) % names.Length], ["steamid"] = Steam(i + 300), ["group"] = i == 0 ? "" : i < 3 ? "#admin" : "#moderator",
                    ["flags"] = i == 0 ? "@css/root" : "", ["immunity"] = i == 0 ? "100" : i < 3 ? "50" : "20", ["ends"] = i == 5 ? DateTime.UtcNow.AddDays(14).ToString("yyyy-MM-dd HH:mm") : "Never" });
            break;
        case "simpleadmin/groups":
            columns.AddRange(new[] { Col("group", "Group"), Col("immunity", "Immunity", "number"), Col("flags", "Flags"), Col("members", "Admins", "number") });
            rows.Add(new() { ["group"] = "#admin", ["immunity"] = "50", ["flags"] = "@css/ban @css/changemap @css/generic @css/kick @css/slay", ["members"] = "2" });
            rows.Add(new() { ["group"] = "#moderator", ["immunity"] = "20", ["flags"] = "@css/chat @css/generic @css/kick", ["members"] = "3" });
            break;
    }
    return rows;
}

(bool, string, string?) Moderate(JsonObject a)
{
    string action = (string?)a["action"] ?? "", steam = (string?)a["steamid"] ?? "";
    if ((string?)a["source"] == "server" && action == "kick")
    {
        int gone = roster.RemoveAll(p => p.SteamId == steam);
        Churn();
        return gone > 0 ? (true, "Kicked.", $"Kicked {steam}") : (false, "That player isn't on the server any more.", null);
    }
    string minutes = (string?)a["minutes"] ?? "";
    string length = minutes == "0" ? "permanently" : minutes.Length > 0 ? $"for {minutes} min" : "";
    return (true, $"{action} {steam} {length}: sent to the plugin.".Replace("  ", " "), $"{action} {steam}");
}

(bool, string, string?) Warmup(string mode)
{
    status["warmup"]!["scheduled"] = true;
    status["warmup"]!["mode"] = mode;
    return (true, $"Warmup with {mode} next map.", $"Warmup scheduled: {mode}");
}

(bool, string, string?) EndWarmup()
{
    status["warmup"]!["running"] = false;
    return (true, "Warmup ended.", "Warmup ended");
}

(bool, string, string?) Pause(string state, string reply)
{
    status["rounds"]!["pause"] = state;
    return (true, reply, state == "Running" ? "Match unpaused" : "Pause asked for");
}

async Task Send(object value)
{
    byte[] bytes = value is JsonNode node ? Encoding.UTF8.GetBytes(node.ToJsonString()) : JsonSerializer.SerializeToUtf8Bytes(value, web);
    if (socket.State == WebSocketState.Open)
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
}

async Task<string?> Receive()
{
    var buffer = new byte[64 * 1024];
    using var text = new MemoryStream();
    while (true)
    {
        WebSocketReceiveResult received;
        try { received = await socket.ReceiveAsync(buffer, CancellationToken.None); }
        catch (WebSocketException) { return null; }
        if (received.MessageType == WebSocketMessageType.Close)
            return null;
        text.Write(buffer, 0, received.Count);
        if (received.EndOfMessage)
            return Encoding.UTF8.GetString(text.ToArray());
    }
}

// Sample admins for trying the panel's roles with the Development sign-in (type the SteamID on /signin):
//   ...001 a #moderator, ...002 an #admin without @css/root, ...003 a player, ...004 a #vip; anyone else has no flags
(bool, string, string?) Whois(string steamId, out object? page)
{
    (string Flags, string Groups) admin = steamId switch
    {
        "76561198000000001" => ("@css/ban @css/chat @css/generic @css/kick @css/unban", "#moderator"),
        "76561198000000002" => ("@css/ban @css/changemap @css/chat @css/config @css/cvar @css/generic @css/kick @css/rcon @css/unban", "#admin"),
        "76561198000000004" => ("@css/reservation @css/vip", "#vip"),
        // Staff on one server only (Production): to try that staff don't read other servers' reports, chat or players
        "76561198000000005" when name == "Production" => ("@css/ban @css/chat @css/generic @css/kick @css/unban", "#moderator"),
        _ => ("", "")
    };
    page = new
    {
        columns = new[] { new { key = "flags", label = "Flags", kind = "text" }, new { key = "groups", label = "Groups", kind = "text" } },
        rows = new[] { new Dictionary<string, string?> { ["flags"] = admin.Flags, ["groups"] = admin.Groups, ["immunity"] = "0" } },
        page = 1, pageSize = 1, total = 1
    };
    return (true, "", null);
}

// Each fake player's country, as the agent's DB-IP lookup would send it (a steady pick from their SteamID)
static string FakeCountry(string steamId) => new[] { "US", "US", "US", "US", "DE", "DE", "DE", "GB", "GB", "GB", "BR", "BR", "FR", "SE", "CA", "PL" }[(int)(ulong.Parse(steamId) % 16)];

// An action's name as GameModeManager shows it (its display name, not the cfg file)
string ActionLabel(string? file) => status["actions"]?.AsArray().FirstOrDefault(a => a?["name"]?.GetValue<string>() == file)?["displayName"]?.GetValue<string>() ?? file ?? "";
