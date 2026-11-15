using GmmPanel.Data;
using GmmPanel.Commands;
using GmmPanel.Protocol;
using Microsoft.Extensions.Caching.Memory;

namespace GmmPanel.Agents;

// What visitors and players may read: a fixed set of lists with fewer columns (no admin names), and a player's own rows.
// Read through any connected server that has the plugin (their databases are shared), and kept a minute, so visitors can't
// make the game servers busy.
public sealed class PublicData
{
    // Public lists and the columns they show
    private static readonly Dictionary<string, string[]> Public = new()
    {
        ["simpleadmin/bans"] = new[] { "name", "steamid", "country", "reason", "created", "length", "status" },   // country: a code, from the ban's IP on the server
        ["simpleadmin/mutes"] = new[] { "name", "steamid", "country", "type", "reason", "created", "length", "status" }
    };

    // A signed-in player's own rows ("My profile")
    private static readonly Dictionary<string, string[]> Own = new()
    {
        ["simpleadmin/bans"] = Public["simpleadmin/bans"],
        ["simpleadmin/mutes"] = Public["simpleadmin/mutes"],
        ["simpleadmin/warns"] = new[] { "reason", "created", "length", "status" }
    };

    private readonly AgentRegistry _registry;
    private readonly IMemoryCache _cache;

    public PublicData(AgentRegistry registry, IMemoryCache cache)
    {
        _registry = registry;
        _cache = cache;
    }

    // Whether any connected server can show this list now
    public bool Offers(string source, string list) => Find(source, list) != null;

    public Task<CommandResult> PublicAsync(string source, string list, int page, string search, string filter) =>
        ReadAsync(source, list, Public, page, search, filter, own: null);

    public Task<CommandResult> OwnAsync(string source, string list, string steamId) =>
        ReadAsync(source, list, Own, 1, steamId, "all", own: steamId);

    private async Task<CommandResult> ReadAsync(string source, string list, Dictionary<string, string[]> allowed, int page, string search, string filter, string? own)
    {
        if (!allowed.TryGetValue($"{source}/{list}", out string[]? columns))
            return new CommandResult(false, "That list isn't public.");
        ServerView? view = Find(source, list);
        if (view?.Status == null || view.Connection == null)
            return new CommandResult(false, "No connected server can show this right now.");

        var args = new Dictionary<string, string> { ["source"] = source, ["list"] = list, ["page"] = page.ToString() };
        if (search.Length > 0)
            args["search"] = search;
        // Only lists with filters (bans: active or all) take one
        bool filters = view.Status.Data!.First(d => d.Id == source).Lists.First(l => l.Id == list).Filters.Contains(filter);
        if (filter.Length > 0 && filters)
            args["filter"] = filter;

        string key = $"public:{string.Join('|', args.OrderBy(a => a.Key).Select(a => a.Key + "=" + a.Value))}";
        if (_cache.TryGetValue(key, out CommandResult? cached) && cached != null)
            return cached;
        if (CommandCatalog.Check("query", view.Status, args) is string refused)
            return new CommandResult(false, refused);

        CommandResult result = await view.Connection.RunAsync("query", args, "", "Public page");
        if (result.Ok && result.Data != null)
        {
            DataPage data = result.Data;
            var rows = own == null ? data.Rows : data.Rows.Where(row => row.GetValueOrDefault("steamid") == own).ToList();
            result = result with
            {
                Data = new DataPage
                {
                    Columns = data.Columns.Where(c => columns.Contains(c.Key)).ToList(),
                    Rows = rows.Select(row => row.Where(cell => columns.Contains(cell.Key)).ToDictionary(cell => cell.Key, cell => cell.Value)).ToList(),
                    Page = data.Page,
                    PageSize = data.PageSize,
                    Total = own == null ? data.Total : rows.Count
                }
            };
        }
        _cache.Set(key, result, TimeSpan.FromSeconds(result.Ok ? 60 : 10));
        return result;
    }

    private ServerView? Find(string source, string list) => _registry.All()
        .Where(v => v.Online && v.Status?.Data?.Any(d => d.Id == source && d.Available && d.Lists.Any(l => l.Id == list)) == true)
        .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();
}
