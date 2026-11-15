using GmmPanel.Auth;
using GmmPanel.Commands;
using GmmPanel.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GmmPanel.Pages;

// A player's public profile, for anyone (/u/<SteamID64>): their Steam picture and name, where they are now, and how much
// they've played and where. Only what's public already (the dashboard, Stats and
// the scoreboard show the same things); staff's notes, chat and sanctions stay on the staff player page. A player can hide
// theirs on My profile, and then the page says only that it's private.
[EnableRateLimiting("public")]
public class PublicProfileModel : PanelPage
{
    private readonly History _history;
    private readonly SteamProfile _steam;
    private readonly UserPrefs _prefs;

    public PublicProfileModel(History history, SteamProfile steam, UserPrefs prefs)
    {
        _history = history;
        _steam = steam;
        _prefs = prefs;
    }

    [BindProperty(SupportsGet = true)]
    public string SteamId { get; set; } = "";

    public History.PlayerRecord? Record { get; private set; }
    public string Name { get; private set; } = "";
    public string? Avatar { get; private set; }
    public bool Hidden { get; private set; }
    public bool Own => Viewer?.SteamId == SteamId;
    public (string Server, DateTimeOffset Since)? Online { get; private set; }
    public long Played { get; private set; }
    public long PlayedThisWeek { get; private set; }

    // Staff with the player history flag somewhere get a link to the full staff page
    public bool StaffLink { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!CommandCatalog.IsSteamId(SteamId))
            return NotFound();
        // Public profiles switched off (Site settings, Features): their Steam page instead
        if (!Features.On(HttpContext.RequestServices.GetRequiredService<PanelStore>().Current, "profiles"))
            return Redirect($"https://steamcommunity.com/profiles/{SteamId}");
        Record = _history.Player(SteamId);
        Hidden = _prefs.IsHidden(SteamId) && !Own;
        Avatar = await _steam.AvatarAsync(SteamId);
        Name = Record?.Name is { Length: > 0 } seen ? seen : _steam.KnownName(SteamId) ?? SteamId;
        if (Viewer != null)
            StaffLink = (await AccessAsync(Selection.Get(HttpContext, Viewer, HttpContext.RequestServices.GetRequiredService<PanelStore>())))
                .Has(PanelPermissions.Need("history"));
        if (Hidden)
            return Page();

        Online = _history.OnlineNow(SteamId);
        if (Record != null)
        {
            Played = _history.Seconds(Record);
            PlayedThisWeek = _history.WeekSeconds(Record);
        }
        // Nobody we know: never seen on a server
        if (Record == null)
            Response.StatusCode = 404;
        return Page();
    }
}
