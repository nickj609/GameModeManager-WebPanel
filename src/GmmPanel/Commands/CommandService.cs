using System.Threading.RateLimiting;
using GmmPanel.Agents;
using GmmPanel.Auth;
using GmmPanel.Data;
using GmmPanel.Protocol;

namespace GmmPanel.Commands;

// Everything GameModeManager's in-game admin menus can do, as commands. The panel checks each one against the server's
// last status before sending it, and GameModeManager checks it again: neither side trusts the other's input.
// docs/wiki/Agent-Protocol.md lists them with their arguments.
public static class CommandCatalog
{
    private sealed record Command(Func<StatusSnapshot, Dictionary<string, string>, string?> Check);

    // Rotation page rows the panel may change, and how each value is checked
    private static readonly Dictionary<string, Func<RotationInfo, string, bool>> RotationKeys = new()
    {
        ["maps"] = (r, v) => v is "true" or "false",
        ["modes"] = (r, v) => v is "true" or "false",
        ["interval"] = (r, v) => int.TryParse(v, out int n) && r.IntervalChoices.Contains(n),
        ["modeVote"] = (r, v) => v is "random" or "vote",
        ["countSkipped"] = (r, v) => v is "true" or "false",
        ["rtv"] = (r, v) => v is "true" or "false",
        ["whenEmpty"] = (r, v) => v is "true" or "false",
        ["emptyMinutes"] = (r, v) => int.TryParse(v, out int n) && r.EmptyChoices.Contains(n)
    };

    // MatchZy's commands that work from the server console. Player-bound ones (ready, coach, bots, smokes, spawns) aren't
    // here: they act on the player who types them.
    private static readonly HashSet<string> MatchZyActions = new()
    {
        "start", "restart", "end", "skipveto", "knife", "playout", "whitelist", "practice", "exitpractice", "pause", "unpause"
    };

    // Console lines the panel won't send: they would show or change the passwords that guard the server
    private static readonly string[] ConsoleBlocked = { "rcon_password", "sv_password", "rcon" };

