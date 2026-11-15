using GmmPanel.Agents;
using Microsoft.Extensions.Caching.Memory;

namespace GmmPanel.Auth;

// The admin flags the panel uses: the same ones CounterStrikeSharp, GameModeManager's menus and CS2-SimpleAdmin ask for,
// so someone sees in the panel exactly what they could do in game
public static class Flags
{
    public const string Root = "@css/root";
    public const string Generic = "@css/generic";
    public const string ChangeMap = "@css/changemap";
    public const string Cvar = "@css/cvar";
    public const string Config = "@css/config";
    public const string Rcon = "@css/rcon";
    public const string Vip = "@css/vip";
    public const string Reservation = "@css/reservation";

    // Perks, not staff powers: a player with only these is a player (shown as VIP with @css/vip), never staff
    public static readonly HashSet<string> Perks = new(StringComparer.OrdinalIgnoreCase) { Vip, Reservation };
}

// What one person may do on one server. Panel admins may do everything. Anyone else gets the admin flags and groups that
// server knows for their SteamID (CS2-SimpleAdmin's admins, admins.json, admin_groups.json: CounterStrikeSharp's
// AdminManager, asked through GameModeManager). Nobody else can do anything but look.
public sealed class Access
{
    public static readonly Access Visitor = new(false, false, new HashSet<string>(), new List<string>());

    public Access(bool signedIn, bool panelAdmin, HashSet<string> flags, List<string> groups)
    {
        SignedIn = signedIn;
        PanelAdmin = panelAdmin;
        Flags = flags;
        Groups = groups;
    }

    public bool SignedIn { get; }
    public bool PanelAdmin { get; }
    public IReadOnlySet<string> Flags { get; }
    public IReadOnlyList<string> Groups { get; }

    // A flag, or its domain's root (@css/root covers every @css/ flag), as CounterStrikeSharp decides it
    public bool Has(string flag)
    {
        if (PanelAdmin)
            return true;
        if (Flags.Contains(flag))
            return true;
        int slash = flag.IndexOf('/');
        return slash > 0 && Flags.Contains(flag[..slash] + "/root");
    }

    // Staff: any admin flag besides the perks (a VIP's @css/vip or a reserved slot doesn't make them staff)
    public bool IsStaff => PanelAdmin || Flags.Any(f => !Auth.Flags.Perks.Contains(f));

    // A VIP: @css/vip (from CS2-SimpleAdmin, admins.json or a group such as #vip) and not staff, who show their staff role
    public bool IsVip => !IsStaff && Flags.Contains(Auth.Flags.Vip);

    // Manage: GameModeManager's in-game admin pages need these (maps and modes, settings and warmup, rounds, MatchZy)
    public bool CanManage => Has(PanelPermissions.Need("maps")) || Has(PanelPermissions.Need("warmup")) || Has(PanelPermissions.Need("rotation"))
        || Has(PanelPermissions.Need("matchzy")) || Has(Auth.Flags.Cvar) || Has(Auth.Flags.Generic);

    // Admin: server settings (@css/cvar), the console (@css/rcon), plugins (@css/root), or what the Panel page set instead
    public bool CanAdminister => Has(PanelPermissions.Need("server")) || Has(PanelPermissions.Need("console")) || Has(PanelPermissions.Need("plugins"));

    // The name people see: the server's own group ("#moderator" shows as Moderator), else what the flags amount to
    public string RoleName
    {
        get
        {
            if (PanelAdmin || Has(Auth.Flags.Root))
                return "Admin";
            if (!IsStaff)
                return IsVip ? "VIP" : SignedIn ? "Player" : "Visitor";
            if (Groups.Count > 0)
                return Pretty(Groups[0]);
            return Has(Auth.Flags.ChangeMap) || Has(Auth.Flags.Cvar) ? "Admin" : "Moderator";
        }
    }

