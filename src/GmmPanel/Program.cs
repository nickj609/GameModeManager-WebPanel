using System.Reflection;
using System.Threading.RateLimiting;
using GmmPanel;
using GmmPanel.Agents;
using GmmPanel.Auth;
using GmmPanel.Commands;
using GmmPanel.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;

// gmmpanel admin ... / gmmpanel token ... / gmmpanel server ...: change panel.json and exit
if (args.Length > 0 && Cli.Handles(args[0]))
    return Cli.Run(args);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
var panel = builder.Configuration.GetSection("Panel");

var store = new PanelStore(Cli.DataDirectory(builder.Configuration));
builder.Services.AddSingleton(store);
// The panel's own log files (data/logs, one a day), next to the console; kept Retention.LogDays
builder.Logging.AddProvider(new FileLog(store.DataDirectory));

// Sign-in cookies stay valid across restarts: the keys live in the data folder (protected by Windows itself on Windows)
var keys = builder.Services.AddDataProtection()
    .SetApplicationName("gmmpanel")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(store.DataDirectory, "keys")));
if (OperatingSystem.IsWindows())
    keys.ProtectKeysWithDpapi();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<AgentHub>();
builder.Services.AddSingleton<CommandService>();
builder.Services.AddSingleton<Backgrounds>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<AccessService>();
builder.Services.AddSingleton<SetupState>();
builder.Services.AddSingleton<PublicData>();
builder.Services.AddHttpClient<SteamProfile>();
builder.Services.AddSingleton<History>();
builder.Services.AddSingleton<Tickets>();
builder.Services.AddSingleton<PlayerNotes>();
builder.Services.AddSingleton<Notices>();
builder.Services.AddSingleton<Backup>();
builder.Services.AddSingleton<Jobs>();
builder.Services.AddSingleton<TaskRunner>();
builder.Services.AddHostedService<JobClock>();
builder.Services.AddSingleton<DataCleanup>();
builder.Services.AddHostedService(services => services.GetRequiredService<DataCleanup>());
builder.Services.AddSingleton<PanelLists>();
builder.Services.AddSingleton<MapPictures>();
builder.Services.AddSingleton<UserPrefs>();
builder.Services.AddSingleton<SiteTheme>();
builder.Services.AddHttpClient("discord", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton(services => new Discord(store, services.GetRequiredService<IHttpClientFactory>().CreateClient("discord"), services.GetRequiredService<ILogger<Discord>>(), services));
PanelPermissions.Store = store;

// cloudflared runs on this machine (or next to the panel in Docker); only it may say who the visitor is
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    foreach (string network in panel.GetSection("TrustedProxies").Get<string[]>() ?? Array.Empty<string>())
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "gmmpanel";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.LoginPath = "/signin";
        options.AccessDeniedPath = "/denied";
        // A background htmx poll after the session ended gets 401 (panel.js says so) rather than the sign-in page
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Headers.ContainsKey("HX-Request"))
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else
                context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        // panel.json is the truth on every request: a removed panel admin is a player from the next click
        options.Events.OnValidatePrincipal = async context =>
        {
            PanelUser? user = context.Principal == null ? null : PanelUser.From(context.Principal, store);
            if (user == null)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }
            context.ReplacePrincipal(PanelUser.Principal(user, CookieAuthenticationDefaults.AuthenticationScheme));
        };
    })
    .AddSteam(options =>
    {
        // Anyone with a Steam account may sign in: panel admins get the panel, everyone else is a player whose rights on each
        // server come from that server's admin flags. While setup is open, only the person who typed the setup code may sign
        // in, and they become the first panel admin.
        options.Events.OnTicketReceived = async context =>
        {
            HttpContext http = context.HttpContext;
            AuditLog audit = http.RequestServices.GetRequiredService<AuditLog>();
            SetupState setup = http.RequestServices.GetRequiredService<SetupState>();
            string? claimed = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            string? steamId = PanelUser.SteamIdFromOpenId(claimed);
            string from = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (steamId == null)
            {
                context.Response.Redirect("/signin?failed=1");
                context.HandleResponse();
                return;
            }

            AdminEntry? entry = store.FindAdmin(steamId) ?? setup.Claim(http, steamId);
            if (entry == null && setup.Needed)
            {
                context.Response.Redirect("/setup");
                context.HandleResponse();
                return;
            }
            string name = entry?.Name is { Length: > 0 } known ? known : await http.RequestServices.GetRequiredService<SteamProfile>().NameAsync(steamId);
            if (entry != null)
                audit.Write($"{name} ({steamId})", "sign in", null, true, $"panel admin, from {from}");
            context.Principal = PanelUser.Principal(steamId, name, entry != null ? Roles.Admin : Roles.Player, CookieAuthenticationDefaults.AuthenticationScheme);
            // A panel admin's sign-in while no server is added yet (the admin came from "admin add", so the setup code was
            // never asked for): the setup page, for the first server and its token
            if (entry != null && store.Current.Servers.Count == 0)
                context.ReturnUri = "/setup";
        };
        options.Events.OnRemoteFailure = context =>
        {
            context.Response.Redirect("/signin?failed=1");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Signed in by default; each server page then checks that person's flags on that server (Auth/Access.cs). The panel's own
    // pages (audit log, panel admins and servers) are for panel admins only.
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy("paneladmin", policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Admin));
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "gmmpanel.af";
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.HeaderName = "RequestVerificationToken";
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("signin", context => PerAddress(context, 10));
    options.AddPolicy("agent", context => PerAddress(context, 12));
    options.AddPolicy("public", context => PerAddress(context, 90));
});