    private static readonly Dictionary<string, Command> Commands = new()
    {
        // Modes and maps
        ["change_mode"] = new((s, a) => Has(s.Modes, a, "mode")),
        ["set_next_mode"] = new((s, a) => Has(s.Modes, a, "mode")),
        ["random_mode"] = new((s, a) => s.Modes.Count > 1 ? null : "This server has only one mode."),
        ["change_map"] = new((s, a) => Has(s.Maps, a, "map")),
        ["set_next_map"] = new((s, a) => Has(s.Maps, a, "map")),
        ["random_map"] = new((s, a) => s.Modes.FirstOrDefault(m => m.Name == s.Mode.Name)?.Maps.Count > 1 ? null : "This mode only has one map."),
        // As the in-game Start vote pages: GG1MapChooser's random map vote, or yes/no on one map or mode
        ["start_vote"] = new((s, a) => !s.Votes.CanStart ? s.Votes.Unavailable ?? "A vote can't start now."
            : a.GetValueOrDefault("kind") switch
            {
                "random" => null,
                "map" => Has(s.Maps, a, "map") ?? (a["map"] == s.Map.Name ? "That's the current map." : null),
                "mode" => Has(s.Modes, a, "mode") ?? (a["mode"] == s.Mode.Name ? "That's the current mode." : null),
                _ => "Choose a random map vote, or a map or mode to vote on."
            }),

        // Settings
        ["set_setting"] = new((s, a) =>
        {
            SettingInfo? setting = s.Settings.FirstOrDefault(x => x.Name == a.GetValueOrDefault("setting"));
            if (setting == null)
                return "That setting isn't on this server.";
            if (a.GetValueOrDefault("on") is not ("true" or "false"))
                return "Choose on or off.";
            return setting.Cheats && a["on"] == "true" && a.GetValueOrDefault("confirm") != "true" ? "Turning this on needs a yes to sv_cheats first." : null;
        }),
        ["set_value"] = new((s, a) =>
        {
            ValueSettingInfo? setting = s.ValueSettings.FirstOrDefault(v => v.Name == a.GetValueOrDefault("setting"));
            if (setting == null)
                return "That setting isn't on this server.";
            string value = a.GetValueOrDefault("value") ?? "";
            if (!setting.Choices.Contains(value))
                return "Choose one of the setting's values.";
            return setting.CheatChoices.Contains(value) && a.GetValueOrDefault("confirm") != "true" ? "That value needs a yes to sv_cheats first." : null;
        }),

        // Actions
        ["run_action"] = new((s, a) => Has(s.Actions, a, "action")),

        // Warmup
        ["start_warmup"] = new((s, a) => WarmupProblem(s) ?? (s.Warmup.Running ? "A warmup is already running." : WarmupMode(s, a))),
        ["schedule_warmup"] = new((s, a) => WarmupProblem(s) ?? WarmupMode(s, a)),
        ["end_warmup"] = new((s, a) => s.Warmup.Unavailable ?? (s.Warmup.Running ? null : "No warmup is running.")),

        // Rounds, as the in-game Rounds page
        ["pause"] = new((s, a) => s.Rounds.Pause != "Running" ? "The match is already paused or about to be."
            : s.Rounds.PauseUnavailable ? "The match can't be paused in warmup or during a timeout." : null),
        ["cancel_pause"] = new((s, a) => s.Rounds.Pause == "Pending" ? null : "No pause is waiting."),
        ["unpause"] = new((s, a) => s.Rounds.Pause == "Paused" ? null : "The match isn't paused."),
        ["timeout"] = new((s, a) => a.GetValueOrDefault("team") is not ("ct" or "t") ? "Choose CT or T."
            : s.Rounds.TimeoutsByMatchZy ? "During a live MatchZy match, timeouts are the players' .tac."
            : s.Rounds.CanTimeout ? null : "A timeout can't be called now."),
        ["replay_round"] = new((s, a) => s.Rounds.Backups.Count >= 1 ? null : "This round has no backup yet."),
        ["replay_last"] = new((s, a) => s.Rounds.Backups.Count >= 2 ? null : "The last round has no backup."),
        ["restore_round"] = new((s, a) => int.TryParse(a.GetValueOrDefault("round"), out int round) && s.Rounds.Backups.Contains(round) ? null : "There's no backup for that round."),
        ["freeze"] = new((s, a) => a.GetValueOrDefault("on") is "true" or "false" ? null : "Choose on or off."),

        // The answers to a setting's follow-up question: they name the changed settings in chat, as in game
        ["end_round"] = new((s, a) => s.Rounds.CanEndRound ? null : "Ending this round would end the match."),
        ["restart_match"] = new((s, a) => null),

        // Rotation
        ["set_rotation"] = new((s, a) => RotationKeys.TryGetValue(a.GetValueOrDefault("key") ?? "", out var valid) && valid(s.Rotation, a.GetValueOrDefault("value") ?? "")
            ? null : "That isn't a rotation setting or value."),

        // MatchZy
        ["matchzy"] = new((s, a) => s.MatchZy == null ? "MatchZy isn't loaded on this server."
            : MatchZyActions.Contains(a.GetValueOrDefault("action") ?? "") ? null : "That isn't a MatchZy command the panel can run."),

        // Console: the owner only, and never the passwords
        ["console"] = new((s, a) =>
        {
            string line = (a.GetValueOrDefault("line") ?? "").Trim();
            if (line.Length == 0)
                return "Type a command.";
            if (line.Length > 512 || line.Contains('\n') || line.Contains('\r'))
                return "One command, up to 512 characters.";
            string first = line.Split(' ', ';')[0].ToLowerInvariant();
            if (ConsoleBlocked.Contains(first) || line.Contains("rcon_password", StringComparison.OrdinalIgnoreCase))
                return "The panel doesn't send commands that show or change the server's passwords.";
            return s.Console.Available ? null : s.Console.Reason ?? "The console isn't available on this server.";
        }),

        // The Admin page's server settings: live until the server restarts (its cfgs set them again)
        ["set_hostname"] = new((s, a) => s.Server == null ? Outdated
            : TextProblem(a.GetValueOrDefault("value"), 64, "The hostname", allowEmpty: false)),
        ["set_teamname"] = new((s, a) => s.Server == null ? Outdated
            : a.GetValueOrDefault("team") is not ("ct" or "t") ? "Choose CT or T."
            : TextProblem(a.GetValueOrDefault("value"), 32, "A team name", allowEmpty: true)),

        // CounterStrikeSharp's plugins, as css_plugins load / unload / reload: the owner only
        ["plugin"] = new((s, a) =>
        {
            if (s.Plugins == null)
                return Outdated;
            PluginInfo? plugin = s.Plugins.FirstOrDefault(p => p.Name == a.GetValueOrDefault("plugin"));
            if (plugin == null)
                return "That plugin isn't on this server.";
            if (plugin.Protected)
                return "GameModeManager can't unload or reload itself from the panel: the link would drop. Restart the server instead.";
            return a.GetValueOrDefault("action") switch
            {
                "load" => plugin.State == "loaded" ? "It's already loaded." : null,
                "unload" or "reload" => plugin.State == "loaded" ? null : "It isn't loaded.",
                _ => "Choose load, unload or reload."
            };
        }),

        // A page of one plugin's allow-listed data (read only)
        ["query"] = new((s, a) =>
        {
            if (FindList(s, a) is not (_, DataList list))
                return ListProblem(s, a);
            if (!int.TryParse(a.GetValueOrDefault("page") ?? "1", out int page) || page is < 1 or > 10000)
                return "That page doesn't exist.";
            string search = a.GetValueOrDefault("search") ?? "";
            if (search.Length > 0 && (!list.Search || TextProblem(search, 64, "The search", false) != null))
                return "Search by a name or SteamID64, up to 64 characters, without quotes or semicolons.";
            string filter = a.GetValueOrDefault("filter") ?? "";
            return filter.Length == 0 || list.Filters.Contains(filter) ? null : "That filter isn't offered.";
        }),

        // A write through the plugin's own console command (css_addban, css_unban ...), so its caches stay right
        ["moderate"] = new((s, a) =>
        {
            if (FindList(s, a) is not (_, DataList list))
                return ListProblem(s, a);
            DataAction? action = list.Actions.FirstOrDefault(x => x.Id == a.GetValueOrDefault("action"));
            if (action == null)
                return "That action isn't offered for this list.";
            foreach (string field in action.Fields)
            {
                string value = a.GetValueOrDefault(field) ?? "";
                string? problem = field switch
                {
                    "steamid" => IsSteamId(value) ? null : "Enter a SteamID64 (17 digits, starting 7656119).",
                    "minutes" => int.TryParse(value, out int minutes) && minutes is >= 0 and <= 5256000 ? null : "Enter the length in minutes (0 is permanent).",
                    "reason" => TextProblem(value, 128, "The reason", allowEmpty: true),
                    "group" => TextProblem(value, 32, "The group", allowEmpty: false),
                    "amount" => int.TryParse(value, out int amount) && amount is > -1000000 and < 1000000 && amount != 0 ? null : "Enter an amount other than 0.",
                    "name" => TextProblem(value, 64, "The name", allowEmpty: false),
                    "flags" => value.Trim().Length is > 0 and <= 256 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '@' or '#' or '/' or '_' or ',' or ' ' or '-')
                        ? null : "Flags look like @css/ban @css/kick, or a #group; letters, digits, @ # / _ and spaces only.",
                    "immunity" => int.TryParse(value, out int immunity) && immunity is >= 0 and <= 100000 ? null : "Enter an immunity from 0 to 100000.",
                    _ => null
                };
                if (problem != null)
                    return problem;
            }
            return null;
        }),
    };

    private const string Outdated = "This server's GameModeManager is older than the panel: update it for this.";


    private static (DataSource, DataList)? FindList(StatusSnapshot s, Dictionary<string, string> a)
    {
        DataSource? source = s.Data?.FirstOrDefault(d => d.Id == a.GetValueOrDefault("source") && d.Available);
        DataList? list = source?.Lists.FirstOrDefault(l => l.Id == a.GetValueOrDefault("list"));
        return source != null && list != null ? (source, list) : null;
    }

    private static string ListProblem(StatusSnapshot s, Dictionary<string, string> a)
    {
        if (s.Data == null)
            return Outdated;
        DataSource? source = s.Data.FirstOrDefault(d => d.Id == a.GetValueOrDefault("source"));
        return source == null ? "That plugin isn't on this server."
            : !source.Available ? source.Reason ?? $"{source.Name}'s data can't be read."
            : "That list isn't offered.";
    }

    public static bool IsSteamId(string value) =>
        // 17 digits, from the first real account (76561197960265728) up
        value.Length == 17 && value.StartsWith("7656119", StringComparison.Ordinal) && value.All(char.IsAsciiDigit)
        && string.CompareOrdinal(value, "76561197960265728") >= 0;

    // Text that goes into a console command inside quotes: no quotes, semicolons or control characters to break out of it
    public static string? TextProblem(string? value, int max, string what, bool allowEmpty)
    {
        value ??= "";
        if (value.Trim().Length == 0)
            return allowEmpty ? null : $"{what} can't be empty.";
        if (value.Length > max)
            return $"{what} can be up to {max} characters.";
        if (value.Any(c => c is '"' or ';' or '\\' || char.IsControl(c)))
            return $"{what} can't have quotes, semicolons, backslashes or line breaks.";
        return null;
    }

    public static bool Exists(string name) => Commands.ContainsKey(name);


    public static string? Check(string name, StatusSnapshot status, Dictionary<string, string> args) =>
        Commands.TryGetValue(name, out var command) ? command.Check(status, args) : "Unknown command.";

    private static string? WarmupProblem(StatusSnapshot s) =>
        !s.Warmup.Enabled ? "Warmup is turned off in GameModeManager's config." : s.Warmup.Unavailable;

    private static string? WarmupMode(StatusSnapshot s, Dictionary<string, string> a) =>
        s.Warmup.Modes.Contains(a.GetValueOrDefault("mode") ?? "") ? null : "That warmup mode isn't on this server.";

    private static string? Has<T>(List<T> items, Dictionary<string, string> args, string key) where T : NamedItem =>
        args.TryGetValue(key, out var name) && items.Any(item => item.Name == name) ? null : $"That {key} isn't on this server.";
}

