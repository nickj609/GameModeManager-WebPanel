using GmmPanel.Agents;
using GmmPanel.Commands;
using GmmPanel.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GmmPanel.Pages;

// Report a player: anyone signed in, from the account menu or a server's page. Staff answer under Moderate; the person
// sees it on My profile.
[EnableRateLimiting("public")]
public class ReportModel : PanelPage
{
    private readonly PanelStore _store;
    private readonly AgentRegistry _registry;
    private readonly Tickets _tickets;
    private readonly History _history;
    private readonly Discord _discord;
    private readonly AuditLog _audit;

    public ReportModel(PanelStore store, AgentRegistry registry, Tickets tickets, History history, Discord discord, AuditLog audit)
    {
        _store = store;
        _registry = registry;
        _tickets = tickets;
        _history = history;
        _discord = discord;
        _audit = audit;
    }

    // Who can be reported: everyone on a server now (from its roster), by server
    public List<(string Server, List<(string SteamId, string Name)> Players)> Reportable { get; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Server { get; set; }

    // From a player's profile ("Report" there): their SteamID64, filled in
    [BindProperty(SupportsGet = true, Name = "steamId")]
    public string? Target { get; set; }

    public string? Problem { get; private set; }
    public bool HasRules => _store.Current.Rules.Length > 0 && Features.On(_store.Current, "rules");

    // Player reports switched off (Site settings, Features): the page isn't there
    public IActionResult OnGet()
    {
        if (!Features.On(_store.Current, "reports"))
            return NotFound();
        Load();
        return Page();
    }

    public IActionResult OnPost(string? player, string? steamId, string? text)
    {
        if (!Features.On(_store.Current, "reports"))
            return NotFound();
        Load();
        text = (text ?? "").Trim();
        string target = (string.IsNullOrWhiteSpace(steamId) ? player : steamId)?.Trim() ?? "";
        string server = (Server ?? "").Trim();
        if (!_store.Current.Servers.Any(s => s.Name.Equals(server, StringComparison.OrdinalIgnoreCase)))
            Problem = "Choose the server it happened on.";
        else if (!CommandCatalog.IsSteamId(target))
            Problem = "Pick a player, or enter their SteamID64 (17 digits, starting 7656119).";
        else if (target == Admin.SteamId)
            Problem = "You can't report yourself.";
        // Only someone a server has seen (on now, or in the player history): a made-up SteamID never lands with staff
        else if (_history.Player(target) == null && !Reportable.Any(r => r.Players.Any(p => p.SteamId == target)))
            Problem = "That player hasn't been on any of our servers, so there's nothing to report. Pick someone from the list.";
        else if (text.Length is < 10 or > Tickets.MaxText)
            Problem = $"Say what happened, in 10 to {Tickets.MaxText} characters.";
        if (Problem != null)
            return Page();

        string name = _history.Player(target)?.Name
            ?? Reportable.SelectMany(r => r.Players).FirstOrDefault(p => p.SteamId == target).Name ?? "";
        if (_tickets.Add(new Tickets.Ticket { Kind = "report", Server = server, SteamId = Admin.SteamId, Name = Admin.Name, TargetSteamId = target, TargetName = name, Text = text }) is string refused)
        {
            Problem = refused;
            return Page();
        }
        _audit.Write(Admin.Audit, $"report sent about {target}", server, true, "");
        _discord.Ticket("New player report", Admin.Name, Admin.SteamId, text, server, name.Length > 0 ? name : target, target);
        TempData["Sent"] = "Report sent. Thanks: staff will look at it.";
        return Redirect("/profile#tickets");
    }

    private void Load()
    {
        foreach (ServerEntry server in _store.Current.Servers.Where(s => Admin.CanUse(s.Name)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var roster = _registry.Find(server.Name) is { Online: true, Status.Roster: { } players }
                ? players.Where(p => p.SteamId != Admin.SteamId).Select(p => (p.SteamId, p.Name)).ToList()
                : new();
            Reportable.Add((server.Name, roster));
        }
    }
}
