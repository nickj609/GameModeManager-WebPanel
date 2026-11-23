using System.Text.Json;
using GmmPanel.Agents;
using GmmPanel.Auth;
using GmmPanel.Commands;
using GmmPanel.Data;
using GmmPanel.Protocol;
using Microsoft.AspNetCore.Mvc;

namespace GmmPanel.Pages;

// Moderate: bans, mutes, warnings, appeals and reports; who's on, the chat log and everyone seen; and the admin lists.
// The panel has no database access: the selected server's GameModeManager reads CS2-SimpleAdmin's own database (allow-listed, read-only queries) and writes through the plugin's own
// console commands. Appeals, reports, the chat log and player history are the panel's own (Data/PanelLists.cs).
public class ModerateModel : PanelPage
{
    private static readonly string[] ArgNames = { "source", "list", "action", "steamid", "minutes", "reason", "group", "amount", "name", "flags", "immunity", "page", "search", "filter", "id", "where" };

    // The page's sections, and which lists can fill each, in order of preference
    public sealed record Tab(string Id, string Label, string Icon, string Group, (string Source, string List)[] Lists, string Empty);

    public static readonly Tab[] Tabs =
    {
        new("bans", "Bans", "ban", "Moderation", new[] { ("simpleadmin", "bans") },
            "Bans come from CS2-SimpleAdmin's database. It isn't installed on this server, or its database can't be read."),
        new("mutes", "Mutes & gags", "mute", "Moderation", new[] { ("simpleadmin", "mutes") },
            "Mutes and gags come from CS2-SimpleAdmin's database. It isn't installed on this server, or its database can't be read."),
        new("warns", "Warnings", "gavel", "Moderation", new[] { ("simpleadmin", "warns") },
            "Warnings come from CS2-SimpleAdmin's database. It isn't installed on this server, or its database can't be read."),
        new("appeals", "Appeals", "appeal", "Moderation", new[] { (PanelLists.Id, "appeals") }, ""),
        new("reports", "Reports", "flag", "Moderation", new[] { (PanelLists.Id, "reports") }, ""),
        new("live", "Who's on", "players", "Players", new[] { (PanelLists.Id, "online") }, ""),
        new("chat", "Chat log", "chat", "Players", new[] { (PanelLists.Id, "chat") }, ""),
        new("seen", "Player history", "clock", "Players", new[] { (PanelLists.Id, "seen") }, ""),
        // CounterStrikeSharp's own lists (admins.json, admin_groups.json and CS2-SimpleAdmin's, which it loads into them), with
        // SimpleAdmin's add and remove when it's installed: one place for every admin
        new("admins", "Server admins", "shield", "Admins", new[] { ("server", "admins") }, ""),
        new("groups", "Server groups", "players", "Admins", new[] { ("server", "groups") }, ""),
    };

    private readonly PanelStore _store;
    private readonly AgentRegistry _registry;
    private readonly CommandService _commands;
    private readonly PanelLists _lists;
    private readonly Tickets _tickets;
    private readonly History _history;
    private readonly AuditLog _audit;

    public ModerateModel(PanelStore store, AgentRegistry registry, CommandService commands, PanelLists lists, Tickets tickets, History history, AuditLog audit)
    {
        _audit = audit;
        _store = store;
        _registry = registry;
        _commands = commands;
        _lists = lists;
        _tickets = tickets;
        _history = history;
    }

    public ServerView? View { get; private set; }

    // The server the lists are read through (the header's switcher)
    public string? Server => View?.Name;

    // The server's lists, its own ("This server": the Panel page's flags apply) and the panel's
    public List<DataSource> Sources { get; private set; } = new();

    // A tab's list: the first of its candidates that's installed (available or not), so its reason can be shown
    public (DataSource Source, DataList? List)? Find(Tab tab)
    {
        foreach (var (sourceId, listId) in tab.Lists)
        {
            DataSource? source = Sources.FirstOrDefault(d => d.Id == sourceId);
            if (source == null)
                continue;
            DataList? list = source.Lists.FirstOrDefault(l => l.Id == listId);
            if (list != null || !source.Available)
                return (source, list);
        }
        return null;
    }

