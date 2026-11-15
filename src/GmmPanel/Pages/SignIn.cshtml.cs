using GmmPanel.Auth;
using GmmPanel.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace GmmPanel.Pages;

[EnableRateLimiting("signin")]
public class SignInModel : PageModel
{
    private readonly PanelStore _store;
    private readonly AuditLog _audit;
    private readonly SetupState _setup;

    public SignInModel(PanelStore store, AuditLog audit, SetupState setup)
    {
        _store = store;
        _audit = audit;
        _setup = setup;
    }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Failed { get; set; }

    public string? DevSignIn { get; private set; }

    public void OnGet()
    {
        DevSignIn = Auth.DevSignIn.SteamId(HttpContext);
    }

    // Steam's OpenID sign-in. The return address is this panel's own; the Steam handler checks what Steam signed.
    public IActionResult OnPost()
    {
        return Challenge(new AuthenticationProperties { RedirectUri = SafeReturn() }, "Steam");
    }

    // Development: as the configured SteamID, or another one typed in (to see the panel as a player or a server's admin)
    public async Task<IActionResult> OnPostDevAsync(string? steamId)
    {
        string? allowed = Auth.DevSignIn.SteamId(HttpContext);
        if (allowed == null)
            return NotFound();
        string id = steamId is { Length: 17 } typed && typed.All(char.IsAsciiDigit) ? typed : allowed;
        AdminEntry? entry = _store.FindAdmin(id) ?? _setup.Claim(HttpContext, id);
        if (entry == null && _setup.Needed)
            return LocalRedirect("/setup");
        string name = entry?.Name is { Length: > 0 } known ? known : $"Dev player {id[^4..]}";
        _audit.Write($"{name} ({id})", "sign in", null, true, entry != null ? "development sign-in, panel admin" : "development sign-in, player");
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            PanelUser.Principal(id, name, entry != null ? Roles.Admin : Roles.Player, CookieAuthenticationDefaults.AuthenticationScheme));
        return LocalRedirect(SafeReturn());
    }

    public async Task<IActionResult> OnPostSignOutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return LocalRedirect("/");
    }

    private string SafeReturn() => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}