// Which admin flag each command needs: the one GameModeManager's in-game page (or the plugin's own command) asks for, so the
// panel gives nobody more than the game does
public static class CommandFlags
{
    public static string For(string name, StatusSnapshot s, Dictionary<string, string> a) => name switch
    {
        "change_mode" or "set_next_mode" or "random_mode" or "change_map" or "set_next_map" or "random_map" or "start_vote" => PanelPermissions.Need("maps"),
        "set_setting" => s.Settings.FirstOrDefault(x => x.Name == a.GetValueOrDefault("setting"))?.Flag ?? Flags.Cvar,
        "set_value" => s.ValueSettings.FirstOrDefault(x => x.Name == a.GetValueOrDefault("setting"))?.Flag ?? Flags.Cvar,
        "run_action" => s.Actions.FirstOrDefault(x => x.Name == a.GetValueOrDefault("action"))?.Flag ?? Flags.Cvar,
        "start_warmup" or "schedule_warmup" or "end_warmup" => PanelPermissions.Need("warmup"),
        // Saving it into GameModeManager.json changes the server's config file: @css/config, as a cfg change would
        "set_rotation" => a.GetValueOrDefault("save") == "true" ? "@css/config" : PanelPermissions.Need("rotation"),
        "set_hostname" or "set_teamname" => PanelPermissions.Need("server"),
        // A setting's follow-up (End round, Restart match) comes with the setting's own flag; the Rounds page has its own
        "end_round" or "restart_match" => a.ContainsKey("settings") ? Flags.Cvar : s.Rounds.Flag,
        "pause" or "cancel_pause" or "unpause" or "timeout" or "replay_round" or "replay_last" or "restore_round" or "freeze" => s.Rounds.Flag,
        "matchzy" => PanelPermissions.Need("matchzy"),
        "console" => PanelPermissions.Need("console"),
        "plugin" => PanelPermissions.Need("plugins"),
        // This server's own lists (live players, CounterStrikeSharp's admins): the panel's areas, so they can be changed
        "query" when a.GetValueOrDefault("source") == "server" => a.GetValueOrDefault("list") == "players" ? PanelPermissions.Need("players") : PanelPermissions.Need("serveradmins"),
        "moderate" when a.GetValueOrDefault("source") == "server" => PanelPermissions.Need("kick"),
        "query" => List(s, a)?.Flag ?? Flags.Root,
        "moderate" => List(s, a)?.Actions.FirstOrDefault(x => x.Id == a.GetValueOrDefault("action"))?.Flag ?? Flags.Root,
        _ => Flags.Root
    };

