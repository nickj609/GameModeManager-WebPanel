using GmmPanel.Agents;
using GmmPanel.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace GmmPanel.Pages;

// The status widget: one server's card, for another site to frame (SecurityHeaders lets /widget be framed)
[EnableRateLimiting("public")]
public class WidgetModel : PageModel
{
    private readonly PanelStore _store;
    private readonly AgentRegistry _registry;
    private readonly MapPictures _maps;

    public WidgetModel(PanelStore store, AgentRegistry registry, MapPictures maps)
    {
        _store = store;
        _registry = registry;
        _maps = maps;
    }

    [BindProperty(SupportsGet = true)]
    public string Name { get; set; } = "";

    public ServerView? View { get; private set; }

    public string? ArtFor(string map) => _maps.UrlFor(map);

    public IActionResult OnGet()
    {
        if (!Features.On(_store.Current, "widget"))
            return NotFound();
        ServerEntry? entry = _store.Current.Servers.FirstOrDefault(s => s.Name.Equals(Name, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
            return NotFound();
        View = _registry.Get(entry.Name);
        return Page();
    }
}
