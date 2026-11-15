using GmmPanel.Agents;
using GmmPanel.Auth;
using GmmPanel.Data;
using GmmPanel.Protocol;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GmmPanel.Pages;

// The dashboard: one card per server, for everyone (Connect). Manage shows where the viewer's admin flags allow it.
[EnableRateLimiting("public")]
public class IndexModel : PanelPage
{
    private readonly PanelStore _store;
    private readonly AgentRegistry _registry;
    private readonly Backgrounds _backgrounds;
    private readonly History _history;
    private readonly Tickets _tickets;
    private readonly MapPictures _maps;
    private readonly SteamProfile _steam;
    private List<Background>? _own;

    public IndexModel(PanelStore store, AgentRegistry registry, Backgrounds backgrounds, History history, Tickets tickets, MapPictures maps, SteamProfile steam)
    {
        _steam = steam;
        _maps = maps;
        _tickets = tickets;
        _history = history;
        _store = store;
        _registry = registry;
        _backgrounds = backgrounds;
    }

    public List<ServerView> Servers { get; private set; } = new();
    public Dictionary<string, Access> Rights { get; } = new(StringComparer.OrdinalIgnoreCase);

    // The lines above the cards: one for everyone, one about the person signed in, one with what staff should look at.
    // Each shows only when it has something to say.
    public int Online { get; private set; }
    public int Week { get; private set; }
    public string? BusyHours { get; private set; }
    public string? Busiest { get; private set; }
    public History.PlayerRecord? Me { get; private set; }
    public long MyPlayed { get; private set; }
    public string? MyServer { get; private set; }
    public List<Tickets.Ticket> MyOpen { get; private set; } = new();
    public int OpenAppeals { get; private set; } = -1;   // -1: the viewer can't answer them
    public int OpenReports { get; private set; } = -1;
    public int Offline { get; private set; }
    public List<string> OfflineNames { get; private set; } = new();   // Staff: which servers aren't connected
    public string? MyAvatar => Viewer == null ? null : _steam.KnownAvatar(Viewer.SteamId);

    // The community row's third card, for whoever's looking: visitors are asked to sign in, everyone else gets the owner's
    // Discord link (a community link with the Discord icon)
    public string? Discord => _store.Current.Links.FirstOrDefault(l => l.Icon == "discord")?.Url;

    public async Task OnGetAsync(string? s)
    {
        await LoadAsync();
        Choose(s);
        await SpotAsync();
        await LinesAsync();
        await CommunityAsync();
    }

    // The picker's chips, every 10 s
    public async Task<IActionResult> OnGetListAsync(string? s)
    {
        await LoadAsync();
        Choose(s);
        return Partial("_ServerStrip", this);
    }

