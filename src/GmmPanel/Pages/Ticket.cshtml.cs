using GmmPanel.Agents;
using GmmPanel.Auth;
using GmmPanel.Commands;
using GmmPanel.Data;
using GmmPanel.Protocol;
using Microsoft.AspNetCore.Mvc;

namespace GmmPanel.Pages;

// One appeal or report, for staff: what was said, by whom, who answered it, staff's comments (open or answered), and the
// answer: for a report, ban, mute, gag, silence or warn through CS2-SimpleAdmin (or close it); for an appeal, accept (it
// unbans through CS2-SimpleAdmin first) or reject. A comment can be shown to the writer, on their profile and their bell.
public class TicketModel : PanelPage
{
    private readonly PanelStore _store;
    private readonly AgentRegistry _registry;
    private readonly CommandService _commands;
    private readonly Tickets _tickets;
    private readonly SteamProfile _steam;
    private readonly Notices _notices;
    private readonly AuditLog _audit;

    public TicketModel(PanelStore store, AgentRegistry registry, CommandService commands, Tickets tickets, SteamProfile steam, Notices notices, AuditLog audit)
    {
        _store = store;
        _registry = registry;
        _commands = commands;
        _tickets = tickets;
        _steam = steam;
        _notices = notices;
        _audit = audit;
    }

    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = "";

    public Tickets.Ticket Ticket { get; private set; } = new();
    public string? Server { get; private set; }
    public string? TargetAvatar { get; private set; }
    public string? WriterAvatar { get; private set; }
    public bool Report => Ticket.Kind == "report";
    public bool Open => Ticket.Status == "OPEN";

    // A report's answers: CS2-SimpleAdmin's actions that take a SteamID, a length and a reason, where the flags allow
    public List<PlayerModel.Act> Acts { get; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
            return LocalRedirect("/denied");
        TargetAvatar = CommandCatalog.IsSteamId(Ticket.TargetSteamId) ? await _steam.AvatarAsync(Ticket.TargetSteamId) : null;
        WriterAvatar = Report && CommandCatalog.IsSteamId(Ticket.SteamId) ? _steam.KnownAvatar(Ticket.SteamId) : null;
        return Page();
    }

    public async Task<IActionResult> OnPostAnswerAsync(string? pick, int minutes, string? reason, string? comment, bool show)
    {
        if (!await LoadAsync())
            return Forbid();
        if (!Open)
            return Toast(false, "Someone already answered it.");
        comment = (comment ?? "").Trim();
        if (comment.Length > Tickets.MaxComment)
            return Toast(false, $"A comment is up to {Tickets.MaxComment} characters.");
        string who = Ticket.TargetName.Length > 0 ? Ticket.TargetName : Ticket.TargetSteamId;
        string status;
        string done;

        if (Report && pick != "close")
        {
            if (Acts.FirstOrDefault(a => $"{a.List}:{a.Id}" == pick) is not { } act || Server == null)
                return Toast(false, "Choose what to do.");
            reason = (reason ?? "").Trim();
            if (reason.Length == 0)
                reason = $"Reported: {Ticket.Text}";
            if (reason.Length > 120)
                reason = reason[..117] + "...";
            var args = new Dictionary<string, string> { ["source"] = "simpleadmin", ["list"] = act.List, ["action"] = act.Id, ["steamid"] = Ticket.TargetSteamId, ["reason"] = reason };
            if (act.Minutes)
                args["minutes"] = PlayerModel.Lengths.Any(l => l.Minutes == minutes) ? minutes.ToString() : "1440";
            CommandResult result = await _commands.RunAsync(Admin, Server, "moderate", args);
            if (!result.Ok)
                return Partial("_Toast", result);
            status = "CLOSED";
            done = $"{act.Label}: {who}. The report is closed.";
            _audit.Write(Admin.Audit, $"report {Ticket.Id} {act.Id} ({Ticket.TargetName} {Ticket.TargetSteamId})", Server, true, "");
        }
        else if (Report)
        {
            status = "CLOSED";
            done = "Report closed.";
            _audit.Write(Admin.Audit, $"report {Ticket.Id} closed ({Ticket.TargetName} {Ticket.TargetSteamId})", Server, true, "");
        }
        else if (pick == "accept")
        {
            if (Server == null)
                return Toast(false, "Choose a server in the header first.");
            CommandResult unban = await _commands.RunAsync(Admin, Server, "moderate",
                new() { ["source"] = "simpleadmin", ["list"] = "bans", ["action"] = "unban", ["steamid"] = Ticket.TargetSteamId });
            if (!unban.Ok)
                return Partial("_Toast", unban);
            status = "ACCEPTED";
            done = $"Appeal accepted: {who} is unbanned.";
            _audit.Write(Admin.Audit, $"appeal {Ticket.Id} accepted ({Ticket.TargetName} {Ticket.TargetSteamId})", Server, true, "");
        }
        else if (pick == "reject")
        {
            status = "REJECTED";
            done = "Appeal rejected. They see it on their profile.";
            _audit.Write(Admin.Audit, $"appeal {Ticket.Id} rejected ({Ticket.TargetName} {Ticket.TargetSteamId})", Server, true, "");
        }
        else
            return Toast(false, "Choose what to do.");

        if (comment.Length > 0)
            _tickets.AddComment(Ticket.Id, new Tickets.Comment(Admin.Name, Admin.SteamId, DateTimeOffset.UtcNow, comment, show));
        if (!_tickets.SetStatus(Ticket.Id, status, Admin.Name))
            return Toast(false, "Someone already answered it.");
        Response.Headers["HX-Refresh"] = "true";
        return Toast(true, done);
    }

