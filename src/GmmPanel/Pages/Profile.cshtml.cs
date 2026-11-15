using static GmmPanel.Data.Lang;
using GmmPanel.Agents;
using GmmPanel.Auth;
using GmmPanel.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GmmPanel.Pages;

// My profile: the signed-in person's own rows from CS2-SimpleAdmin (their bans, mutes and warnings), what
// they may do on each server, and their appeals and reports (they write them here; staff answer under Moderate)
[EnableRateLimiting("public")]
public class ProfileModel : PanelPage
{
    private static readonly (string Source, string List, string Label)[] Lists =
    {
        ("simpleadmin", "bans", "Bans"),
        ("simpleadmin", "mutes", "Mutes & gags"),
        ("simpleadmin", "warns", "Warnings")
    };

    private readonly PublicData _data;
    private readonly PanelStore _store;
    private readonly Tickets _tickets;
    private readonly History _history;
    private readonly Discord _discord;
    private readonly AuditLog _audit;
    private readonly UserPrefs _prefs;

    public ProfileModel(PublicData data, PanelStore store, Tickets tickets, History history, Discord discord, AuditLog audit, UserPrefs prefs)
    {
        _prefs = prefs;
        _data = data;
        _store = store;
        _tickets = tickets;
        _history = history;
        _discord = discord;
        _audit = audit;
    }

    public List<(string Label, CommandResult Result)> Sections { get; } = new();
    public List<(string Server, Access Access)> Servers { get; } = new();
    public List<Tickets.Ticket> Mine { get; private set; } = new();
    public bool Banned { get; private set; }
    public string? Message { get; private set; }
    public bool MessageOk { get; private set; }
    public string? DiscordName => _prefs.Discord(Admin.SteamId);
    public long Played { get; private set; }
    public long PlayedThisWeek { get; private set; }
    public string? Country { get; private set; }   // The country GameModeManager last looked up for them (Player countries)

    // How many of a list's rows are still in force (bans, mutes and warnings that are ACTIVE)
    public int Active(string label) => Sections.FirstOrDefault(s => s.Label == label).Result?.Data?.Rows
        .Count(r => string.Equals(r.GetValueOrDefault("status"), "ACTIVE", StringComparison.OrdinalIgnoreCase)) ?? 0;

    // Servers grouped by what the person may do there, so identical rows read as one: the scope ("All servers (6)"), the
    // permissions ("Full access · panel admin", "Flags: @css/ban @css/kick · Group: #moderator") and the role tag
    public List<(List<string> Servers, Access Access, string Detail)> AccessGroups => Servers
        .Select(s => (s.Server, s.Access, Detail: s.Access.PanelAdmin ? T("Full access · panel admin")
            : string.Join(" · ", new[]
            {
                s.Access.Flags.Count > 0 ? F("Flags: {0}", string.Join(" ", s.Access.Flags.OrderBy(f => f))) : T("No admin flags"),
                s.Access.Groups.Count > 0 ? F("Group: {0}", string.Join(" ", s.Access.Groups)) : null
            }.Where(part => part != null))))
        .GroupBy(s => (s.Access.RoleName, s.Access.RoleClass, s.Detail))
        .Select(g => (g.Select(x => x.Server).ToList(), g.First().Access, g.Key.Detail))
        .ToList();
    public bool ProfileHidden => _prefs.IsHidden(Admin.SteamId);

    // Hide or show their public profile (/u/<steamid>)
    public IActionResult OnPostPublic(bool hide)
    {
        _prefs.SetHidden(Admin.SteamId, hide);
        TempData["Sent"] = hide ? T("Your public profile is hidden.") : T("Your public profile is visible again.");
        return RedirectToPage();
    }
    public string? Avatar { get; private set; }

    // Their Discord username, shown on their profile card (and to staff on their player page). Empty removes it.
    public async Task<IActionResult> OnPostDiscordAsync(string? discord)
    {
        discord = (discord ?? "").Trim().TrimStart('@');
        if (discord.Length > 0 && !UserPrefs.IsDiscordName(discord))
        {
            await LoadAsync();
            Fail("A Discord username is 2 to 32 characters: letters, digits, _ and dots.");
            return Page();
        }
        _prefs.SetDiscord(Admin.SteamId, discord);
        TempData["Sent"] = discord.Length > 0 ? F("Discord set to {0}.", discord) : T("Discord removed.");
        return RedirectToPage();
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        // After a sent appeal or report (post, then redirect, so a reload doesn't send it twice)
        if (TempData["Sent"] is string sent)
            Done(sent);
    }