    private static DataList? List(StatusSnapshot s, Dictionary<string, string> a) =>
        s.Data?.FirstOrDefault(d => d.Id == a.GetValueOrDefault("source"))?.Lists.FirstOrDefault(l => l.Id == a.GetValueOrDefault("list"));
}

public sealed class CommandService
{
    private readonly AgentRegistry _registry;
    private readonly AuditLog _audit;
    private readonly AccessService _access;
    private readonly Discord _discord;
    private readonly Notices _notices;
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(user =>
        RateLimitPartition.GetFixedWindowLimiter(user, _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    public CommandService(AgentRegistry registry, AuditLog audit, AccessService access, Discord discord, Notices notices)
    {
        _discord = discord;
        _notices = notices;
        _registry = registry;
        _audit = audit;
        _access = access;
    }

    public async Task<CommandResult> RunAsync(PanelUser user, string server, string name, Dictionary<string, string> args)
    {
        string what = $"{name} {string.Join(" ", args.Where(arg => arg.Key != "confirm").Select(arg => $"{arg.Key}={arg.Value}"))}".Trim();
        CommandResult result = await TryRunAsync(user, server, name, args);
        // Reading a list isn't logged (every page and search would be); a refused read is, as a refused command is
        if (name != "query" || !result.Ok)
            _audit.Write(user.Audit, what, server, result.Ok, result.Message);
        if (name != "query" && result.Ok)
        {
            _discord.Action(user.Name, server, name, args);
            // A kick through the panel: the player hears about it in their bell (a kick in game leaves no record to read)
            if (name == "moderate" && args.GetValueOrDefault("action") == "kick" && args.GetValueOrDefault("steamid") is { } kicked)
                _notices.Add(kicked, "kick", "You were kicked from {0}.", server, "/profile");
            // Someone's admin rights changed: the panel asks their server again on their next click
            if (name == "moderate" && args.GetValueOrDefault("list") is "admins" && args.GetValueOrDefault("steamid") is { } changed)
                _access.Forget(changed);
        }
        return result;
    }

    private async Task<CommandResult> TryRunAsync(PanelUser user, string server, string name, Dictionary<string, string> args)
    {
        if (!user.CanUse(server))
            return new CommandResult(false, "You can't use this server.");
        if (!CommandCatalog.Exists(name))
            return new CommandResult(false, "Unknown command.");

        using RateLimitLease lease = _limiter.AttemptAcquire(user.SteamId);
        if (!lease.IsAcquired)
            return new CommandResult(false, "Too many commands in a minute. Wait a moment.");

        ServerView? view = _registry.Find(server);
        AgentConnection? connection = view?.Connection;
        if (view?.Status == null || connection == null)
            return new CommandResult(false, "The server isn't connected.");

        // The same flag the game would ask for, from this server's own admin list (panel admins have them all)
        string flag = CommandFlags.For(name, view.Status, args);
        Access access = await _access.ForAsync(user, server);
        if (!access.Has(flag))
            return new CommandResult(false, $"You need the {flag} admin flag on {server} for that.");

        string? refused = CommandCatalog.Check(name, view.Status, args);
        if (refused != null)
            return new CommandResult(false, refused);

        return await connection.RunAsync(name, args, user.SteamId, user.Name);
    }
}
