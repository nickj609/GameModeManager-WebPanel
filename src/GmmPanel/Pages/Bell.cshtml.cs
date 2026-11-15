using GmmPanel.Auth;
using GmmPanel.Data;
using Microsoft.AspNetCore.Mvc;

namespace GmmPanel.Pages;

// The header's bell, for everyone signed in, every minute: their own unread notices (Notices.cs checks for new ones), and
// for staff the open appeals and reports they can answer
public class BellModel : PanelPage
{
    private readonly Tickets _tickets;
    private readonly PanelStore _store;
    private readonly Notices _notices;

    public BellModel(Tickets tickets, PanelStore store, Notices notices)
    {
        _tickets = tickets;
        _store = store;
        _notices = notices;
    }

    public int Unread { get; private set; }
    public List<Notices.Notice> Items { get; private set; } = new();
    public int Appeals { get; private set; } = -1;
    public int Reports { get; private set; } = -1;

    public async Task<IActionResult> OnGetAsync()
    {
        if (Viewer == null)
            return new EmptyResult();
        await _notices.CheckAsync(Viewer, await HighestAsync(this, _store));
        Unread = _notices.Unread(Viewer.SteamId);
        Items = _notices.For(Viewer.SteamId).Take(12).ToList();
        Access access = await AccessAsync(Selection.Get(HttpContext, Viewer, _store));
        if (access.Has(PanelPermissions.Need("appeals")))
            Appeals = _tickets.Open("appeal");
        if (access.Has(PanelPermissions.Need("reports")))
            Reports = _tickets.Open("report");
        return Page();
    }

    // Opening the menu marks them read
    public IActionResult OnPostRead()
    {
        if (Viewer != null)
            _notices.MarkRead(Viewer.SteamId);
        return new NoContentResult();
    }

    // Their highest role on any server (Admin, then a moderator group, then VIP, then Player), so switching servers isn't news
    public static async Task<Access> HighestAsync(PanelPage page, PanelStore store)
    {
        Access best = Access.Visitor;
        int Rank(Access a) => a.RoleClass switch { "admin" => 4, "moderator" => 3, "vip" => 2, "player" => 1, _ => 0 };
        foreach (ServerEntry server in store.Current.Servers.Where(s => page.Viewer!.CanUse(s.Name)))
        {
            Access a = await page.AccessFor(server.Name);
            if (Rank(a) > Rank(best))
                best = a;
        }
        return best;
    }
}