    // The spotlight, when a chip is picked (and every 10 s): remembered for next time
    public async Task<IActionResult> OnGetSpotAsync(string? s)
    {
        await LoadAsync();
        Choose(s);
        if (Spot != null)
            Response.Cookies.Append("gmm.spot", Spot.Name, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = Request.IsHttps, MaxAge = TimeSpan.FromDays(180), IsEssential = true });
        await SpotAsync();
        return Partial("_Spotlight", this);
    }

    // ---------- The spotlight: one server, picked in the strip ----------

    public ServerView? Spot { get; private set; }
    public List<(DateTimeOffset At, int Players, int Max)> SpotDay { get; private set; } = new();
    public Dictionary<string, DateTimeOffset> SpotSince { get; private set; } = new();
    public (List<(string Country, int Players)> Countries, int Other, int Total)? SpotCountries { get; private set; }   // Player countries (Features: countries)
    public string? AvatarOf(string steamId) => _steam.KnownAvatar(steamId);

    // The asked server, else the one picked last time, else the busiest online
    private void Choose(string? asked)
    {
        string? wanted = asked ?? Request.Cookies["gmm.spot"];
        Spot = Servers.FirstOrDefault(v => v.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? Servers.Where(v => v.Online).MaxBy(v => v.Status?.Players ?? 0) ?? Servers.FirstOrDefault();
    }

    private async Task SpotAsync()
    {
        if (Spot == null)
            return;
        SpotDay = Day(Spot.Name);
        SpotSince = _history.OnlineOn(Spot.Name);
        if (FeatureOn("countries"))
            SpotCountries = _history.Countries(Spot.Name, DateTimeOffset.UtcNow.AddDays(-30));
        // Steam pictures for who's on: asked in the background, a few at a time, so the page never waits on Steam
        foreach (RosterEntry p in (Spot.Status?.Roster ?? new()).Where(p => _steam.KnownAvatar(p.SteamId) == null).Take(4))
            _ = _steam.AvatarAsync(p.SteamId);
    }

    // ---------- The community strip: signs of life across every server ----------

    public (int Count, List<History.PlayerRecord> Latest) NewFaces { get; private set; }
    public List<(History.PlayerRecord Player, long Seconds)> Regulars { get; private set; } = new();

    private async Task CommunityAsync()
    {
        NewFaces = _history.NewSince(DateTimeOffset.UtcNow.AddDays(-7), 8);
        Regulars = _history.TopThisWeek(3);
        // Their Steam pictures: the ones not known yet are asked for, and the page waits at most 1.5 s for them (the rest
        // show next time; Steam's answers are kept 6 hours)
        var missing = NewFaces.Latest.Select(p => p.SteamId).Concat(Regulars.Select(r => r.Player.SteamId))
            .Distinct().Where(id => _steam.KnownAvatar(id) == null).Take(11).Select(id => _steam.AvatarAsync(id)).ToList();
        if (missing.Count > 0)
            await Task.WhenAny(Task.WhenAll(missing), Task.Delay(1500));
    }

    // The owner's picture for a map: a still named after it (a video would play in every card, so stills only). Their own
    // backgrounds are for staff; visitors get the gradient.
    public string? ArtFor(string map)
    {
        // The map's own picture (data/maps), for everyone; then the owner's still, for people signed in
        if (_maps.UrlFor(map) is string picture)
            return picture;
        if (Viewer == null)
            return null;
        _own ??= _backgrounds.Own();
        return _own.FirstOrDefault(b => b.Kind == "still" && b.Map.Equals(map, StringComparison.OrdinalIgnoreCase))?.Url;
    }

    // The last 24 hours of player counts, for the card's graph and "peak today"
    public List<(DateTimeOffset At, int Players, int Max)> Day(string server) => _history.Counts(server, DateTimeOffset.UtcNow.AddHours(-24));

    public bool CanManage(string server) => Rights.TryGetValue(server, out Access? access) && (access.CanManage || access.CanAdminister);

    private async Task LoadAsync()
    {
        Servers = _store.Current.Servers
            .Where(server => Viewer == null || Viewer.CanUse(server.Name))
            .Select(server => _registry.Get(server.Name))
            // A steady order, like browser tabs (re-sorting by players every refresh would move the picked tab out of view):
            // by name, offline servers last
            .OrderBy(view => view.Online ? 0 : 1)
            .ThenBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (Viewer != null)
            foreach (ServerView view in Servers)
                Rights[view.Name] = await AccessAsync(view.Name);
    }

    private async Task LinesAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var live = Servers.Where(s => s.Online && s.Status != null).ToList();
        Online = live.Sum(s => s.Status!.Players);
        Week = _history.SeenSince(now.AddDays(-7));
        if (live.Count > 1 && live.Max(s => s.Status!.Players) > 0)
            Busiest = live.OrderByDescending(s => s.Status!.Players).First().Name;

        // The three busiest hours in a row over the last week, every server added up, in the panel's own time
        var byHour = new double[24];
        var samples = new int[24];
        foreach (ServerView server in Servers)
            foreach (var (at, players, _) in _history.Counts(server.Name, now.AddDays(-7)))
            {
                int hour = at.ToLocalTime().Hour;
                byHour[hour] += players;
                samples[hour]++;
            }
        if (samples.Sum() >= 24 * 6)
        {
            double Avg(int h) => samples[h % 24] == 0 ? 0 : byHour[h % 24] / samples[h % 24];
            int start = Enumerable.Range(0, 24).MaxBy(h => Avg(h) + Avg(h + 1) + Avg(h + 2));
            if (Avg(start) + Avg(start + 1) + Avg(start + 2) >= 1)
            {
                string from = Hour(start), to = Hour(start + 3);
                // "8–11 pm" rather than "8 pm–11 pm" when both ends share am or pm
                if (from.Length > 3 && to.Length > 3 && from[^3..] == to[^3..] && from[^3..] is " am" or " pm")
                    from = from[..^3];
                BusyHours = $"{from}–{to}";
            }
        }

        if (Viewer == null)
            return;
        Me = _history.Player(Viewer.SteamId);
        if (Me != null)
        {
            MyPlayed = _history.Seconds(Me);
            MyServer = _history.OnlineNow(Viewer.SteamId)?.Server;
        }
        MyOpen = _tickets.By(Viewer.SteamId).Where(t => t.SteamId == Viewer.SteamId && t.Status == "OPEN").ToList();

        // Staff: what's waiting, where their flags let them answer it
        if (Rights.Values.Any(r => r.Has(PanelPermissions.Need("appeals"))))
            OpenAppeals = _tickets.Open("appeal");
        if (Rights.Values.Any(r => r.Has(PanelPermissions.Need("reports"))))
            OpenReports = _tickets.Open("report");
        if (Rights.Values.Any(r => r.IsStaff))
        {
            OfflineNames = Servers.Where(s => !s.Online).Select(s => s.Name).ToList();
            Offline = OfflineNames.Count;
        }
    }

    private static string Hour(int hour)
    {
        hour %= 24;
        // 24-hour time outside English, as most of the panel's languages write it
        if (GmmPanel.Data.Lang.Current != "en")
            return $"{hour:00}:00";
        return hour == 0 ? "12 am" : hour < 12 ? $"{hour} am" : hour == 12 ? "12 pm" : $"{hour - 12} pm";
    }
}
