using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmmPanel.Protocol;

// The agent protocol, version 1. docs/wiki/Agent-Protocol.md is the spec; docs/samples has a message of each kind.
// Every frame is a JSON text message with a "type". Names are camelCase.
public static class AgentProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

// agent -> panel, first message
public sealed class HelloMessage
{
    public string Type { get; set; } = "hello";
    public int Protocol { get; set; }
    public string Server { get; set; } = "";
    public string GmmVersion { get; set; } = "";
    public string CssVersion { get; set; } = "";
}

// panel -> agent, the answer to hello
public sealed class WelcomeMessage
{
    public string Type { get; set; } = "welcome";
    public int Protocol { get; set; } = AgentProtocol.Version;
    public string Server { get; set; } = "";
}

// panel -> agent, before closing
public sealed class ErrorMessage
{
    public string Type { get; set; } = "error";
    public string Message { get; set; } = "";
}

// agent -> panel
public sealed class StatusMessage
{
    public string Type { get; set; } = "status";
    public StatusSnapshot Status { get; set; } = new();
}

// agent -> panel, one of GameModeManager's events, as text for people
public sealed class EventMessage
{
    public string Type { get; set; } = "event";
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

public sealed class RosterEntry
{
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Team { get; set; } = "";            // CT, T or Spectator
    public int Score { get; set; }                    // The scoreboard (older agents send none: 0)
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int? Assists { get; set; }                 // Null from agents that don't send them yet
    public int Ping { get; set; }
    public string? Country { get; set; }              // ISO 3166-1 alpha-2 from the player's IP, looked up by the agent (never the IP itself); null when unknown or off
}

// agent -> panel, a chat line as it happens (the panel keeps the chat log)
public sealed class ChatMessage
{
    public string Type { get; set; } = "chat";
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Team { get; set; } = "";
    public string Text { get; set; } = "";
    public bool TeamOnly { get; set; }
    public DateTimeOffset At { get; set; }
}

// panel -> agent
public sealed class CommandMessage
{
    public string Type { get; set; } = "command";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, string> Args { get; set; } = new();
    public string AdminSteamId { get; set; } = "";
    public string AdminName { get; set; } = "";
}

// agent -> panel, the answer to a command
public sealed class ResultMessage
{
    public string Type { get; set; } = "result";
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public string Message { get; set; } = "";

    // After a setting change, the question GameModeManager's menus ask, so the panel asks it too: "restart" (the change
    // needs a match restart) or "round" (it applies next round). Null when nothing needs asking.
    public FollowUp? FollowUp { get; set; }

    // The console's reply (local RCON's output), only for "console"
    public string? Output { get; set; }

    // One page of a plugin's data, only for "query"
    public DataPage? Data { get; set; }
}

// A page of rows from one of a plugin's allow-listed lists (bans, mutes, VIPs ...). Values are text, ready to show.
public sealed class DataPage
{
    public List<DataColumn> Columns { get; set; } = new();
    public List<Dictionary<string, string?>> Rows { get; set; } = new();
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public int Total { get; set; }
}

public sealed class DataColumn
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "text";        // text, steamid, time, status (an .ind word), number
}

public sealed class FollowUp
{
    public string Kind { get; set; } = "";            // restart or round
    public string Settings { get; set; } = "";        // The changed settings' names, for the question
    public bool CanReplay { get; set; }               // round: this round has a backup ("Replay round")
    public bool CanEndRound { get; set; }             // round: ending it wouldn't end the match ("End round")
}

// What GameModeManager reports: built on the game thread, sent whole
public sealed class StatusSnapshot
{
    public string GmmVersion { get; set; } = "";
    public string CssVersion { get; set; } = "";
    public int Players { get; set; }
    public int MaxPlayers { get; set; }
    public NamedItem Mode { get; set; } = new();
    public NamedItem Map { get; set; } = new();
    public string? NextMap { get; set; }
    public bool VoteInProgress { get; set; }
    public VotesInfo Votes { get; set; } = new();
    public List<ModeInfo> Modes { get; set; } = new();
    public List<NamedItem> Maps { get; set; } = new();
    public List<SettingInfo> Settings { get; set; } = new();
    public List<ValueSettingInfo> ValueSettings { get; set; } = new();
    public List<ActionInfo> Actions { get; set; } = new();
    public WarmupInfo Warmup { get; set; } = new();
    public RotationInfo Rotation { get; set; } = new();
    public RoundsInfo Rounds { get; set; } = new();
    public MatchZyInfo? MatchZy { get; set; }          // Null when MatchZy isn't loaded
    public ConsoleInfo Console { get; set; } = new();
    public List<IntegrationInfo> Integrations { get; set; } = new();

