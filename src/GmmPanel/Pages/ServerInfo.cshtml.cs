using GmmPanel.Agents;
using GmmPanel.Auth;
using GmmPanel.Data;
using GmmPanel.Protocol;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GmmPanel.Pages;

// One server, for everyone: what it's playing, who's on, and how busy it's been (24 hours and 7 days)
// Staff also get each player's SteamID, linked to their history.
[EnableRateLimiting("public")]
public class ServerInfoModel : PanelPage
{
    private readonly PanelStore _store;
    private readonly AgentRegistry _registry;
    private readonly History _history;
    private readonly MapPictures _maps;
    private readonly SteamProfile _steam;

    public ServerInfoModel(PanelStore store, AgentRegistry registry, History history, MapPictures maps, SteamProfile steam)
    {
        _store = store;
        _registry = registry;
        _history = history;
        _maps = maps;
        _steam = steam;
    }

    [BindProperty(SupportsGet = true)]
    public string Name { get; set; } = "";

    public ServerView View { get; private set; } = null!;
    public List<(DateTimeOffset At, int Players, int Max)> Week { get; private set; } = new();
    public Dictionary<string, DateTimeOffset> Since { get; private set; } = new();
    public bool Staff { get; private set; }
    public bool CanManage { get; private set; }

    // The map's picture (data/maps) for the header's square, and the Steam pictures already known for who's on
    public string? ArtFor(string map) => _maps.UrlFor(map);
    public (List<(string Country, int Players)> Countries, int Other, int Total)? Countries { get; private set; }   // Player countries (Features: countries)
    public List<History.MapPlay> Maps { get; private set; } = new();   // The last maps played, the current one first
    public string? AvatarOf(string steamId) => _steam.KnownAvatar(steamId);

    public async Task<IActionResult> OnGetAsync()
    {
        ServerEntry? entry = _store.Current.Servers.FirstOrDefault(s => s.Name.Equals(Name, StringComparison.OrdinalIgnoreCase));
        if (entry == null || (Viewer != null && !Viewer.CanUse(entry.Name)))
            return NotFound();
        View = _registry.Get(entry.Name);
        Week = _history.Counts(View.Name, DateTimeOffset.UtcNow.AddDays(-7));
        Maps = _history.RecentMaps(View.Name, 10);
        if (FeatureOn("countries"))
            Countries = _history.Countries(View.Name, DateTimeOffset.UtcNow.AddDays(-30));
        Since = _history.OnlineOn(View.Name);
        foreach (RosterEntry p in (View.Status?.Roster ?? new()).Where(p => _steam.KnownAvatar(p.SteamId) == null).Take(8))
            _ = _steam.AvatarAsync(p.SteamId);   // In the background: the page never waits on Steam
        if (Viewer != null)
        {
            Access access = await AccessAsync(View.Name);
            Staff = access.Has(PanelPermissions.Need("history"));
            CanManage = access.CanManage || access.CanAdminister;
        }
        return Page();
    }
}
