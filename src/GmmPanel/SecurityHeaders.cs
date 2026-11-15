namespace GmmPanel;

// Every response: no inline scripts or styles, no framing, nothing sent to other sites. Pictures only from here and
// Steam's avatar hosts (the signed-in person's Steam picture). form-action allows Steam
// because the sign-in form redirects there.
public static class SecurityHeaders
{
    private const string Policy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: https://avatars.steamstatic.com https://avatars.akamai.steamstatic.com https://avatars.cloudflare.steamstatic.com https://avatars.fastly.steamstatic.com; media-src 'self'; connect-src 'self'; " +
        "font-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; " +
        "form-action 'self' https://steamcommunity.com";

    // The status widget (/widget/<server>) is made to sit in another site's page: any site may frame it. It has no forms and
    // shows nothing that isn't on the public dashboard.
    private static readonly string WidgetPolicy = Policy.Replace("frame-ancestors 'none'", "frame-ancestors *");

    public static Task Add(HttpContext context, Func<Task> next)
    {
        IHeaderDictionary headers = context.Response.Headers;
        bool widget = context.Request.Path.StartsWithSegments("/widget");
        headers.ContentSecurityPolicy = widget ? WidgetPolicy : Policy;
        headers.XContentTypeOptions = "nosniff";
        if (!widget)
            headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "same-origin";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        return next();
    }
}
