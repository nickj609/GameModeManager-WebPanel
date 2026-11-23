using GmmPanel.Auth;
using GmmPanel.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GmmPanel.Pages;

// Pages that know who's looking. Admin is for pages that need someone signed in (the fallback policy already sent everyone
// else to sign in); Viewer is null for a visitor on a public page.
public abstract class PanelPage : PageModel
{
    private PanelUser? _viewer;
    private bool _read;

    public PanelUser? Viewer
    {
        get
        {
            if (!_read)
            {
                _read = true;
                _viewer = User.Identity?.IsAuthenticated == true ? PanelUser.From(User, HttpContext.RequestServices.GetRequiredService<PanelStore>()) : null;
            }
            return _viewer;
        }
    }

    public PanelUser Admin => Viewer ?? throw new InvalidOperationException("Not signed in.");

    // Whether one of the panel's own features is on (Site settings, Features)
    public bool FeatureOn(string id) => Features.On(HttpContext.RequestServices.GetRequiredService<PanelStore>().Current, id);

    // What the viewer may do on a server: their admin flags there (asked once every 2 minutes, then cached)
    public Task<Access> AccessFor(string? server) => AccessAsync(server);

    protected Task<Access> AccessAsync(string? server) =>
        HttpContext.RequestServices.GetRequiredService<AccessService>().ForAsync(Viewer, server);

    // The servers whose records (reports, chat, who's on, player history) the viewer may read: those where they have the
    // flag. Null means every server (panel admins). Someone who is staff on one server doesn't read another's.
    protected async Task<Func<string, bool>?> ServersWithAsync(string flag)
    {
        if (Viewer?.IsPanelAdmin == true)
            return null;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ServerEntry server in HttpContext.RequestServices.GetRequiredService<PanelStore>().Current.Servers)
            if (Viewer != null && Viewer.CanUse(server.Name) && (await AccessAsync(server.Name)).Has(flag))
                allowed.Add(server.Name);
        return allowed.Contains;
    }
}
