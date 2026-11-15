using GmmPanel.Agents;
using GmmPanel.Auth;
using GmmPanel.Commands;
using GmmPanel.Data;
using GmmPanel.Protocol;
using Microsoft.AspNetCore.Mvc;

namespace GmmPanel.Pages;

// One player, for staff: who they are (Steam picture, names, play time, where they are now), what to do about them (ban,
// mute, gag, warn through CS2-SimpleAdmin; kick on the server they're on), staff's private notes, their CS2-SimpleAdmin
// record, appeals and reports about them, and what they said lately. Each part needs its own flag, as on Moderate.
public class PlayerModel : PanelPage
{
    private readonly PanelStore _store;
    private readonly AgentRegistry _registry;
    private readonly CommandService _commands;
    private readonly History _history;
    private readonly Tickets _tickets;
    private readonly PlayerNotes _notes;
    private readonly SteamProfile _steam;
    private readonly AuditLog _audit;

    public PlayerModel(PanelStore store, AgentRegistry registry, CommandService commands, History history, Tickets tickets, PlayerNotes notes, SteamProfile steam, AuditLog audit)
    {
        _store = store;
        _registry = registry;
        _commands = commands;
        _history = history;
        _tickets = tickets;
        _notes = notes;
        _steam = steam;
        _audit = audit;
    }

    [BindProperty(SupportsGet = true)]
    public string SteamId { get; set; } = "";

    public string? Server { get; private set; }
    public History.PlayerRecord? Record { get; private set; }
    public (string Server, DateTimeOffset Since)? Online { get; private set; }
    public long Played { get; private set; }
    public long PlayedThisWeek { get; private set; }

    // How many of a list's rows are still in force (ACTIVE bans, mutes, warnings)
    public int Active(string label) => Sanctions.FirstOrDefault(s => s.Label == label).Result?.Data?.Rows
        .Count(r => string.Equals(r.GetValueOrDefault("status"), "ACTIVE", StringComparison.OrdinalIgnoreCase)) ?? 0;
    public string? Avatar { get; private set; }
    public List<(string Label, CommandResult Result)> Sanctions { get; } = new();
    public List<Tickets.Ticket> About { get; private set; } = new();
    public List<History.ChatLine>? Chat { get; private set; }
    public List<PlayerNotes.Note> Notes { get; private set; } = new();
    public bool CanKick { get; private set; }
    public bool PanelAdmin => Admin.IsPanelAdmin;

    // What the viewer may do to this player through CS2-SimpleAdmin: its actions that take only a SteamID, a length and a
    // reason (ban, mute, gag, silence, warn), where their flag allows it
    public sealed record Act(string List, string Id, string Label, bool Minutes);
    public List<Act> Acts { get; } = new();

    public static readonly (int Minutes, string Label)[] Lengths = { (60, "1 hour"), (1440, "1 day"), (10080, "1 week"), (43200, "30 days"), (0, "Permanent") };

    public async Task<IActionResult> OnGetAsync()
    {
        if (!CommandCatalog.IsSteamId(SteamId))
            return NotFound();
        Access? access = await LoadAsync();
        if (access == null)
            return LocalRedirect("/denied");

        Record = _history.Player(SteamId);
        Online = _history.OnlineNow(SteamId);
        Played = Record == null ? 0 : _history.Seconds(Record);
        PlayedThisWeek = Record == null ? 0 : _history.WeekSeconds(Record);
        Avatar = await _steam.AvatarAsync(SteamId);
        Notes = _notes.For(SteamId);
        if (access.Has(PanelPermissions.Need("appeals")) || access.Has(PanelPermissions.Need("reports")))
            About = _tickets.By(SteamId);
        if (access.Has(PanelPermissions.Need("chat")))
            Chat = _history.Chat(null, SteamId, "", 1, 20).Rows;
        if (Online is { } on && (await AccessAsync(on.Server)).Has(PanelPermissions.Need("kick")))
            CanKick = true;

        // CS2-SimpleAdmin's record, through the selected server, for the lists the viewer's flags allow
        ServerView? view = Server == null ? null : _registry.Find(Server);
        var simpleAdmin = view?.Status?.Data?.FirstOrDefault(d => d.Id == "simpleadmin" && d.Available);
        if (simpleAdmin != null)
            foreach (var list in simpleAdmin.Lists.Where(l => l.Id is "bans" or "mutes" or "warns" && access.Has(l.Flag)))
                Sanctions.Add((list.Label, await _commands.RunAsync(Admin, Server!, "query",
                    new() { ["source"] = "simpleadmin", ["list"] = list.Id, ["search"] = SteamId, ["filter"] = "all" })));
        return Page();
    }

