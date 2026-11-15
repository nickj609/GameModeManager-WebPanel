using GmmPanel.Data;

namespace GmmPanel.Auth;

// Which admin flag each part of the panel needs. The defaults are what the game asks for (GameModeManager's menus,
// css_plugins, the plugins' own commands); panel admins can change them on the Panel page (panel.json "Permissions"), for
// example to let a moderator group change the hostname. Settings, actions and plugin lists keep their own flags (the cfg's
// "// permission:" and the plugins' commands) and aren't listed here.
public static class PanelPermissions
{
    public sealed record Area(string Id, string Label, string Default, string Help);

    public static readonly Area[] Areas =
    {
        new("maps", "Maps, modes and votes", Flags.ChangeMap, "Manage: change, set next, random, start a vote"),
        new("warmup", "Warmup", Flags.Cvar, "Manage: start, schedule and end warmups"),
        new("rotation", "Rotation", Flags.Cvar, "Manage: map and mode rotation, RTV"),
        new("server", "Hostname and team names", Flags.Cvar, "Admin: the Server section"),
        new("plugins", "Load, unload and reload plugins", Flags.Root, "Admin: the Plugins section's buttons"),
        new("console", "Console", Flags.Rcon, "Admin: the server's console"),
        new("matchzy", "MatchZy", Flags.Config, "Manage: MatchZy's commands"),
        new("players", "Live players", Flags.Generic, "Moderate: who's on, with score and ping"),
        new("kick", "Kick", "@css/kick", "Moderate: Kick on the live players list"),
        new("chat", "Chat log", "@css/chat", "Moderate: what players said"),
        new("history", "Player history", Flags.Generic, "Moderate: everyone seen, and each player's page"),
        new("appeals", "Ban appeals", "@css/unban", "Moderate: read, accept (unban) and reject appeals"),
        new("reports", "Player reports", "@css/kick", "Moderate: read and close reports"),
        new("serveradmins", "Server admins", Flags.Generic, "Moderate: CounterStrikeSharp's admins and groups, read only")
    };

    // CounterStrikeSharp's own flags, offered on the Panel page
    public static readonly string[] Choices =
    {
        "@css/generic", "@css/kick", "@css/ban", "@css/unban", "@css/slay", "@css/changemap", "@css/cvar", "@css/config",
        "@css/chat", "@css/vote", "@css/password", "@css/rcon", "@css/cheats", "@css/root"
    };

    // Set once at startup (Program.cs); panel.json is reread when it changes
    public static PanelStore? Store { get; set; }

    public static string Need(string area)
    {
        if (Store?.Current.Permissions.TryGetValue(area, out string? flag) == true && IsFlag(flag))
            return flag;
        return Areas.FirstOrDefault(a => a.Id == area)?.Default ?? Flags.Root;
    }

    public static bool IsFlag(string? flag) =>
        flag is { Length: > 3 and <= 64 } && flag[0] == '@' && flag.IndexOf('/') > 1 && flag.All(c => char.IsAsciiLetterOrDigit(c) || c is '@' or '/' or '_' or '-');
}
