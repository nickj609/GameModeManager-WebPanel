using GmmPanel.Agents;
using GmmPanel.Data;
using GmmPanel.Protocol;

namespace GmmPanel.Pages;

// Leaderboards, for everyone: the panel's own boards from player history: most played of all time, this week, and the
// newest players. Players who hide their public profile are left off.
public class StatsModel : PublicListModel
{
    private const int PageSize = 25;
    private readonly History _history;
    private readonly UserPrefs _prefs;

    public StatsModel(PublicData data, History history, UserPrefs prefs) : base(data)
    {
        _history = history;
        _prefs = prefs;
    }

    public override (string Id, string Source, string List, string Label)[] Lists { get; } =
    {
        ("played", PanelSource, "played", "Most played"),
        ("week", PanelSource, "week", "This week"),
        ("newest", PanelSource, "newest", "Newest")
    };

    public override string Path => "/stats";

    // The play-time boards switched off (Site settings, Features): the page isn't offered
    protected override bool PanelListsOn => FeatureOn("playtime");

    // The line under the title, for the board shown
    public string Note => Current.Id switch
    {
        "played" => "Hours played on all our servers, since the panel started counting.",
        "week" => "Hours played this week (from Monday).",
        _ => "The latest players to join our servers."
    };

    protected override DataPage? PanelList(string list, string search, int page)
    {
        var players = _history.All().Where(p => !_prefs.IsHidden(p.SteamId));
        if (search.Length > 0)
            players = players.Where(p => p.SteamId == search || p.Names.Any(n => n.Contains(search, StringComparison.OrdinalIgnoreCase)));
        var rows = list switch
        {
            "played" => players.Select(p => (p, Value: _history.Seconds(p))).Where(x => x.Value > 0).OrderByDescending(x => x.Value).ToList(),
            "week" => players.Select(p => (p, Value: _history.WeekSeconds(p))).Where(x => x.Value > 0).OrderByDescending(x => x.Value).ToList(),
            "newest" => players.Select(p => (p, Value: 0L)).OrderByDescending(x => x.p.FirstSeen).ToList(),
            _ => null
        };
        if (rows == null)
            return null;
        bool hours = list != "newest";
        // Every board has the same five columns after the player, in the same order where they share one
        List<DataColumn> columns = list switch
        {
            "week" => new() { Col("place", "#", "number"), Col("name", "Player"), Col("steamid", "SteamID", "steamid"), Col("hours", "This week", "number"), Col("total", "Played", "number"), Col("visits", "Visits", "number"), Col("lastseen", "Last seen", "time") },
            "newest" => new() { Col("place", "", "number"), Col("name", "Player"), Col("steamid", "SteamID", "steamid"), Col("hours", "Played", "number"), Col("visits", "Visits", "number"), Col("firstseen", "First seen", "time"), Col("lastseen", "Last seen", "time") },
            _ => new() { Col("place", "#", "number"), Col("name", "Player"), Col("steamid", "SteamID", "steamid"), Col("hours", "Played", "number"), Col("visits", "Visits", "number"), Col("firstseen", "First seen", "time"), Col("lastseen", "Last seen", "time") }
        };
        return new DataPage
        {
            Columns = columns,
            Rows = rows.Skip((page - 1) * PageSize).Take(PageSize).Select((x, i) => new Dictionary<string, string?>
            {
                ["place"] = hours ? ((page - 1) * PageSize + i + 1).ToString() : "",   // Newest is no ranking: its place column stays empty, so the boards line up
                ["name"] = x.p.Name,
                ["steamid"] = x.p.SteamId,
                ["hours"] = Format.Hours(hours ? x.Value : _history.Seconds(x.p)),
                ["total"] = Format.Hours(_history.Seconds(x.p)),
                ["visits"] = x.p.Sessions.ToString("N0"),
                ["lastseen"] = Format.Ago(x.p.LastSeen),
                ["firstseen"] = Format.Ago(x.p.FirstSeen)
            }).ToList(),
            Page = page,
            PageSize = PageSize,
            Total = rows.Count
        };
    }

    private static DataColumn Col(string key, string label, string kind = "text") => new() { Key = key, Label = label, Kind = kind };
}
