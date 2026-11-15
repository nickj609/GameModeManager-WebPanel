using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GmmPanel.Data;

// The site look (panel.json "Site") as a stylesheet, /theme.css, loaded after site.css by every page. A stylesheet from the
// panel itself, so the CSP (no inline styles) is kept. The logo picture lives in data/branding.
public sealed class SiteTheme
{
    public const int MaxLogoBytes = 512 * 1024;
    private readonly PanelStore _store;

    public SiteTheme(PanelStore store)
    {
        _store = store;
        Folder = Path.Combine(store.DataDirectory, "branding");
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }
    public SiteLook Look => _store.Current.Site;

    public string Name => Look.Name.Length > 0 ? Look.Name : "GameModeManager Panel";
    // The community's name as people see it: the site name, else the built-in logo's (the dashboard's eyebrow, the footer)
    public string Brand => Look.Name.Length > 0 ? Look.Name : "Example.gg";

    // Changes whenever the look does, or the panel is updated (the stylesheet's code may have changed), so browsers fetch
    // the new stylesheet and logo
    private static readonly string Build = typeof(SiteTheme).Assembly.ManifestModule.ModuleVersionId.ToString("N");
    public string Version => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        System.Text.Json.JsonSerializer.Serialize(Look) + string.Join(",", _store.Current.Links.Select(l => l.Color)) + LogoStamp() + Build)))[..10].ToLowerInvariant();

    // A site name that reads as a domain ("Example.gg") splits into the name and its ending, which the logo and footer show in
    // the accent colour, like the built-in logo's ".io"; any other name has no ending
    public static (string Name, string? Ending) Domain(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 && name.Length - dot is >= 3 and <= 7 && !name.Contains(' ') && name[(dot + 1)..].All(char.IsLetter)
            ? (name[..dot], name[dot..]) : (name, null);
    }

    // The site's pictures in data/branding: the logo (header, and footer unless it has its own), the footer logo and the favicon
    public string? LogoUrl => Url(Look.Logo);
    public string? FooterLogoUrl => Url(Look.FooterLogo);
    public string? FaviconUrl => Url(Look.Favicon);
    public bool LogoIsSvg => IsSvg(Look.Logo);
    public bool FooterLogoIsSvg => IsSvg(Look.FooterLogo);

    // An SVG logo is drawn inside the page so it takes the theme's colours (SvgLogo); null for a picture or none. Each place
    // it's drawn gets its own id prefix.
    public Microsoft.AspNetCore.Html.HtmlString? LogoSvg(string prefix) => Svg(Look.Logo, prefix);
    public Microsoft.AspNetCore.Html.HtmlString? FooterLogoSvg(string prefix) => Svg(Look.FooterLogo, prefix);

    // The files a request may fetch from /branding
    public bool Serves(string file) => file.Length > 0 && (file == Look.Logo || file == Look.FooterLogo || file == Look.Favicon);

    private string? Url(string file) => Exists(file) ? $"/branding/{file}?v={Version}" : null;
    private bool Exists(string file) => file.Length > 0 && File.Exists(Path.Combine(Folder, file));
    private bool IsSvg(string file) => file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && Exists(file);

    private readonly Dictionary<string, (string Version, string Markup)> _svg = new();
    private Microsoft.AspNetCore.Html.HtmlString? Svg(string file, string prefix)
    {
        if (!IsSvg(file))
            return null;
        string version = Version;
        lock (_svg)
        {
            if (!_svg.TryGetValue(file, out var cached) || cached.Version != version)
                _svg[file] = cached = (version, File.ReadAllText(Path.Combine(Folder, file)));
            return new Microsoft.AspNetCore.Html.HtmlString(SvgLogo.WithIds(cached.Markup, prefix));
        }
    }

    private string LogoStamp() => string.Concat(new[] { Look.Logo, Look.FooterLogo, Look.Favicon }.Where(Exists)
        .Select(f => File.GetLastWriteTimeUtc(Path.Combine(Folder, f)).Ticks.ToString()));

    public string Css()
    {
        SiteLook look = Look;
        var css = new StringBuilder("/* The site look, from the Appearance page */\n:root {\n");
        // A colour saved before the readability check (SiteLook.ColourProblem) that fails it is left out: the default shows
        bool Use(string field, string value) => SiteLook.IsColour(value) && SiteLook.ColourProblem(field, value) == null;
        if (Use("warn", look.Warn))
            css.Append($"\t--warn: {look.Warn};\n");
        if (Use("accent", look.Accent))
            css.Append($"\t--accent: {look.Accent};\n");
        int radius = Math.Clamp(look.Radius, 0, 16);
        if (radius != 8)
            css.Append($"\t--radius: {radius}px;\n");
        // The backdrop's darkness to start with; appearance.js reads it as the default for each viewer's slider
        css.Append($"\t--dim: {(Math.Clamp(look.Dim, 0, 100) / 100.0).ToString("0.00", CultureInfo.InvariantCulture)};\n");
        // The see-through look everyone starts with; appearance.js reads these as each viewer's defaults
        css.Append($"\t--glass: {(Math.Clamp(look.PanelOpacity, 40, 100) / 100.0).ToString("0.00", CultureInfo.InvariantCulture)};\n");
        css.Append($"\t--blur: {Math.Clamp(look.PanelBlur, 0, 32)}px;\n");
        css.Append($"\t--bar-alpha: {(Math.Clamp(look.BarOpacity, 40, 100) / 100.0).ToString("0.00", CultureInfo.InvariantCulture)};\n");
        // The surfaces are for the dark look only: light mode keeps its own palette (site.css), so its dark text stays readable
        css.Append("}\n:root:not([data-theme=\"light\"]) {\n");
        if (Use("bars", look.Bars))
            css.Append($"\t--bar: rgb({Rgb(look.Bars)} / var(--bar-alpha));\n");
        if (Use("panel", look.Panel))
            css.Append($"\t--glass-top: rgb({Rgb(look.Panel, 1.0)} / calc(var(--glass) - 0.08));\n\t--glass-low: rgb({Rgb(look.Panel, 0.55)} / var(--glass));\n"
                // Menus, the drawer, toasts and dialogs: nearly solid, so the colour is toned down towards a neutral dark grey
                // to look like the see-through body does over the dark backdrop (a solid navy reads far bluer than the panel)
                + $"\t--pop-top: rgb({Toned(look.Panel, 22, 0.55)} / 0.97);\n\t--pop-low: rgb({Toned(look.Panel, 11, 0.55)} / 0.98);\n");
        if (Use("notify", look.Notify))
            css.Append($"\t--notify: {look.Notify};\n");   // Pop-ups: the toasts and the bell's list
        if (Use("page", look.Page))
            css.Append($"\t--bg-top: {look.Page};\n");
        css.Append("}\n");

        double strength = Math.Clamp(look.PatternStrength, 1, 20) / 100.0;
        string ink = $"rgb(var(--ink) / {strength.ToString("0.00", CultureInfo.InvariantCulture)})";
        string? pattern = look.Pattern switch
        {
            "dots" => $"radial-gradient({ink} 1px, transparent 1.6px) 0 0 / 16px 16px",
            "grid" => $"linear-gradient({ink} 1px, transparent 1px) 0 0 / 24px 24px, linear-gradient(90deg, {ink} 1px, transparent 1px) 0 0 / 24px 24px",
            "lines" => $"repeating-linear-gradient(135deg, {ink} 0 1px, transparent 1px 12px)",
            _ => null
        };
        // Community links with their own colour (Site settings); the rest use the accent (site.css)
        var links = _store.Current.Links;
        for (int i = 0; i < links.Count; i++)
            if (SiteLook.IsColour(links[i].Color))
                css.Append($".link-{i} {{ color: {links[i].Color} !important; }}\n");
        if (pattern != null)
            css.Append($".frame, .site-top, .site-foot {{ background-image: none; }}\n.frame {{ background: {pattern}, linear-gradient(180deg, var(--glass-top), var(--glass-low)); }}\n");
        return css.ToString();
    }

    // "r g b" for rgb(), darkened by a factor (the panel's lower half)
    private static string Rgb(string hex, double factor = 1.0)
    {
        int r = Convert.ToInt32(hex[1..3], 16), g = Convert.ToInt32(hex[3..5], 16), b = Convert.ToInt32(hex[5..7], 16);
        int Scale(int c) => Math.Clamp((int)(c * factor), 0, 255);
        return $"{Scale(r)} {Scale(g)} {Scale(b)}";
    }

    // A colour mixed with a neutral grey of the given level: share is how much of the colour stays (0 to 1)
    private static string Toned(string hex, int grey, double share)
    {
        int r = Convert.ToInt32(hex[1..3], 16), g = Convert.ToInt32(hex[3..5], 16), b = Convert.ToInt32(hex[5..7], 16);
        int Mix(int c) => Math.Clamp((int)Math.Round(c * share + grey * (1 - share)), 0, 255);
        return $"{Mix(r)} {Mix(g)} {Mix(b)}";
    }

    // A logo picture: PNG, JPEG or WebP only (an SVG can carry script), checked by its first bytes, up to 512 KB
    // A favicon can also be an .ico
    public static bool IsIcon(byte[] bytes) => bytes.Length > 6 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0;

    public static string? PictureType(byte[] bytes) =>
        bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 ? ".png"
        : bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF ? ".jpg"
        : bytes.Length > 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP" ? ".webp"
        : null;

    public static string ContentType(string file) => Path.GetExtension(file) switch { ".png" => "image/png", ".jpg" => "image/jpeg", ".webp" => "image/webp", ".svg" => "image/svg+xml", ".ico" => "image/x-icon", _ => "application/octet-stream" };
}