    // Protocol 1 additions (round 3). An older agent leaves them out; the panel shows "update GameModeManager" there.
    public ServerInfo? Server { get; set; }
    public List<PluginInfo>? Plugins { get; set; }       // Null: the agent can't list CounterStrikeSharp's plugins
    public List<DataSource>? Data { get; set; }          // The plugins' data the agent can read (installed plugins only)
    public List<RosterEntry>? Roster { get; set; }        // Who's on (humans): player history and "who's on". Null from an older agent
}

// The Admin page's server settings, and the address players connect to
public sealed class ServerInfo
{
    public string Hostname { get; set; } = "";
    public string? Address { get; set; }              // Public ip:port for steam://connect, or null when unknown
    public int Port { get; set; }                     // hostport: the Connect fallback, with the address the agent connects from
    public string TeamCt { get; set; } = "";          // mp_teamname_1
    public string TeamT { get; set; } = "";           // mp_teamname_2
}

// A CounterStrikeSharp plugin, as css_plugins list shows it, plus folders in plugins/ that aren't loaded
public sealed class PluginInfo
{
    public string Name { get; set; } = "";            // The plugins/ folder name: what css_plugins load/unload/reload take
    public string Module { get; set; } = "";          // ModuleName
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string State { get; set; } = "";           // loaded, unloaded, loading, notloaded (never loaded), disabled (plugins/disabled)
    public bool Protected { get; set; }               // GameModeManager itself: unloading it would cut the panel off
}

// One plugin whose data the agent reads through that plugin's own database connection
public sealed class DataSource
{
    public string Id { get; set; } = "";              // server, simpleadmin
    public string Name { get; set; } = "";            // CS2-SimpleAdmin ...
    public bool Available { get; set; }
    public string? Reason { get; set; }               // Why not: no database settings, unsupported database type ...
    public List<DataList> Lists { get; set; } = new();
}

public sealed class DataList
{
    public string Id { get; set; } = "";              // bans, mutes, warns, vips, ranks, skins, players ...
    public string Label { get; set; } = "";
    public bool Search { get; set; }                  // Takes a name or SteamID search
    public List<string> Filters { get; set; } = new(); // Status filters the list takes (active, all)
    public string Flag { get; set; } = "@css/root";     // The admin flag reading it needs, as the plugin's own commands want
    public List<DataAction> Actions { get; set; } = new();   // Writes, through the plugin's own console commands
}

public sealed class DataAction
{
    public string Id { get; set; } = "";              // ban, unban, mute, unmute ...
    public string Label { get; set; } = "";
    public bool Row { get; set; }                     // Offered on a row (with its SteamID); otherwise above the list
    public string Flag { get; set; } = "@css/root";     // The admin flag the plugin's own command needs (css_addban: @css/ban)
    public List<string> Fields { get; set; } = new(); // steamid, minutes, reason, group, amount
    public Dictionary<string, string> When { get; set; } = new();   // Row actions: only on rows with these values (status=ACTIVE)
}

public class NamedItem
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";

    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}

public sealed class ActionInfo : NamedItem
{
    public string Flag { get; set; } = "@css/cvar";   // The cfg header's permission (empty: @css/cvar)
    public string Group { get; set; } = "";           // The cfg's "// group:" ("Bots"); "" for none
}

public sealed class ModeInfo : NamedItem
{
    public List<string> Maps { get; set; } = new();   // Map names in this mode's map groups
}

