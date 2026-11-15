using System.Net;

namespace GmmPanel.Auth;

// Development sign-in without Steam: only in Development, only from this machine, and only when the request didn't come
// through Cloudflare. The SteamID can be changed on the sign-in page there, to try how a player or a server admin sees it.
public static class DevSignIn
{
    public static string? SteamId(HttpContext context)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        string? steamId = configuration["Panel:DevSignInAs"];
        if (!environment.IsDevelopment() || string.IsNullOrWhiteSpace(steamId))
            return null;
        IPAddress? from = context.Connection.RemoteIpAddress;
        bool local = from != null && IPAddress.IsLoopback(from);
        var headers = context.Request.Headers;
        bool viaCloudflare = headers.ContainsKey("CF-Connecting-IP") || headers.ContainsKey("CF-Ray") || headers.ContainsKey("X-Forwarded-For");
        return local && !viaCloudflare ? steamId : null;
    }
}