// Pages are UTF-8: let every script through as itself (Cyrillic, Chinese ...) instead of &#x...; codes. Still HTML-encoded.
builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(options =>
    options.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));

builder.Services.AddRazorPages(options =>
{
    foreach (string page in new[] { "/SignIn", "/Denied", "/Error", "/Setup", "/Index", "/Bans", "/Stats", "/Appearance", "/ServerInfo", "/AllServers", "/Search", "/Privacy", "/Widget", "/Rules", "/PublicProfile" })
        options.Conventions.AllowAnonymousToPage(page);
    options.Conventions.AuthorizeFolder("/Audit", "paneladmin");
    options.Conventions.AuthorizePage("/Site", "paneladmin");
    options.Conventions.AuthorizePage("/Tasks");
});

builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(180));

var app = builder.Build();

if (app.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(panel["DevSignInAs"]))
    app.Logger.LogWarning("Development sign-in is on for {SteamId}. It only answers requests from this machine that didn't come through Cloudflare. Never run Development behind the tunnel.", panel["DevSignInAs"]);

app.UseForwardedHeaders();

// Outside Development the panel only answers https (as cloudflared reports it), so cookies are never sent in the clear,
// and browsers are told to use https only (HSTS) so they never try http first
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.Use((context, next) =>
    {
        if (context.Request.IsHttps || context.Request.Path == "/healthz")
            return next();
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return context.Response.WriteAsync("Open the panel through its https address. Behind a proxy, add it to Panel:TrustedProxies (see Installation, Other ways to host).");
    });
}

app.UseExceptionHandler("/Error");
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");
app.Use(SecurityHeaders.Add);

// The page files are inside the executable, so one file is the whole panel
// In Development, straight from the source folder when it's there (dotnet run / dotnet watch from the repo), uncached, so a
// CSS or JS edit shows on reload without a rebuild
string? sourceWwwroot = app.Environment.IsDevelopment()
    ? new[] { Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot")) }
        .FirstOrDefault(Directory.Exists)
    : null;
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = sourceWwwroot != null ? new PhysicalFileProvider(sourceWwwroot) : new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot"),
    ContentTypeProvider = ContentTypes(),
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = sourceWwwroot != null ? "no-cache" : "public, max-age=3600"
});
if (sourceWwwroot != null)
    app.Logger.LogInformation("Development: static files from {Folder}", sourceWwwroot);

app.UseRouting();

// The request's language, for T("...") in pages and Format (Lang.cs)
app.Use((context, next) =>
{
    GmmPanel.Data.Lang.Current = GmmPanel.Data.Lang.Pick(context.Request.Cookies[GmmPanel.Data.Lang.Cookie],
        context.RequestServices.GetRequiredService<PanelStore>().Current.Site.Language, context.Request.Headers.AcceptLanguage);
    return next(context);
});

// The footer's picker: remembers the language for a year, then back to the page
app.MapPost("/lang", (HttpContext context) =>
{
    string code = context.Request.Form["code"].ToString();
    if (GmmPanel.Data.Lang.Languages.Any(l => l.Code == code))
        context.Response.Cookies.Append(GmmPanel.Data.Lang.Cookie, code, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = context.Request.IsHttps, MaxAge = TimeSpan.FromDays(365), IsEssential = true });
    string back = context.Request.Form["back"].ToString();
    return Results.LocalRedirect(back.StartsWith('/') && !back.StartsWith("//") && !back.Contains('\\') ? back : "/");
}).AllowAnonymous();