    public async Task<IActionResult> OnPostAppealAsync(string? text)
    {
        if (!FeatureOn("appeals"))
            return NotFound();
        await LoadAsync();
        text = (text ?? "").Trim();
        if (!Banned)
            Fail("You have no active ban to appeal.");
        else if (text.Length is < 10 or > Tickets.MaxText)
            Fail($"Say why, in 10 to {Tickets.MaxText} characters.");
        else if (_tickets.Add(new Tickets.Ticket { Kind = "appeal", SteamId = Admin.SteamId, Name = Admin.Name, TargetSteamId = Admin.SteamId, TargetName = Admin.Name, Text = text }) is string refused)
            Fail(refused);
        else
        {
            Done("Appeal sent. Staff answer it here.");
            _audit.Write(Admin.Audit, "appeal sent", null, true, "");
            _discord.Ticket("New ban appeal", Admin.Name, Admin.SteamId, text);
            TempData["Sent"] = Message;
            return RedirectToPage(null, null, null, "tickets");
        }
        Mine = _tickets.By(Admin.SteamId).Where(t => t.SteamId == Admin.SteamId).ToList();
        return Page();
    }

    // The writer's reply on their own open appeal or report: kept as a comment staff see on the ticket's page
    public async Task<IActionResult> OnPostReplyAsync(string? id, string? text)
    {
        text = (text ?? "").Trim();
        Tickets.Ticket? ticket = _tickets.Find(id ?? "");
        if (ticket == null || ticket.SteamId != Admin.SteamId)
            return NotFound();
        if (ticket.Status != "OPEN")
            Fail("It's been answered, so it takes no more replies.");
        else if (text.Length is < 2 or > Tickets.MaxComment)
            Fail($"Write 2 to {Tickets.MaxComment} characters.");
        else if (!_tickets.AddComment(ticket.Id, new Tickets.Comment(Admin.Name, Admin.SteamId, DateTimeOffset.UtcNow, text, true)))
            Fail("It has too many replies already.");
        else
        {
            _audit.Write(Admin.Audit, $"{ticket.Kind} reply", null, true, ticket.Id);
            _discord.Ticket(ticket.Kind == "appeal" ? "Reply on a ban appeal" : "Reply on a report", Admin.Name, Admin.SteamId, text);
            TempData["Sent"] = "Reply sent.";
            return RedirectToPage(null, null, null, "tickets");
        }
        await LoadAsync();
        Mine = _tickets.By(Admin.SteamId).Where(t => t.SteamId == Admin.SteamId).ToList();
        return Page();
    }

    private void Fail(string message) { Message = message; MessageOk = false; }
    private void Done(string message) { Message = message; MessageOk = true; }

    private async Task LoadAsync()
    {
        foreach (var (source, list, label) in Lists.Where(l => _data.Offers(l.Source, l.List)))
            Sections.Add((label, await _data.OwnAsync(source, list, Admin.SteamId)));
        Banned = Sections.Any(s => s.Label == "Bans" && s.Result.Data?.Rows.Any(r => string.Equals(r.GetValueOrDefault("status"), "ACTIVE", StringComparison.OrdinalIgnoreCase)) == true);
        foreach (ServerEntry server in _store.Current.Servers.Where(s => Admin.CanUse(s.Name)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            Servers.Add((server.Name, await AccessAsync(server.Name)));
        Avatar = await HttpContext.RequestServices.GetRequiredService<SteamProfile>().AvatarAsync(Admin.SteamId);
        Mine = _tickets.By(Admin.SteamId).Where(t => t.SteamId == Admin.SteamId).ToList();
        if (_history.Player(Admin.SteamId) is { } me)
        {
            Played = _history.Seconds(me);
            PlayedThisWeek = _history.WeekSeconds(me);
            Country = me.Country;
        }
    }
}