    // Ban, mute, gag, warn ...: through CS2-SimpleAdmin on the selected server (its database is shared); "kick" goes to the
    // server they're on
    public async Task<IActionResult> OnPostActAsync(string? pick, int minutes, string? reason)
    {
        if (!CommandCatalog.IsSteamId(SteamId) || await LoadAsync() == null)
            return Forbid();
        reason = (reason ?? "").Trim();
        CommandResult result;
        if (pick == "kick")
        {
            Online = _history.OnlineNow(SteamId);
            result = Online is { } on
                ? await _commands.RunAsync(Admin, on.Server, "moderate", new() { ["source"] = "server", ["list"] = "players", ["action"] = "kick", ["steamid"] = SteamId })
                : new CommandResult(false, "They aren't on a server now.");
        }
        else if (Acts.FirstOrDefault(a => $"{a.List}:{a.Id}" == pick) is { } act)
        {
            var args = new Dictionary<string, string> { ["source"] = "simpleadmin", ["list"] = act.List, ["action"] = act.Id, ["steamid"] = SteamId, ["reason"] = reason };
            if (act.Minutes)
                args["minutes"] = Lengths.Any(l => l.Minutes == minutes) ? minutes.ToString() : "1440";
            result = await _commands.RunAsync(Admin, Server!, "moderate", args);
        }
        else
            result = new CommandResult(false, "Choose what to do.");
        if (result.Ok)
            Response.Headers["HX-Refresh"] = "true";
        return Partial("_Toast", result);
    }

    public async Task<IActionResult> OnPostNoteAsync(string? text)
    {
        if (!CommandCatalog.IsSteamId(SteamId) || await LoadAsync() == null)
            return Forbid();
        text = (text ?? "").Trim();
        if (text.Length is < 2 or > PlayerNotes.MaxText)
            return Partial("_Toast", new CommandResult(false, $"A note is 2 to {PlayerNotes.MaxText} characters."));
        _notes.Add(SteamId, Admin.SteamId, Admin.Name, text);
        _audit.Write(Admin.Audit, $"note added on {SteamId}", null, true, "");
        Response.Headers["HX-Refresh"] = "true";
        return Partial("_Toast", new CommandResult(true, "Note saved."));
    }

    public async Task<IActionResult> OnPostDeleteNoteAsync(string? id)
    {
        if (!CommandCatalog.IsSteamId(SteamId) || await LoadAsync() == null)
            return Forbid();
        string? problem = _notes.Delete(SteamId, id ?? "", Admin.SteamId, Admin.IsPanelAdmin);
        if (problem == null)
        {
            _audit.Write(Admin.Audit, $"note deleted on {SteamId}", null, true, "");
            Response.Headers["HX-Refresh"] = "true";
        }
        return Partial("_Toast", new CommandResult(problem == null, problem ?? "Note deleted."));
    }

    // The viewer's rights here (player history's flag), and what CS2-SimpleAdmin offers them; null when they may not look
    private async Task<Access?> LoadAsync()
    {
        Server = Selection.Get(HttpContext, Admin, _store);
        Access access = await AccessAsync(Server);
        if (!access.Has(PanelPermissions.Need("history")))
            return null;
        ServerView? view = Server == null ? null : _registry.Find(Server);
        var simpleAdmin = view?.Status?.Data?.FirstOrDefault(d => d.Id == "simpleadmin" && d.Available);
        foreach (DataList list in simpleAdmin?.Lists.Where(l => l.Id is "bans" or "mutes" or "warns") ?? Enumerable.Empty<DataList>())
            foreach (DataAction action in list.Actions.Where(a => !a.Row && a.Fields.Contains("steamid") && a.Fields.All(f => f is "steamid" or "minutes" or "reason") && access.Has(a.Flag)))
                Acts.Add(new Act(list.Id, action.Id, action.Label, action.Fields.Contains("minutes")));
        return access;
    }
}
