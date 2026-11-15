using GmmPanel.Agents;
using GmmPanel.Auth;
using GmmPanel.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GmmPanel.Pages;

// The header's search (Ctrl+K): pages the viewer can open, servers by name, hostname, mode or map, and for staff the
// players the panel has seen. Answers with a short list of links for the dropdown.
[EnableRateLimiting("public")]
public class SearchModel : PanelPage
{
    private readonly PanelStore _store;
    private readonly AgentRegistry _registry;
    private readonly History _history;
    private readonly PublicData _data;

    public SearchModel(PanelStore store, AgentRegistry registry, History history, PublicData data)
    {
        _store = store;
        _registry = registry;
        _history = history;
        _data = data;
    }

    public sealed record Hit(string Href, string Label, string Detail, string Icon);

    public List<Hit> Hits { get; } = new();
    public string Query { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(string? q)
    {
        Query = (q ?? "").Trim();
        if (Query.Length > 64)
            Query = Query[..64];
        if (Query.Length == 0)
            return Partial("_SearchResults", this);
        bool Match(string? text) => text != null && text.Contains(Query, StringComparison.OrdinalIgnoreCase);

        string? server = Viewer == null ? null : Selection.Get(HttpContext, Viewer, _store);
        Access access = await AccessAsync(server);

        // Pages
        var pages = new List<Hit> { new("/", "Servers", "Page", "list") };
        if (access.IsStaff && server != null)
        {
            if (access.CanManage) pages.Add(new($"/servers/{Uri.EscapeDataString(server)}", "Manage Game", $"Page · {server}", "crosshair"));
            if (access.CanAdminister) pages.Add(new($"/servers/{Uri.EscapeDataString(server)}/admin", "Manage Server", $"Page · {server}", "server"));
            pages.Add(new("/moderate", "Moderate", "Bans, mutes, appeals, reports, chat, admins", "ban"));
        }
        if (_data.Offers("simpleadmin", "bans")) pages.Add(new("/bans", "Bans", "Page", "gavel"));
        if (_store.Current.Servers.Count > 0 && Features.On(_store.Current, "playtime")) pages.Add(new("/stats", "Stats", "Page", "stats"));
        if (Viewer != null) pages.Add(new("/profile", "My profile", "Appeals, reports, Discord", "teamplayer"));
        if (Viewer?.IsPanelAdmin == true)
        {
            pages.Add(new("/audit", "Audit log", "Page", "shield"));
            pages.Add(new("/site", "Site settings", "Look, backgrounds, features, admins, servers, Discord, backup", "settings"));
        }
        pages.Add(new("/appearance", "Appearance", "Page", "settings"));
        Hits.AddRange(pages.Where(p => Match(p.Label) || Match(p.Detail)).Take(4));

        // Servers, by name, hostname, mode or map
        foreach (ServerEntry entry in _store.Current.Servers.Where(s => Viewer == null || Viewer.CanUse(s.Name)))
        {
            ServerView view = _registry.Get(entry.Name);
            var s = view.Status;
            if (Match(entry.Name) || Match(s?.Server?.Hostname) || Match(s?.Mode.Label) || Match(s?.Map.Label) || Match(s?.Map.Name))
                Hits.Add(new($"/server/{Uri.EscapeDataString(entry.Name)}", entry.Name,
                    s == null ? "No status yet" : $"{s.Mode.Label} · {s.Map.Label} · {(view.Online ? s.Players : 0)}/{s.MaxPlayers}", "server"));
        }

        // Players the panel has seen: staff with the player history flag
        if (Viewer != null && access.Has(PanelPermissions.Need("history")) && Query.Length >= 2)
            foreach (var player in _history.Players(Query, 1, 6).Rows)
                Hits.Add(new($"/players/{player.SteamId}", player.Name, $"Player · {player.SteamId}", "teamplayer"));

        return Partial("_SearchResults", this);
    }
}