    public async Task<IActionResult> OnPostCommentAsync(string? text, bool show)
    {
        if (!await LoadAsync())
            return Forbid();
        text = (text ?? "").Trim();
        if (text.Length is < 2 or > Tickets.MaxComment)
            return Toast(false, $"A comment is 2 to {Tickets.MaxComment} characters.");
        if (!_tickets.AddComment(Ticket.Id, new Tickets.Comment(Admin.Name, Admin.SteamId, DateTimeOffset.UtcNow, text, show)))
            return Toast(false, "That one has 50 comments already.");
        // The writer hears about a reply they can see
        if (show && Ticket.SteamId != Admin.SteamId)
            _notices.Add(Ticket.SteamId, "ticket", Report ? "Staff replied to your report." : "Staff replied to your appeal.", "", "/profile#tickets");
        _audit.Write(Admin.Audit, $"{Ticket.Kind} {Ticket.Id} comment{(show ? " (shown to the writer)" : "")}", null, true, "");
        Response.Headers["HX-Refresh"] = "true";
        return Toast(true, "Comment saved.");
    }

    private PartialViewResult Toast(bool ok, string message) => Partial("_Toast", new CommandResult(ok, message));

    // The ticket and the viewer's rights: the appeals or reports flag on the server picked in the header
    private async Task<bool> LoadAsync()
    {
        if (_tickets.Find(Id) is not { } ticket)
            return false;
        Ticket = ticket;
        Server = Selection.Get(HttpContext, Admin, _store);
        Access access = await AccessAsync(Server);
        if (!access.Has(PanelPermissions.Need(Report ? "reports" : "appeals")))
            return false;
        if (Report && Server != null && _registry.Find(Server)?.Status?.Data?.FirstOrDefault(d => d.Id == "simpleadmin" && d.Available) is { } simpleAdmin)
            foreach (DataList list in simpleAdmin.Lists.Where(l => l.Id is "bans" or "mutes" or "warns"))
                foreach (DataAction action in list.Actions.Where(a => !a.Row && a.Fields.Contains("steamid") && a.Fields.All(f => f is "steamid" or "minutes" or "reason") && access.Has(a.Flag)))
                    Acts.Add(new PlayerModel.Act(list.Id, action.Id, action.Label, action.Fields.Contains("minutes")));
        return true;
    }
}