    // The colour of the role: admin (red), moderator (orange), VIP (purple), player (blue). A server group's name decides
    // when it says which ("#senior_mod"), else its flags do.
    public string RoleClass
    {
        get
        {
            if (PanelAdmin || Has(Auth.Flags.Root))
                return "admin";
            if (!IsStaff)
                return IsVip ? "vip" : SignedIn ? "player" : "";
            string name = RoleName.ToLowerInvariant();
            if (Groups.Count > 0 && (name.Contains("admin") || name.Contains("owner")))
                return "admin";
            if (Groups.Count > 0 && (name.Contains("mod") || name.Contains("helper") || name.Contains("staff")))
                return "moderator";
            return Has(Auth.Flags.ChangeMap) || Has(Auth.Flags.Cvar) ? "admin" : "moderator";
        }
    }

    private static string Pretty(string group)
    {
        string name = group.TrimStart('#').Replace('_', ' ').Replace('-', ' ').Trim();
        return name.Length == 0 ? "Admin" : char.ToUpperInvariant(name[0]) + name[1..];
    }
}

public sealed class AccessService
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);
    private readonly AgentRegistry _registry;
    private readonly IMemoryCache _cache;

    public AccessService(AgentRegistry registry, IMemoryCache cache)
    {
        _registry = registry;
        _cache = cache;
    }

    // What this person may do on this server. Asks that server at most every 2 minutes; while it's offline, the last answer
    // stands (or none: a player). Panel admins never need asking.
    public async Task<Access> ForAsync(PanelUser? user, string? server)
    {
        if (user == null)
            return Access.Visitor;
        if (user.IsPanelAdmin)
            return Full(user, server);
        if (server == null)
            return new Access(true, false, new(), new());
        if (_cache.TryGetValue(Key(server, user.SteamId), out Access? known) && known != null)
            return known;

        Access found = await AskAsync(user, server) ?? Stale(user, server) ?? new Access(true, false, new(), new());
        _cache.Set(Key(server, user.SteamId), found, Fresh);
        _cache.Set(Key(server, user.SteamId) + ":last", found, TimeSpan.FromHours(12));
        return found;
    }

    // Without asking: what a page already found this request (the header reads this)
    public Access Cached(PanelUser? user, string? server)
    {
        if (user == null)
            return Access.Visitor;
        if (user.IsPanelAdmin)
            return Full(user, server);
        if (server != null && _cache.TryGetValue(Key(server, user.SteamId), out Access? known) && known != null)
            return known;
        return Stale(user, server) ?? new Access(true, false, new(), new());
    }

    // Forget what's known about someone (after their admin rights changed through the panel)
    public void Forget(string steamId)
    {
        foreach (ServerView view in _registry.All())
        {
            _cache.Remove(Key(view.Name, steamId));
            _cache.Remove(Key(view.Name, steamId) + ":last");
        }
    }

    private Access Full(PanelUser user, string? server) =>
        server == null || user.CanUse(server) ? new Access(true, true, new(), new()) : new Access(true, false, new(), new());

    private Access? Stale(PanelUser user, string? server) =>
        server != null && _cache.TryGetValue(Key(server, user.SteamId) + ":last", out Access? last) ? last : null;

    private static string Key(string server, string steamId) => $"access:{server.ToLowerInvariant()}:{steamId}";

    // GameModeManager's "whois": the flags and groups CounterStrikeSharp's AdminManager has for that SteamID on that server.
    private async Task<Access?> AskAsync(PanelUser user, string server)
    {
        ServerView? view = _registry.Find(server);
        AgentConnection? connection = view?.Connection;
        if (connection == null || view?.Status == null)
            return null;

        CommandResult who = await connection.RunAsync("whois", new() { ["steamid"] = user.SteamId }, user.SteamId, user.Name);
        if (!who.Ok)
            return null;   // An older GameModeManager without whois: a player
        var row = who.Data?.Rows.FirstOrDefault();
        var flags = Split(row?.GetValueOrDefault("flags"));
        var groups = Split(row?.GetValueOrDefault("groups")).ToList();
        return new Access(true, false, flags, groups);
    }

    private static HashSet<string> Split(string? text) =>
        (text ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
