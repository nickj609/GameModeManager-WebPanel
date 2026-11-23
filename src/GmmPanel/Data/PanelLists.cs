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

    // allow: the servers whose rows the viewer may see (they have the list's permission there); null for all. Appeals
    // aren't per server (a ban counts everywhere), so they aren't filtered.
    // onlyServer: Moderate's server picker (chat, player history, reports); range: the chat log's time range (hour, day, week)
    public CommandResult Page(string list, string filter, string search, int page, Func<string, bool>? allow = null, string sort = "", string onlyServer = "", string range = "")
    {
        page = Math.Clamp(page, 1, 10000);
        search = search.Trim();
        if (search.Length > 64)
            return new CommandResult(false, "Search up to 64 characters.");
        // A picked server narrows what the viewer may see, never widens it
        if (onlyServer.Length > 0)
        {
            Func<string, bool>? may = allow;
            allow = s => s.Equals(onlyServer, StringComparison.OrdinalIgnoreCase) && (may == null || may(s));
        }
        DateTimeOffset? since = range switch { "hour" => DateTimeOffset.UtcNow.AddHours(-1), "day" => DateTimeOffset.UtcNow.AddDays(-1), "week" => DateTimeOffset.UtcNow.AddDays(-7), _ => null };
        DataPage data = list switch
        {
            "appeals" => Tickets("appeals", filter, search, page, null, sort),
            "reports" => Tickets("reports", filter, search, page, allow, sort),
            "online" => Online(search, page, allow, sort),
            "chat" => Chat(search, page, allow, since),
            "seen" => Seen(search, page, allow, sort, onlyServer),
            _ => new DataPage()
        };
        return list is "appeals" or "reports" or "online" or "chat" or "seen" ? new CommandResult(true, "", Data: data) : new CommandResult(false, "That list isn't offered.");
    }

    private DataPage Tickets(string list, string filter, string search, int page, Func<string, bool>? allow, string sort)
    {
        bool appeals = list == "appeals";
        var rows = _tickets.List(appeals ? "appeal" : "report", filter == "all" ? "all" : "open", search);
        if (allow != null)
            rows = rows.Where(t => allow(t.Server)).ToList();
        // Open reports: one row per reported player (the newest, with how many), as a moderation queue groups them; the
        // report's page lists the others and can close them with it
        var repeats = new Dictionary<string, int>();
        if (!appeals && filter != "all")
        {
            rows = rows.GroupBy(t => t.TargetSteamId).Select(g => { repeats[g.First().Id] = g.Count(); return g.First(); }).ToList();
        }
        // A header's order ("-key" = descending); else as Tickets keeps them
        bool desc = sort.StartsWith('-');
        Func<Tickets.Ticket, object?>? by = sort.TrimStart('-') switch
        {
            "name" => t => t.TargetName.ToLowerInvariant(),
            "server" => t => t.Server.ToLowerInvariant(),
            "from" => t => t.Name.ToLowerInvariant(),
            "created" => t => t.Created,
            "status" => t => t.Status,
            "reason" => t => t.Reason,
            _ => null
        };
        if (by != null)
            rows = (desc ? rows.OrderByDescending(by) : rows.OrderBy(by)).ToList();
        var columns = appeals
            ? new List<DataColumn> { Col("name", "Player", sortable: true), Col("steamid", "SteamID", "steamid"), Col("text", "Appeal"), Col("created", "Sent", "time", true), Col("status", "Status", "status", true), Col("by", "Answered by") }
            : new List<DataColumn> { Col("name", "Reported", sortable: true), Col("steamid", "SteamID", "steamid"), Col("server", "Server", sortable: true), Col("reason", "Type", sortable: true), Col("text", "What happened"), Col("from", "From", sortable: true), Col("created", "Sent", "time", true), Col("status", "Status", "status", true), Col("by", "Answered by") };
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
                ["created.at"] = t.Created.ToUnixTimeSeconds().ToString(),   // The moment, for "ago" (_DataTable)
                ["status"] = t.Status,
                // Open and claimed: who's looking at it, in the answered-by column
                ["by"] = t.Status == "OPEN" && t.ClaimedBy.Length > 0 ? $"Looking: {t.ClaimedBy}" : t.HandledBy,
                ["comments"] = t.Comments.Count.ToString(),
                ["reason"] = Data.Tickets.ReasonLabel(t.Reason),
                ["repeat"] = repeats.TryGetValue(t.Id, out int count) && count > 1 ? count.ToString() : null
            }).ToList(),
            Page = page,
            PageSize = PageSize,
            Total = rows.Count
        };
    }

    private DataPage Online(string search, int page, Func<string, bool>? allow, string sort)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var rows = _registry.All().Where(v => v.Online && v.Status?.Roster != null && (allow == null || allow(v.Name)))
            .SelectMany(v => v.Status!.Roster!.Select(p => (Server: v.Name, Player: p, Since: _history.OnlineOn(v.Name).TryGetValue(p.SteamId, out DateTimeOffset s) ? s : (DateTimeOffset?)null)))
            .Where(x => search.Length == 0 || x.Player.SteamId == search || x.Player.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || x.Server.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Server, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Player.Name, StringComparer.OrdinalIgnoreCase).ToList();
        // A header's order ("-key" = descending); "for" sorts by how long they've been on
        bool desc = sort.StartsWith('-');
        Func<(string Server, RosterEntry Player, DateTimeOffset? Since), object?>? by = sort.TrimStart('-') switch
        {
            "name" => x => x.Player.Name.ToLowerInvariant(),
            "where" => x => x.Server.ToLowerInvariant(),
            "team" => x => x.Player.Team,
            "for" => x => x.Since is { } since ? now - since : TimeSpan.Zero,
            _ => null
        };
        if (by != null)
            rows = (desc ? rows.OrderByDescending(by) : rows.OrderBy(by)).ToList();
        return new DataPage
        {
            Columns = new() { Col("name", "Player", sortable: true), Col("steamid", "SteamID", "steamid"), Col("where", "Server", sortable: true), Col("team", "Team", sortable: true), Col("for", "On for", "number", true) },
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

    private DataPage Chat(string search, int page, Func<string, bool>? allow, DateTimeOffset? since)
    {
        var (rows, total) = _history.Chat(null, History.IsSteamId(search) ? search : null, History.IsSteamId(search) ? "" : search, page, PageSize, allow, since);
        return new DataPage
        {
            Columns = new() { Col("at", "When", "time"), Col("server", "Server"), Col("name", "Player"), Col("steamid", "SteamID", "steamid"), Col("text", "Said") },
            Rows = rows.Select(l => new Dictionary<string, string?>
            {
                ["at"] = Format.Time(l.At),
                ["at.at"] = l.At.ToUnixTimeSeconds().ToString(),   // The moment, for "ago" (_DataTable)
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

    private DataPage Seen(string search, int page, Func<string, bool>? allow, string sort, string onlyServer)
    {
        var (rows, total) = _history.Players(search, page, PageSize, allow, sort, onlyServer);
        return new DataPage
        {
            Columns = new() { Col("name", "Player", sortable: true), Col("steamid", "SteamID", "steamid"), Col("last", "Last seen", "time", true), Col("first", "First seen", "time", true), Col("played", "Played", "number", true), Col("status", "Now", "status") },
            Rows = rows.Select(p => new Dictionary<string, string?>
            {
                ["name"] = p.Name,
                ["steamid"] = p.SteamId,
                ["last"] = Format.Time(p.LastSeen),
                ["last.at"] = p.LastSeen.ToUnixTimeSeconds().ToString(),   // The moment, for "ago" (_DataTable)
                ["first"] = Format.Time(p.FirstSeen),
                ["first.at"] = p.FirstSeen.ToUnixTimeSeconds().ToString(),   // The moment, for "ago" (_DataTable)
                ["played"] = Format.Hours(_history.Seconds(p)),
                ["status"] = _history.OnlineNow(p.SteamId) is { } on ? "ONLINE" : null
            }).ToList(),
            Page = page,
            PageSize = PageSize,
            Total = total
        };
    }

    private static DataColumn Col(string key, string label, string kind = "text", bool sortable = false) => new() { Key = key, Label = label, Kind = kind, Sortable = sortable };
}