// Before there's a panel admin, every page is the setup wizard (static files, the Steam return, health and agents aside)
app.Use((context, next) =>
{
    string path = context.Request.Path.Value ?? "/";
    bool open = path.StartsWith("/setup", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/signin", StringComparison.OrdinalIgnoreCase)
        || path is "/healthz" or "/agent" or "/error" || Path.HasExtension(path);
    if (!open && context.RequestServices.GetRequiredService<SetupState>().Needed)
    {
        context.Response.Redirect("/setup");
        return Task.CompletedTask;
    }
    return next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// The address panel admins use is the panel's public one: Discord posts link to it (Discord.Learn skips local addresses)
app.Use((context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true && context.Request.Method == "GET" && !Path.HasExtension(context.Request.Path.Value)
        && PanelUser.From(context.User, store)?.IsPanelAdmin == true)
        context.RequestServices.GetRequiredService<Discord>().Learn(context.Request.Host.Value ?? "");
    return next();
});
app.UseWebSockets();

app.Map("/agent", (HttpContext context, AgentHub hub) => hub.HandleAsync(context))
    .AllowAnonymous()
    .RequireRateLimiting("agent");

app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

// Every server's public status as JSON, for a Discord bot or a website: what the public dashboard shows, nothing more.
// Any site may read it (CORS *); rate limited like the public pages.
app.MapGet("/status.json", (PanelStore panelStore, GmmPanel.Agents.AgentRegistry registry, HttpContext context) =>
{
    // The status widget switched off (Site settings, Features): no JSON either
    if (!Features.On(panelStore.Current, "widget"))
        return Results.NotFound();
    context.Response.Headers.AccessControlAllowOrigin = "*";
    var servers = panelStore.Current.Servers.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s =>
    {
        var view = registry.Get(s.Name);
        var status = view.Status;
        return new
        {
            name = view.Name,
            online = view.Online,
            players = view.Online ? status?.Players ?? 0 : 0,
            maxPlayers = status?.MaxPlayers ?? 0,
            mode = status?.Mode.Label,
            map = status?.Map.Label,
            connect = view.Online ? view.ConnectAddress : null
        };
    });
    return Results.Json(new { servers }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
}).AllowAnonymous().RequireRateLimiting("public");

// The owner's own backgrounds (data/backgrounds), for signed-in admins only. Range requests let videos seek and loop.
// The site look as a stylesheet, and its logo picture (Appearance page), for everyone
app.MapGet("/theme.css", (SiteTheme theme, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.Text(theme.Css(), "text/css");
}).AllowAnonymous();
app.MapGet("/branding/{name}", (string name, SiteTheme theme, HttpContext context) =>
{
    if (!theme.Serves(name))
        return Results.NotFound();
    string path = Path.Combine(theme.Folder, name);
    if (!File.Exists(path))
        return Results.NotFound();
    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    // An SVG opened on its own runs nothing, even though it's cleaned on upload
    if (name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
    return Results.File(path, SiteTheme.ContentType(name));
}).AllowAnonymous();

// Map pictures (data/maps), for everyone: Workshop previews, public anyway
// Connect from a link (Discord's Connect button can't open steam:// itself): straight to the game
app.MapGet("/connect/{name}", (string name, GmmPanel.Agents.AgentRegistry registry) =>
    registry.Find(name)?.ConnectAddress is { } address ? Results.Redirect($"steam://connect/{address}") : Results.Redirect($"/server/{Uri.EscapeDataString(name)}")).AllowAnonymous();

app.MapGet("/maps/{name}", (string name, MapPictures maps, HttpContext context) =>
{
    var file = maps.Find(name);
    if (file == null)
        return Results.NotFound();
    context.Response.Headers.CacheControl = "public, max-age=86400";
    return Results.File(file.Value.Path, file.Value.Type);
}).AllowAnonymous();

app.MapGet("/media/{name}", (string name, Backgrounds backgrounds, HttpContext context) =>
{
    // Everyone sees the backgrounds, visitors too
    var file = backgrounds.Find(name);
    if (file == null)
        return Results.NotFound();
    context.Response.Headers.CacheControl = "public, max-age=86400";
    return Results.File(file.Value.Path, file.Value.Type, enableRangeProcessing: true);
}).AllowAnonymous();

app.MapRazorPages();

app.Logger.LogInformation("GameModeManager panel {Version}, data in {Data}", typeof(Program).Assembly.GetName().Version, store.DataDirectory);
app.Services.GetRequiredService<SetupState>().LogCode();

app.Run();
return 0;

static RateLimitPartition<string> PerAddress(HttpContext context, int perMinute) =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

// The web app manifest (installing the panel on a phone's home screen) isn't in ASP.NET's default list
static Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider ContentTypes()
{
    var types = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    types.Mappings[".webmanifest"] = "application/manifest+json";
    return types;
}

public partial class Program;
