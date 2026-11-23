using GmmPanel.Agents;
using GmmPanel.Data;
using Microsoft.AspNetCore.Mvc;

namespace GmmPanel.Pages;

// Every server, for communities with more than the homepage's strip shows: a server browser like Steam's or BattleMetrics',
// sortable by any column (players first), with a search, a mode filter and "With players", 25 a page. Each row opens the
// server's own page; Connect and Copy address sit at its end.
public class AllServersModel : PanelPage
{
    public const int PageSize = 25;
    private readonly PanelStore _store;
    private readonly AgentRegistry _registry;
    private readonly MapPictures _maps;

    public AllServersModel(PanelStore store, AgentRegistry registry, MapPictures maps)
    {
        _store = store;
        _registry = registry;
        _maps = maps;
    }

    [BindProperty(SupportsGet = true)] public string Q { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Mode { get; set; } = "";
    [BindProperty(SupportsGet = true)] public bool On { get; set; }
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "";   // "key" or "-key"; "" busiest first
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public List<ServerView> Rows { get; private set; } = new();
    public int Total { get; private set; }
    public int All { get; private set; }
    public int Playing { get; private set; }
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public List<string> Modes { get; private set; } = new();

    public string? ArtFor(string? map) => _maps.UrlFor(map);

    public void OnGet()
    {
        Q = (Q ?? "").Trim();
        Mode = Mode ?? "";
        Sort = Sort is { Length: <= 12 } s && System.Text.RegularExpressions.Regex.IsMatch(s, "^-?[a-z]*$") ? s : "";
        // The servers the viewer may see, as on the homepage
        var servers = _store.Current.Servers.Where(s => Viewer == null || Viewer.CanUse(s.Name)).Select(s => _registry.Get(s.Name)).ToList();
        All = servers.Count;
        Playing = servers.Where(v => v.Online).Sum(v => v.Status?.Players ?? 0);
        Modes = servers.Select(v => v.Status?.Mode.Label).Where(m => !string.IsNullOrEmpty(m)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList()!;
        int Players(ServerView v) => v.Online ? v.Status?.Players ?? 0 : 0;
        var rows = servers
            .Where(v => Q.Length == 0 || ServerNames.Label(v.Name).Contains(Q, StringComparison.OrdinalIgnoreCase) || v.Name.Contains(Q, StringComparison.OrdinalIgnoreCase)
                || (v.Status?.Mode.Label ?? "").Contains(Q, StringComparison.OrdinalIgnoreCase) || (v.Status?.Map.Name ?? "").Contains(Q, StringComparison.OrdinalIgnoreCase)
                || (v.Status?.Map.Label ?? "").Contains(Q, StringComparison.OrdinalIgnoreCase))
            .Where(v => Mode.Length == 0 || string.Equals(v.Status?.Mode.Label, Mode, StringComparison.OrdinalIgnoreCase))
            .Where(v => !On || Players(v) > 0);
        bool desc = Sort.StartsWith('-');
        rows = Sort.TrimStart('-') switch
        {
            "name" => desc ? rows.OrderByDescending(v => ServerNames.Label(v.Name), StringComparer.OrdinalIgnoreCase) : rows.OrderBy(v => ServerNames.Label(v.Name), StringComparer.OrdinalIgnoreCase),
            "mode" => desc ? rows.OrderByDescending(v => v.Status?.Mode.Label ?? "") : rows.OrderBy(v => v.Status?.Mode.Label ?? ""),
            "map" => desc ? rows.OrderByDescending(v => v.Status?.Map.Label ?? "") : rows.OrderBy(v => v.Status?.Map.Label ?? ""),
            "players" when !desc => rows.OrderBy(v => Players(v)).ThenBy(v => v.Online ? 0 : 1),
            // Busiest first; offline last; then by name, so the order holds still
            _ => rows.OrderByDescending(v => Players(v)).ThenBy(v => v.Online ? 0 : 1).ThenBy(v => ServerNames.Label(v.Name), StringComparer.OrdinalIgnoreCase)
        };
        var list = rows.ToList();
        Total = list.Count;
        P = Math.Clamp(P, 1, Pages);
        Rows = list.Skip((P - 1) * PageSize).Take(PageSize).ToList();
    }

    public string PageUrl(int page, string? sort = null) => $"/servers?q={Uri.EscapeDataString(Q)}&mode={Uri.EscapeDataString(Mode)}{(On ? "&on=true" : "")}&p={page}" +
        ((sort ?? Sort) is { Length: > 0 } order ? $"&sort={Uri.EscapeDataString(order)}" : "");
}
