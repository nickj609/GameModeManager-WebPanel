using GmmPanel.Agents;
using GmmPanel.Auth;
using GmmPanel.Pages;
using GmmPanel.Protocol;

namespace GmmPanel.Data;

// The lists the panel keeps itself, shown on Moderate like a plugin's: appeals and reports (Tickets), the chat log and
// everyone seen (History). Same shape as the agent's lists, so the same table draws them.
public sealed class PanelLists
{
    public const string Id = "panel";
    private const int PageSize = 25;
    private readonly History _history;
    private readonly Tickets _tickets;
    private readonly AgentRegistry _registry;

    public PanelLists(History history, Tickets tickets, AgentRegistry registry)
    {
        _registry = registry;
        _history = history;
        _tickets = tickets;
    }

    // Built each time: the flags come from the Panel page's permissions
    public static DataSource Source() => new()
    {
        Id = Id,
        Name = "The panel",
        Available = true,
        Lists = new()
        {
            new DataList
            {
                // Answered on the ticket's own page (Pages/Ticket): the list's Open button
                Id = "appeals", Label = "Appeals", Search = true, Flag = PanelPermissions.Need("appeals"), Filters = new() { "open", "all" }
            },
            new DataList
            {
                Id = "reports", Label = "Reports", Search = true, Flag = PanelPermissions.Need("reports"), Filters = new() { "open", "all" }
            },
            // Everyone on every server now (from their rosters), with Kick sent to the row's server
            new DataList
            {
                Id = "online", Label = "Who's on", Search = true, Flag = PanelPermissions.Need("players"),
                Actions = new() { new DataAction { Id = "kick", Label = "Kick", Row = true, Flag = PanelPermissions.Need("kick"), Fields = new() { "steamid", "where" } } }
            },
            new DataList { Id = "chat", Label = "Chat log", Search = true, Flag = PanelPermissions.Need("chat") },
            new DataList { Id = "seen", Label = "Player history", Search = true, Flag = PanelPermissions.Need("history") }
        }
    };

    public CommandResult Page(string list, string filter, string search, int page)
    {
        page = Math.Clamp(page, 1, 10000);
        search = search.Trim();
        if (search.Length > 64)
            return new CommandResult(false, "Search up to 64 characters.");
        DataPage data = list switch
        {
            "appeals" => Tickets("appeals", filter, search, page),
            "reports" => Tickets("reports", filter, search, page),
            "online" => Online(search, page),
            "chat" => Chat(search, page),
            "seen" => Seen(search, page),
            _ => new DataPage()
        };
        return list is "appeals" or "reports" or "online" or "chat" or "seen" ? new CommandResult(true, "", Data: data) : new CommandResult(false, "That list isn't offered.");
    }

    private DataPage Tickets(string list, string filter, string search, int page)
    {
        bool appeals = list == "appeals";
        var rows = _tickets.List(appeals ? "appeal" : "report", filter == "all" ? "all" : "open", search);
        var columns = appeals
            ? new List<DataColumn> { Col("name", "Player"), Col("steamid", "SteamID", "steamid"), Col("text", "Appeal"), Col("created", "Sent", "time"), Col("status", "Status", "status"), Col("by", "Answered by") }
            : new List<DataColumn> { Col("name", "Reported"), Col("steamid", "SteamID", "steamid"), Col("server", "Server"), Col("text", "Reason"), Col("from", "From"), Col("created", "Sent", "time"), Col("status", "Status", "status"), Col("by", "Answered by") };
        return new DataPage
        {
            Columns = columns,
            Rows = rows.Skip((page - 1) * PageSize).Take(PageSize).Select(t => new Dictionary<string, string?>
            {
                ["id"] = t.Id,
                ["name"] = t.TargetName,
                ["steamid"] = t.TargetSteamId,
                ["server"] = t.Server,
                ["text"] = t.Text,
                ["from"] = t.Name,
                ["created"] = Format.Time(t.Created),
                ["status"] = t.Status,
                ["by"] = t.HandledBy,
                ["comments"] = t.Comments.Count.ToString()
            }).ToList(),
            Page = page,
            PageSize = PageSize,
            Total = rows.Count
        };
    }

    private DataPage Online(string search, int page)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var rows = _registry.All().Where(v => v.Online && v.Status?.Roster != null)
            .SelectMany(v => v.Status!.Roster!.Select(p => (Server: v.Name, Player: p, Since: _history.OnlineOn(v.Name).TryGetValue(p.SteamId, out DateTimeOffset s) ? s : (DateTimeOffset?)null)))
            .Where(x => search.Length == 0 || x.Player.SteamId == search || x.Player.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || x.Server.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Server, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Player.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new DataPage
        {
            Columns = new() { Col("name", "Player"), Col("steamid", "SteamID", "steamid"), Col("where", "Server"), Col("team", "Team"), Col("for", "On for") },
            Rows = rows.Skip((page - 1) * PageSize).Take(PageSize).Select(x => new Dictionary<string, string?>
            {
                ["name"] = x.Player.Name,
                ["steamid"] = x.Player.SteamId,
                ["where"] = x.Server,
                ["team"] = x.Player.Team,
                ["for"] = x.Since is { } since ? Format.Span(now - since) : ""
            }).ToList(),
            Page = page,
            PageSize = PageSize,
            Total = rows.Count
        };
    }

    private DataPage Chat(string search, int page)
    {
        var (rows, total) = _history.Chat(null, History.IsSteamId(search) ? search : null, History.IsSteamId(search) ? "" : search, page, PageSize);
        return new DataPage
        {
            Columns = new() { Col("at", "When", "time"), Col("server", "Server"), Col("name", "Player"), Col("steamid", "SteamID", "steamid"), Col("text", "Said") },
            Rows = rows.Select(l => new Dictionary<string, string?>
            {
                ["at"] = Format.Time(l.At),
                ["server"] = l.Server,
                ["name"] = l.Name,
                ["steamid"] = l.SteamId,
                ["text"] = (l.TeamOnly ? $"({l.Team}) " : "") + l.Text
            }).ToList(),
            Page = page,
            PageSize = PageSize,
            Total = total
        };
    }

    private DataPage Seen(string search, int page)
    {
        var (rows, total) = _history.Players(search, page, PageSize);
        return new DataPage
        {
            Columns = new() { Col("name", "Player"), Col("steamid", "SteamID", "steamid"), Col("last", "Last seen", "time"), Col("first", "First seen", "time"), Col("played", "Played", "number"), Col("status", "Now", "status") },
            Rows = rows.Select(p => new Dictionary<string, string?>
            {
                ["name"] = p.Name,
                ["steamid"] = p.SteamId,
                ["last"] = Format.Time(p.LastSeen),
                ["first"] = Format.Time(p.FirstSeen),
                ["played"] = Format.Hours(_history.Seconds(p)),
                ["status"] = _history.OnlineNow(p.SteamId) is { } on ? "ONLINE" : null
            }).ToList(),
            Page = page,
            PageSize = PageSize,
            Total = total
        };
    }

    private static DataColumn Col(string key, string label, string kind = "text") => new() { Key = key, Label = label, Kind = kind };
}