public sealed class SettingInfo : NamedItem
{
    public string State { get; set; } = "Unknown";    // On, Off, Mixed or Unknown, as GameModeManager's SettingState
    public string Applies { get; set; } = "now";      // now, round, restart or map (GameModeManager's SettingApplies)
    public bool Cheats { get; set; }                  // Turning it on leaves sv_cheats on: the panel asks first
    public string Flag { get; set; } = "@css/cvar";   // The admin flag its in-game menu row needs
    public string Group { get; set; } = "";           // The cfg's "// group:" (its page in the menu); older agents send none
}

public sealed class ValueSettingInfo : NamedItem
{
    public string? Value { get; set; }                // Null when it can't be told
    public List<string> Choices { get; set; } = new();
    public List<string> CheatChoices { get; set; } = new();   // Choices that leave sv_cheats on
    public string Flag { get; set; } = "@css/cvar";
    public string Applies { get; set; } = "now";
    public string Group { get; set; } = "";
}

// GameModeManager's player votes and what an admin can start, as the Maps and Modes pages offer
public sealed class VotesInfo
{
    public bool CanStart { get; set; }                // No vote running and the next map isn't picked yet
    public string? Unavailable { get; set; }          // Why not, for people
}

public sealed class WarmupInfo
{
    public bool Enabled { get; set; }
    public bool Scheduled { get; set; }
    public bool Running { get; set; }
    public string? Mode { get; set; }
    public List<string> Modes { get; set; } = new();      // The warmup modes (not the game modes)
    public string? Unavailable { get; set; }              // Why the panel can't start one (MatchZy, Arms Race)
}

// The admin panel's Rotation page: changes last until the server restarts; GameModeManager.json keeps the defaults
public sealed class RotationInfo
{
    public bool Maps { get; set; }
    public bool Modes { get; set; }
    public int Interval { get; set; }                 // Change mode every this many maps
    public List<int> IntervalChoices { get; set; } = new() { 1, 2, 3, 4, 5, 6, 8, 10 };
    public string ModeVote { get; set; } = "random";  // random or vote
    public bool CountSkipped { get; set; }
    public bool Rtv { get; set; }                     // RTV and nominate
    public bool WhenEmpty { get; set; }
    public int EmptyMinutes { get; set; }
    public List<int> EmptyChoices { get; set; } = new() { 5, 10, 15, 20, 30, 60 };
    public bool Schedules { get; set; }
    public int MapsCounted { get; set; }
    public bool ModeChangeDue { get; set; }
}

public sealed class RoundsInfo
{
    public bool Enabled { get; set; } = true;          // Actions.Rounds in GameModeManager's config
    public string Flag { get; set; } = "@css/generic"; // Actions.RoundsPermission
    public string Pause { get; set; } = "Running";    // Running, Pending or Paused, as GameModeManager's MatchPause
    public bool PauseUnavailable { get; set; }        // In warmup or a timeout, with nothing to unpause
    public bool CanTimeout { get; set; }
    public bool TimeoutsByMatchZy { get; set; }       // During a live MatchZy match timeouts are players' .tac
    public bool MatchZyLive { get; set; }
    public bool Frozen { get; set; }                  // "Freeze players" is on
    public bool CanEndRound { get; set; }
    public int CurrentRound { get; set; }
    public List<int> Backups { get; set; } = new();   // Rounds that can be restored, newest first: [0] this round, [1] the last one
}

// MatchZy's state, as GameModeManager's !matchzy page reads it
public sealed class MatchZyInfo
{
    public bool Live { get; set; }
    public bool Practice { get; set; }
    public bool Veto { get; set; }
    public bool ReadyUp { get; set; }
    public bool Paused { get; set; }
    public bool KnifeRound { get; set; }
    public bool Playout { get; set; }
    public bool Whitelist { get; set; }
}

public sealed class ConsoleInfo
{
    public bool Available { get; set; }               // The agent can reach the server's own RCON on 127.0.0.1
    public string? Reason { get; set; }               // Why not, for people
}

public sealed class IntegrationInfo
{
    public string Name { get; set; } = "";
    public bool Found { get; set; }
    public string Detail { get; set; } = "";
}
