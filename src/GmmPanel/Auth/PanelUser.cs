using System.Security.Claims;
using GmmPanel.Data;

namespace GmmPanel.Auth;

// The person signed in through Steam. A panel admin when panel.json lists them (read on every request, so a removed one
// counts at once); otherwise a player, whose rights on each server come from that server's admin flags (Access.cs).
public sealed class PanelUser
{
    public const string SteamIdClaim = "gmm:steamid";

    private PanelUser(string steamId, string name, AdminEntry? entry)
    {
        SteamId = steamId;
        SteamName = name;
        Entry = entry;
    }

    public AdminEntry? Entry { get; }
    public string SteamId { get; }
    private string SteamName { get; }
    public string Name => Entry != null && !string.IsNullOrWhiteSpace(Entry.Name) ? Entry.Name : SteamName;
    public bool IsPanelAdmin => Entry != null;
    public string Role => IsPanelAdmin ? Roles.Admin : Roles.Player;
    public string Audit => $"{Name}{_suffix} ({SteamId})";

    // Which servers this person may open at all: a panel admin's list (empty = all), everyone else every server, where their
    // flags on that server then decide what they see
    public bool CanUse(string server) => Entry?.CanUse(server) ?? true;

    public static PanelUser? From(ClaimsPrincipal principal, PanelStore store)
    {
        string? steamId = principal.FindFirstValue(SteamIdClaim);
        if (steamId == null)
            return null;
        AdminEntry? entry = store.FindAdmin(steamId);
        if (entry != null && !Roles.IsValid(entry.Role))
            entry = null;
        return new PanelUser(steamId, principal.FindFirstValue(ClaimTypes.Name) ?? steamId, entry);
    }

    // A scheduled task runs as the person who made it, as they are now in panel.json (a removed panel admin is a player
    // again, whose flags each server checks). "(scheduled)" marks it in the audit log.
    public static PanelUser ForTask(string steamId, string name, PanelStore store)
    {
        AdminEntry? entry = store.FindAdmin(steamId);
        if (entry != null && !Roles.IsValid(entry.Role))
            entry = null;
        var user = new PanelUser(steamId, name, entry);
        user._suffix = " · scheduled";
        return user;
    }

    private string _suffix = "";

    // Steam's claimed ID is https://steamcommunity.com/openid/id/<SteamID64>
    public static string? SteamIdFromOpenId(string? claimedId)
    {
        const string prefix = "https://steamcommunity.com/openid/id/";
        if (claimedId == null || !claimedId.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        string id = claimedId[prefix.Length..];
        return id.Length == 17 && id.All(char.IsAsciiDigit) ? id : null;
    }

    public static ClaimsPrincipal Principal(string steamId, string name, string role, string scheme)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(SteamIdClaim, steamId),
            new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(name) ? steamId : name),
            new Claim(ClaimTypes.Role, role)
        }, scheme);
        return new ClaimsPrincipal(identity);
    }

    // The steam name kept from sign-in; the role is read again from panel.json
    public static ClaimsPrincipal Principal(PanelUser user, string scheme) => Principal(user.SteamId, user.SteamName, user.Role, scheme);
}
