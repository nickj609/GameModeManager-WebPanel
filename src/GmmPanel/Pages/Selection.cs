using GmmPanel.Auth;
using GmmPanel.Data;

namespace GmmPanel.Pages;

// The server shown in the header's switcher: the one in the address, else the last one opened (a cookie), else the first.
// The cookie only names a server; it's checked against the admin's servers on every read.
public static class Selection
{
    private const string Cookie = "gmm.server";

    public static string? Get(HttpContext context, PanelUser admin, PanelStore store)
    {
        var mine = store.Current.Servers.Where(server => admin.CanUse(server.Name)).Select(server => server.Name).ToList();
        string? wanted = context.GetRouteValue("name") as string ?? context.Request.Query["server"].FirstOrDefault() ?? context.Request.Cookies[Cookie];
        return mine.FirstOrDefault(name => name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) ?? mine.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    public static void Set(HttpContext context, string server)
    {
        context.Response.Cookies.Append(Cookie, server, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            MaxAge = TimeSpan.FromDays(365),
            IsEssential = true
        });
    }
}