    // The viewer's admin flags on the server the lists are read through
    public Access Access { get; private set; } = Access.Visitor;

    // Moderation tabs show (with why they're empty) unless the viewer's flags rule the list out; the others only when their
    // list exists and the flags allow reading it
    public IEnumerable<Tab> Shown => Tabs.Where(tab => Find(tab) is { } found
        ? found.List == null || Access.Has(found.List.Flag)
        : tab.Group == "Moderation" && Access.IsStaff);

    public bool CanDo(DataAction action) => Access.Has(action.Flag);

    // Open appeals and reports, for the rail
    public int OpenCount(string tab) => tab switch { "appeals" => _tickets.Open("appeal"), "reports" => _tickets.Open("report"), _ => 0 };

    // Filled for the _DataTable partial
    public DataSource? Source { get; private set; }
    public DataList? List { get; private set; }
    public CommandResult? Result { get; private set; }
    public string Search { get; private set; } = "";
    public string Filter { get; private set; } = "";
    public string Sort { get; private set; } = "";
    public string OnlyServer { get; private set; } = "";   // The panel's lists: one server (chat, player history, reports)
    public string Range { get; private set; } = "";        // The chat log: hour, day or week
    // The servers the viewer may pick for those
    public List<string> PickableServers => _store.Current.Servers.Where(s => Admin.CanUse(s.Name)).Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();      // A sortable header's order: "key", or "-key" for descending

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        if (Server != null)
            Selection.Set(HttpContext, Server);
        // Moderate is for people with some admin flag on that server (or panel admins)
        return View == null || Access.IsStaff ? Page() : LocalRedirect("/denied");
    }

    // One page of a list: the panel's own, or through the agent
    public async Task<IActionResult> OnGetListAsync()
    {
        if (!await LoadAsync())
            return Content("");
        Result = await PageOfAsync(int.TryParse(Request.Query["page"], out int page) ? page : 1);
        return Partial("_DataTable", this);
    }

    // The list as shown (its search, filters and sort), every page of it up to 2,000 rows, as CSV or JSON: evidence or
    // records kept outside the panel. Logged, as the audit log's export is.
    public async Task<IActionResult> OnGetExportAsync(string? format)
    {
        if (!await LoadAsync())
            return NotFound();
        CommandResult first = await PageOfAsync(1);
        if (!first.Ok || first.Data is not { } data || List == null)
            return Content(first.Message);
        var rows = data.Rows.ToList();
        int pages = Math.Max(1, (data.Total + data.PageSize - 1) / Math.Max(1, data.PageSize));
        for (int page = 2; page <= pages && rows.Count < 2000; page++)
        {
            CommandResult next = await PageOfAsync(page);
            if (!next.Ok || next.Data == null || next.Data.Rows.Count == 0)
                break;
            rows.AddRange(next.Data.Rows);
        }
        rows = rows.Take(2000).ToList();
        var columns = data.Columns;
        string filters = string.Join(", ", new[] { Search.Length > 0 ? $"\"{Search}\"" : null, Filter.Length > 0 ? Filter : null, OnlyServer.Length > 0 ? OnlyServer : null, Range.Length > 0 ? Range : null }.Where(f => f != null));
        _audit.Write(Admin.Audit, $"list export {(format == "csv" ? "csv" : "json")} {List.Id} ({(filters.Length > 0 ? filters + ", " : "")}{rows.Count} rows)", Server, true, "");
        string stamp = DateTime.Now.ToString("yyyy-MM-dd");
        string name = $"{List.Id}-{stamp}";
        if (format == "csv")
        {
            var csv = new System.Text.StringBuilder(string.Join(",", columns.Select(c => Cell(c.Label))) + "\r\n");
            foreach (var row in rows)
                csv.Append(string.Join(",", columns.Select(c => Cell(row.GetValueOrDefault(c.Key) ?? "")))).Append("\r\n");
            // A byte-order mark, so Excel reads it as UTF-8
            return File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8", $"{name}.csv");
        }
        var json = rows.Select(row => columns.ToDictionary(c => c.Label, c => row.GetValueOrDefault(c.Key) ?? "")).ToList();
        return File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(json, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), "application/json", $"{name}.json");
    }

    // A CSV cell: quoted, quotes doubled, and a leading = + - @ made harmless so a spreadsheet doesn't run it as a formula
    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    // One page of the list the query names, with its filters: the panel's own, or through the agent
    private async Task<CommandResult> PageOfAsync(int number)
    {
        var args = Args(Request.Query);
        Source = Sources.FirstOrDefault(d => d.Id == args.GetValueOrDefault("source"));
        List = Source?.Lists.FirstOrDefault(l => l.Id == args.GetValueOrDefault("list"));
        Search = args.GetValueOrDefault("search") ?? "";
        Filter = args.GetValueOrDefault("filter") ?? "";
        // Panel lists only, read here (never sent to GameModeManager): a server they may see, a known range, the sort
        OnlyServer = Request.Query["srv"].ToString() is { Length: > 0 } srv && PickableServers.Contains(srv, StringComparer.OrdinalIgnoreCase) ? srv : "";
        Range = Request.Query["range"].ToString() is "hour" or "day" or "week" ? Request.Query["range"].ToString() : "";
        Sort = (Request.Query["sort"].ToString() is { Length: <= 32 } sort && System.Text.RegularExpressions.Regex.IsMatch(sort, "^-?[a-z]*$")) ? sort : "";
        args["page"] = number.ToString();
        CommandResult result;
        if (Source?.Id == PanelLists.Id)
        {
            result = List == null ? new CommandResult(false, "That list isn't offered.")
                : !Access.Has(List.Flag) ? new CommandResult(false, $"You need the {List.Flag} admin flag on {Server} for that.")
                : _lists.Page(List.Id, Filter, Search, number, await ServersWithAsync(List.Flag), Sort, OnlyServer, Range);
        }
        else
        {
            result = await _commands.RunAsync(Admin, View!.Name, "query", args);
            if (Source?.Id == "server" && List?.Id is "admins" or "groups" && result.Data != null)
                await MarkSimpleAdminAsync(List.Id, result.Data);
        }
        return result;
    }

    // Which server admins and groups CS2-SimpleAdmin keeps ("from": SimpleAdmin, else the CounterStrikeSharp files), with
    // SimpleAdmin's names; admins.json doesn't name its admins to GameModeManager, so names the panel has seen fill the rest
    private async Task MarkSimpleAdminAsync(string list, DataPage page)
    {
        var kept = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string key = list == "admins" ? "steamid" : "group";
        if (Sources.Any(s => s.Id == "simpleadmin" && s.Available))
            for (int number = 1; number <= 8; number++)
            {
                CommandResult sa = await _commands.RunAsync(Admin, View!.Name, "query", new() { ["source"] = "simpleadmin", ["list"] = list, ["page"] = number.ToString() });
                if (sa.Data == null)
                    break;
                foreach (var row in sa.Data.Rows)
                    if (row.GetValueOrDefault(key) is { Length: > 0 } id)
                        kept[id] = row.GetValueOrDefault("name");
                if (number * sa.Data.PageSize >= sa.Data.Total)
                    break;
            }
        page.Columns.Add(new DataColumn { Key = "from", Label = "Kept in" });
        foreach (var row in page.Rows)
        {
            string id = row.GetValueOrDefault(key) ?? "";
            row["from"] = kept.ContainsKey(id) ? "SimpleAdmin" : list == "admins" ? "admins.json" : "admin_groups.json";
            if (list == "admins" && string.IsNullOrEmpty(row.GetValueOrDefault("name")))
                row["name"] = kept.GetValueOrDefault(id) is { Length: > 0 } name ? name : _history.Player(id)?.Name ?? "";
        }
    }

    // A write (ban, unban, mute, kick, an appeal's answer ...): a toast, then the open list reloads
    public async Task<IActionResult> OnPostModerateAsync()
    {
        if (!await LoadAsync())
            return NotFound();
        var args = Args(Request.Form);
        // Server admins' and groups' SimpleAdmin actions go to SimpleAdmin's own list of the same name
        if (args.GetValueOrDefault("source") == "server" && args.GetValueOrDefault("action") is { } act && act.StartsWith("sa-", StringComparison.Ordinal))
        {
            args["source"] = "simpleadmin";
            args["action"] = act[3..];
        }
        CommandResult result = args.GetValueOrDefault("source") == PanelLists.Id
            ? await AnswerAsync(args)
            : await _commands.RunAsync(Admin, View!.Name, "moderate", args);
        if (result.Ok)
            Response.Headers["HX-Trigger"] = JsonSerializer.Serialize(new { refreshList = true });
        return Partial("_Toast", result);
    }

    // The panel's own lists' writes: only Kick from Who's on (appeals and reports are answered on their own page, Pages/Ticket)
    private async Task<CommandResult> AnswerAsync(Dictionary<string, string> args)
    {
        if (args.GetValueOrDefault("list") != "online")
            return new CommandResult(false, "That isn't there any more.");
        string where = args.GetValueOrDefault("where") ?? "";
        if (_store.Current.Servers.All(s => !s.Name.Equals(where, StringComparison.OrdinalIgnoreCase)))
            return new CommandResult(false, "That server isn't on the panel.");
        return await _commands.RunAsync(Admin, where, "moderate",
            new() { ["source"] = "server", ["list"] = "players", ["action"] = "kick", ["steamid"] = args.GetValueOrDefault("steamid") ?? "" });
    }

    private static Dictionary<string, string> Args(IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> values)
    {
        var args = new Dictionary<string, string>();
        foreach (var (key, value) in values)
            if (ArgNames.Contains(key) && !string.IsNullOrEmpty(value))
                args[key] = value.ToString().Trim();
        return args;
    }

    private async Task<bool> LoadAsync()
    {
        string? server = Selection.Get(HttpContext, Admin, _store);
        View = server == null ? null : _registry.Get(server);
        Access = await AccessAsync(server);
        var data = View?.Status?.Data ?? new();
        DataSource? simpleAdmin = data.FirstOrDefault(d => d.Id == "simpleadmin" && d.Available);
        // The server's own lists and CS2-SimpleAdmin's (an older GameModeManager may still report other plugins: left out)
        Sources = data.Where(source => source.Id is "server" or "simpleadmin").Select(source => WithPanelFlags(source, simpleAdmin))
            // Offline: the server's own lists say why they're empty; the panel's own (reports, chat, history) still work
            .Select(source => View is { Online: false } ? new DataSource { Id = source.Id, Name = source.Name, Available = false, Lists = source.Lists,
                Reason = $"{ServerNames.Label(View.Name)} isn't connected: this list comes back when it is." } : source)
            .Append(PanelLists.Source()).ToList();
        return View != null;
    }

    // "This server"'s lists use the Panel page's flags (live players, kick, server admins), as CommandFlags does
    // Server admins and groups also carry CS2-SimpleAdmin's add and remove (as "sa-" actions, sent to SimpleAdmin): remove
    // only on rows SimpleAdmin keeps (admins.json and admin_groups.json are changed in their files)
    private static DataSource WithPanelFlags(DataSource source, DataSource? simpleAdmin)
    {
        if (source.Id != "server")
            return source;
        List<DataAction> SimpleAdminActions(string list) => simpleAdmin?.Lists.FirstOrDefault(l => l.Id == list)?.Actions
            .Select(a => new DataAction { Id = "sa-" + a.Id, Label = a.Label, Row = a.Row, Flag = a.Flag, Fields = a.Fields, When = a.Row ? new() { ["from"] = "SimpleAdmin" } : a.When }).ToList() ?? new();
        return new DataSource
        {
            Id = source.Id, Name = source.Name, Available = source.Available, Reason = source.Reason,
            Lists = source.Lists.Select(l => new DataList
            {
                Id = l.Id, Label = l.Label, Search = l.Search, Filters = l.Filters,
                Flag = l.Id == "players" ? PanelPermissions.Need("players") : PanelPermissions.Need("serveradmins"),
                Actions = l.Actions.Select(a => new DataAction { Id = a.Id, Label = a.Label, Row = a.Row, Fields = a.Fields, When = a.When, Flag = PanelPermissions.Need("kick") })
                    .Concat(l.Id is "admins" or "groups" ? SimpleAdminActions(l.Id) : new()).ToList()
            }).ToList()
        };
    }
}
