using GmmPanel.Agents;
using GmmPanel.Auth;
using GmmPanel.Commands;
using GmmPanel.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace GmmPanel.Pages;

// The setup wizard. 1: the code from the panel's log and your name. 2: Steam sign-in, which makes you the first panel admin.
// 3: your first game server: its token and the settings to paste into GameModeManager. 4: wait until it connects.
// After the first admin exists, steps 3 and 4 stay here for panel admins, to add more servers.
public class SetupModel : PanelPage
{
    private readonly SetupState _setup;
    private readonly PanelStore _store;
    private readonly AgentRegistry _registry;
    private readonly AuditLog _audit;

    public SetupModel(SetupState setup, PanelStore store, AgentRegistry registry, AuditLog audit)
    {
        _setup = setup;
        _store = store;
        _registry = registry;
        _audit = audit;
    }

    public string Step { get; private set; } = "code";   // code, signin, server, token, wait
    public string? Problem { get; private set; }
    public string? DevSteamId { get; private set; }
    public SiteLook Look => _store.Current.Site;

    [BindProperty(SupportsGet = true)]
    public string? Server { get; set; }

    // Filled after a token is made: shown once, never stored
    public string? Token { get; private set; }
    public string AgentUrl { get; private set; } = "";

    public IActionResult OnGet()
    {
        if (_setup.Needed)
        {
            Step = _setup.ProvenName(HttpContext) != null ? "signin" : "code";
            DevSteamId = DevSignIn.SteamId(HttpContext);
            return Page();
        }
        if (Viewer?.IsPanelAdmin != true)
            return Viewer == null ? LocalRedirect("/signin?returnUrl=%2Fsetup") : LocalRedirect("/denied");
        if (Request.Query["step"] == "look")
        {
            Step = "look";
            return Page();
        }
        Step = Server != null && _store.Current.Servers.Any(s => s.Name.Equals(Server, StringComparison.OrdinalIgnoreCase)) ? "wait" : "server";
        AgentUrl = GmmSettings.AgentUrl(_store.Current, Request);
        return Page();
    }

    // Step 1: the code and a name. A wrong code says so; the limiter allows 10 tries a minute from one address.
    public IActionResult OnPostCode(string? code, string? name)
    {
        if (!_setup.Needed)
            return LocalRedirect("/setup");
        Step = "code";
        if (!_setup.MayTry(HttpContext))
        {
            Problem = "Too many tries. Wait a minute.";
            return Page();
        }
        name = (name ?? "").Trim();
        if (CommandCatalog.TextProblem(name, 40, "Your name", allowEmpty: false) is string bad)
            Problem = bad;
        else if (!_setup.CheckCode(code))
            Problem = "That isn't the setup code. It's in the panel's log (its console window, or docker logs), and changes each time the panel starts.";
        if (Problem != null)
            return Page();
        _setup.Prove(HttpContext, name);
        return LocalRedirect("/setup");
    }

    // Step 2: Steam. Program.cs's sign-in sees the setup cookie and makes this account the first panel admin.
    public IActionResult OnPostSteam()
    {
        return Challenge(new AuthenticationProperties { RedirectUri = "/setup" }, "Steam");
    }

    // Step 3: a server and its token
    public IActionResult OnPostServer(string? name, string? agentUrl)
    {
        if (Viewer?.IsPanelAdmin != true)
            return LocalRedirect("/denied");
        name = (name ?? "").Trim();
        AgentUrl = string.IsNullOrWhiteSpace(agentUrl) ? GmmSettings.AgentUrl(_store.Current, Request) : agentUrl.Trim();
        Step = "server";
        if (!PanelStore.IsServerName(name))
            Problem = "Server names use letters, digits, - and _ (up to 40), like Production or Deathmatch-2.";
        else if (GmmSettings.AgentUrlProblem(AgentUrl) is string bad)
            Problem = bad;
        else if (_store.Current.Servers.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            Problem = $"There's already a server called {name}. Give this one its own name, like {name}-2, or make a new token for it in Site settings, Servers.";
        if (Problem != null)
            return Page();
        // Remembered, so Site settings, Servers shows every server's settings with the same address
        if (AgentUrl != GmmSettings.AgentUrl(_store.Current, Request))
        {
            PanelData data = _store.LoadForEdit();
            data.AgentUrl = AgentUrl;
            _store.Save(data);
        }

        Token = _store.NewServerToken(name);
        Server = name;
        Step = "token";
        _audit.Write(Admin.Audit, $"add server {name}", name, true, "from the setup page");
        return Page();
    }

    // Step 4, polled: has the server connected yet?
    public IActionResult OnGetWait()
    {
        if (Viewer?.IsPanelAdmin != true || Server == null)
            return StatusCode(StatusCodes.Status403Forbidden);
        ServerView? view = _registry.Find(Server);
        return Content(view?.Online == true
            ? $"<span class=\"ind ok\">{System.Net.WebUtility.HtmlEncode(Server)} is connected</span> <a class=\"button primary\" href=\"/setup?step=look\">Next: make it yours</a> <a class=\"button\" href=\"/\">Open the dashboard</a>"
            : "<span class=\"ind warn\">Waiting for the server to connect</span>", "text/html");
    }

}
