using System.Text.Json;
using GmmPanel.Agents;
using GmmPanel.Auth;
using GmmPanel.Commands;
using GmmPanel.Data;
using Microsoft.AspNetCore.Mvc;

namespace GmmPanel.Pages;

public class ServerModel : PanelPage
{
    // The only form fields a command can carry
    private static readonly string[] ArgNames = { "mode", "map", "setting", "on", "value", "action", "round", "team", "kind", "key", "confirm", "line", "settings", "plugin" };

    private readonly PanelStore _store;
    private readonly AgentRegistry _registry;
    private readonly CommandService _commands;
    private readonly MapPictures _maps;

    public ServerModel(PanelStore store, AgentRegistry registry, CommandService commands, MapPictures maps)
    {
        _store = store;
        _registry = registry;
        _commands = commands;
        _maps = maps;
    }

    [BindProperty(SupportsGet = true)]
    public string Name { get; set; } = "";

    // Null: Manage (the game: maps, modes, settings ...); "admin": the server itself (hostname, team names, plugins, console)
    [BindProperty(SupportsGet = true)]
    public string? Section { get; set; }

    public bool IsAdmin => Section == "admin";

    public string Address => $"/servers/{Uri.EscapeDataString(View.Name)}{(IsAdmin ? "/admin" : "")}";

    public ServerView View { get; private set; } = null!;

    // The map's picture for the header's square (data/maps), else null (the crosshair)
    public string? ArtFor(string map) => _maps.UrlFor(map);

    // The viewer's admin flags on this server: which sections and rows the page shows
    public Access Access { get; private set; } = Access.Visitor;

    // Shown in the Console section's log
    public string ConsoleLine { get; private set; } = "";
    public CommandResult? ConsoleResult { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is IActionResult refused)
            return refused;
        Selection.Set(HttpContext, View.Name);
        return Page();
    }

    // Polled every 5 seconds with the revision the page shows: 204 (htmx keeps the page) unless something changed
    public async Task<IActionResult> OnGetLiveAsync(long rev)
    {
        if (await LoadAsync() is IActionResult refused)
            return refused;
        if (View.Revision == rev)
            return StatusCode(StatusCodes.Status204NoContent);
        return Partial("_ServerBody", this);
    }

    public async Task<IActionResult> OnPostCommandAsync(string command)
    {
        if (await LoadAsync() is IActionResult refused)
            return refused;

        CommandResult result = await _commands.RunAsync(Admin, View.Name, command, Args());

        // The server sends its new status right after the answer; have the page fetch it. A setting change that needs a
        // restart or applies next round asks the same question GameModeManager's menus ask (panel.js shows it).
        var events = new Dictionary<string, object> { ["refresh"] = true };
        if (result.Ok && result.FollowUp != null)
            events["followup"] = result.FollowUp;
        Response.Headers["HX-Trigger"] = JsonSerializer.Serialize(events, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Partial("_Toast", result);
    }

    // The Console section: one line to the server's own RCON, its output back into the log
    public async Task<IActionResult> OnPostConsoleAsync(string line)
    {
        if (await LoadAsync() is IActionResult refused)
            return refused;
        ConsoleLine = (line ?? "").Trim();
        ConsoleResult = await _commands.RunAsync(Admin, View.Name, "console", new Dictionary<string, string> { ["line"] = ConsoleLine });
        return Partial("_ConsoleEntry", this);
    }

    private Dictionary<string, string> Args()
    {
        var args = new Dictionary<string, string>();
        foreach (string arg in ArgNames)
            if (Request.Form.TryGetValue(arg, out var value) && !string.IsNullOrEmpty(value))
                args[arg] = value.ToString();
        return args;
    }

    // The server, and what the viewer may do there: Manage needs one of GameModeManager's admin page flags, Admin one of
    // @css/cvar, @css/rcon or @css/root. Null when the page may be shown.
    private async Task<IActionResult?> LoadAsync()
    {
        ServerEntry? entry = _store.Current.Servers.FirstOrDefault(server => server.Name.Equals(Name, StringComparison.OrdinalIgnoreCase));
        if (entry == null || !Admin.CanUse(entry.Name))
            return NotFound();
        View = _registry.Get(entry.Name);
        Access = await AccessAsync(entry.Name);
        bool allowed = IsAdmin ? Access.CanAdminister : Access.CanManage;
        if (!allowed)
            return Access.CanManage || Access.CanAdminister ? LocalRedirect(PagePath(!IsAdmin)) : LocalRedirect("/denied");
        return null;
    }

    public string PagePath(bool admin) => $"/servers/{Uri.EscapeDataString(View.Name)}{(admin ? "/admin" : "")}";
}
