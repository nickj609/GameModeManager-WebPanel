using GmmPanel.Auth;
using GmmPanel.Commands;
using GmmPanel.Data;
using Microsoft.AspNetCore.Mvc;

namespace GmmPanel.Pages;

// Site settings (panel admins): the site's look for everyone (name, logo, footer, colours, pattern, default language),
// backgrounds, map pictures, the community links and features; and (Site.Panel.cs) the panel admins, permissions,
// servers, Discord, retention and backups. Each person's own look is on Appearance.
// Restoring a backup can be big (pictures, backgrounds): up to Backup.MaxRestoreBytes on this page
[RequestSizeLimit(Backup.MaxRestoreBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = Backup.MaxRestoreBytes)]
public partial class SiteModel : PanelPage
{
    // The rail's sections, in order (Id = the pane and the #link)
    public sealed record Section(string Id, string Label, string Icon, string Group);
    public static readonly Section[] Sections =
    {
        new("look", "Site look", "settings", "Look"), new("links", "Social links", "link", "Look"), new("menu", "Menu links", "list", "Look"),
        new("backgrounds", "Backgrounds", "image", "Pictures"), new("maps", "Map pictures", "crosshair", "Pictures"),
        new("integrations", "Features", "plugins", "Features"), new("rules", "Rules", "list", "Features"), new("widget", "Status widget", "server", "Features"),
        new("admins", "Panel admins", "shield", "People"), new("permissions", "Permissions", "list", "People"),
        new("servers", "Servers", "server", "Servers"), new("discord", "Discord", "discord", "Servers"),
        new("retention", "Retention", "clock", "Data"), new("backup", "Backup", "refresh", "Data")
    };

    private readonly PanelStore _store;
    private readonly SiteTheme _theme;
    private readonly AuditLog _audit;
    private readonly Backgrounds _backgrounds;
    private readonly MapPictures _maps;
    private readonly GmmPanel.Agents.AgentRegistry _registry;

    public SiteModel(PanelStore store, SiteTheme theme, AuditLog audit, Backgrounds backgrounds, MapPictures maps, GmmPanel.Agents.AgentRegistry registry,
        AccessService access, Discord discord, DataCleanup cleanup, Backup backup)
    {
        _access = access;
        _discord = discord;
        _cleanup = cleanup;
        _backup = backup;
        _registry = registry;
        _backgrounds = backgrounds;
        _maps = maps;
        _store = store;
        _theme = theme;
        _audit = audit;
    }

    public SiteLook Look => _store.Current.Site;
    public PanelData Store => _store.Current;
    public List<string> ServerNames => _store.Current.Servers.Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
    public List<LinkEntry> Links => _store.Current.Links;
    public string AccentColour => SiteLook.IsColour(Look.Accent) ? Look.Accent : "#d4801a";   // The colour picker starts here for a link without its own
    public string? LogoUrl => _theme.LogoUrl;
    public Microsoft.AspNetCore.Html.HtmlString? LogoSvg => _theme.LogoSvg("preview");
    public string? FooterLogoUrl => _theme.FooterLogoUrl;
    public Microsoft.AspNetCore.Html.HtmlString? FooterLogoSvg => _theme.FooterLogoSvg("preview-foot");
    public string? FaviconUrl => _theme.FaviconUrl;
    public bool Owner => Viewer?.IsPanelAdmin == true;
    public string? Message { get; private set; }
    public bool Failed { get; private set; }
    public List<Background> OwnBackgrounds => _backgrounds.Own();
    public string? DisplayName(string file) => _backgrounds.DisplayName(file);
    public List<string> MapFiles => Directory.EnumerateFiles(_maps.Folder).Select(Path.GetFileName).OfType<string>().Order().ToList();
    public const long MaxUpload = 25 * 1024 * 1024;

    // Maps your servers play (their map lists) that have no picture yet, for the upload form
    public List<string> MissingPictures => _store.Current.Servers
        .SelectMany(s => _registry.Find(s.Name)?.Status?.Maps.Select(m => m.Name) ?? Enumerable.Empty<string>())
        .Distinct(StringComparer.OrdinalIgnoreCase).Where(m => _maps.UrlFor(m) == null).Order().ToList();

    public void OnGet()
    {
        if (TempData["Saved"] is string saved)
            Message = saved;
    }

    public async Task<IActionResult> OnPostSiteAsync(string? name, string? sub, string? footer, string? accent, string? warn, string? bars, string? panel, string? page, string? notify,
        string? pattern, int patternStrength, int radius, IFormFile? logo, bool removeLogo, IFormFile? footerLogo, bool removeFooterLogo,
        IFormFile? favicon, bool removeFavicon, bool reset)
    {
        if (!Owner)
            return LocalRedirect("/denied");
        PanelData data = _store.LoadForEdit();
        if (reset)
        {
            RemoveLogo(data.Site.Logo);
            RemoveLogo(data.Site.FooterLogo);
            RemoveLogo(data.Site.Favicon);
            data.Site = new SiteLook();
            return Saved(data, "The site look is back to the panel's own.");
        }
        foreach (var (value, what, max) in new[] { (name, "The site name", 40), (sub, "The line under the logo", 40), (footer, "The footer text", 120) })
            if (CommandCatalog.TextProblem(value, max, what, allowEmpty: true) is string bad)
                return Fail(bad);
        foreach (string? colour in new[] { accent, warn, bars, panel, page, notify })
            if (!string.IsNullOrEmpty(colour) && !SiteLook.IsColour(colour))
                return Fail("Colours look like #d83a30.");
        // Colours the light text wouldn't show on are refused, so the site can't be made unreadable
        foreach (var (field, colour) in new[] { ("accent", accent), ("warn", warn), ("bars", bars), ("panel", panel), ("page", page), ("notify", notify) })
            if (SiteLook.ColourProblem(field, colour) is string unreadable)
                return Fail(unreadable);

        SiteLook site = data.Site;
        site.Name = (name ?? "").Trim();
        site.Sub = (sub ?? "").Trim();
        site.Footer = (footer ?? "").Trim();
        site.Accent = accent ?? "";
        site.Warn = warn ?? "";
        site.Bars = bars ?? "";
        site.Panel = panel ?? "";
        site.Page = page ?? "";
        site.Notify = notify ?? "";
        site.Pattern = SiteLook.Patterns.Contains(pattern) ? pattern! : "none";
        site.PatternStrength = Math.Clamp(patternStrength, 1, 20);
        site.Radius = Math.Clamp(radius, 0, 16);
        site.Theme = SiteLook.ThemeOf(Request.Form["theme"].ToString());
        string background = Request.Form["background"].ToString();
        site.Background = OwnBackgrounds.Any(b => b.Id == background) ? background : "";
        site.Motion = Request.Form["motion"] == "true";
        site.Dim = int.TryParse(Request.Form["dim"], out int dim) ? Math.Clamp(dim, 0, 100) : 5;
        site.PanelOpacity = int.TryParse(Request.Form["panelOpacity"], out int panelOpacity) ? Math.Clamp(panelOpacity, 40, 100) : 88;
        site.PanelBlur = int.TryParse(Request.Form["panelBlur"], out int panelBlur) ? Math.Clamp(panelBlur, 0, 32) : 16;
        site.BarOpacity = int.TryParse(Request.Form["barOpacity"], out int barOpacity) ? Math.Clamp(barOpacity, 40, 100) : 94;
        string language = Request.Form["language"].ToString();
        site.Language = Lang.Languages.Any(l => l.Code == language) ? language : "";

        foreach (var (file, remove, name_, what, icon) in new[] { (logo, removeLogo, "logo", "The logo", false), (footerLogo, removeFooterLogo, "footer-logo", "The footer logo", false), (favicon, removeFavicon, "favicon", "The favicon", true) })
        {
            string current = name_ switch { "logo" => site.Logo, "footer-logo" => site.FooterLogo, _ => site.Favicon };
            string? saved = current;
            if (remove)
            {
                RemoveLogo(current);
                saved = "";
            }
            else if (file is { Length: > 0 })
            {
                var (stored, problem) = await StorePictureAsync(file, name_, what, icon);
                if (problem != null)
                    return Fail(problem);
                if (current != stored)
                    RemoveLogo(current);
                saved = stored;
            }
            switch (name_)
            {
                case "logo": site.Logo = saved!; break;
                case "footer-logo": site.FooterLogo = saved!; break;
                default: site.Favicon = saved!; break;
            }
        }
        return Saved(data, "Site look saved. Everyone sees it on their next page.");
    }

    // A logo, footer logo or favicon into data/branding as <name>.<type>. PNG, JPEG or WebP as they are; an SVG only as its
    // cleaned shapes and text (no scripts or links), drawn in the page in the theme's colours; an .ico for the favicon.
    private async Task<(string? File, string? Problem)> StorePictureAsync(IFormFile upload, string name, string what, bool icon)
    {
        if (upload.Length > SiteTheme.MaxLogoBytes)
            return (null, $"{what} can be up to 512 KB.");
        using var memory = new MemoryStream();
        await upload.CopyToAsync(memory);
        byte[] bytes = memory.ToArray();
        string? ext = SiteTheme.PictureType(bytes) ?? (icon && SiteTheme.IsIcon(bytes) ? ".ico" : null);
        string? svg = ext == null ? SvgLogo.Clean(bytes) : null;
        if (ext == null && svg == null)
            return (null, icon ? $"{what} has to be a PNG, ICO or SVG picture." : $"{what} has to be a PNG, JPEG, WebP or SVG picture.");
        string file = name + (ext ?? ".svg");
        if (svg != null)
            await System.IO.File.WriteAllTextAsync(Path.Combine(_theme.Folder, file), svg);
        else
            await System.IO.File.WriteAllBytesAsync(Path.Combine(_theme.Folder, file), bytes);
        return (file, null);
    }

    // A background (picture or short video) into data/backgrounds, or a map picture into data/maps named after its map
    public async Task<IActionResult> OnPostUploadAsync(IFormFile? file, string? kind, string? map, string? title)
    {
        if (!Owner)
            return LocalRedirect("/denied");
        if (CommandCatalog.TextProblem(title, 40, "The display name", allowEmpty: true) is string badTitle)
            return Fail(badTitle);
        if (file == null || file.Length == 0)
            return Fail("Choose a file first.");
        bool mapPicture = kind == "map";
        long limit = mapPicture ? 2 * 1024 * 1024 : MaxUpload;
        if (file.Length > limit)
            return Fail(mapPicture ? "Map pictures can be up to 2 MB." : "Backgrounds can be up to 25 MB (keep videos short and loop-friendly).");
        using var memory = new MemoryStream();
        await file.CopyToAsync(memory);
        byte[] bytes = memory.ToArray();
        string? ext = SiteTheme.PictureType(bytes) ?? (mapPicture ? null : VideoType(bytes));
        if (ext == null)
            return Fail(mapPicture ? "A map picture is a PNG, JPEG or WebP." : "A background is a PNG, JPEG or WebP picture, or a WebM or MP4 video.");
        string name = Clean(mapPicture ? map ?? "" : Path.GetFileNameWithoutExtension(file.FileName));
        if (name.Length == 0)
            return Fail(mapPicture ? "Type the map's name, like de_dust2." : "Give the file a name with letters or digits.");
        string folder = mapPicture ? _maps.Folder : _backgrounds.Folder;
        foreach (string old in Directory.EnumerateFiles(folder, name + ".*"))
            System.IO.File.Delete(old);
        await System.IO.File.WriteAllBytesAsync(Path.Combine(folder, name + ext), bytes);
        if (!mapPicture)
            SetDisplayName(name + ext, title);
        _audit.Write(Admin.Audit, mapPicture ? $"map picture {name}" : $"background {name}{ext}", null, true, "");
        TempData["Saved"] = mapPicture ? $"Picture for {name} saved: its cards show it." : $"{name}{ext} is in the background picker. Named after a map, Auto plays it on that map.";
        return RedirectToPage(null, null, null, mapPicture ? "maps" : "backgrounds");
    }

    public IActionResult OnPostDeleteFile(string? kind, string? name)
    {
        if (!Owner)
            return LocalRedirect("/denied");
        string folder = kind == "map" ? _maps.Folder : _backgrounds.Folder;
        name = Path.GetFileName(name ?? "");
        string path = Path.Combine(folder, name);
        if (name.Length == 0 || name == "README.txt" || !System.IO.File.Exists(path))
            return Fail("That file isn't there any more.");
        System.IO.File.Delete(path);
        if (kind != "map")
            SetDisplayName(name, null);
        _audit.Write(Admin.Audit, $"delete {(kind == "map" ? "map picture" : "background")} {name}", null, true, "");
        TempData["Saved"] = $"{name} deleted.";
        return RedirectToPage(null, null, null, kind == "map" ? "maps" : "backgrounds");
    }

    // A background's display name in the picker (empty: its scene's or file's own name)
    public IActionResult OnPostRenameBackground(string? name, string? title)
    {
        if (!Owner)
            return LocalRedirect("/denied");
        name = Path.GetFileName(name ?? "");
        if (name.Length == 0 || _backgrounds.Find(name) == null)
            return Fail("That background isn't there any more.");
        if (CommandCatalog.TextProblem(title, 40, "The display name", allowEmpty: true) is string bad)
            return Fail(bad);
        SetDisplayName(name, title);
        _audit.Write(Admin.Audit, $"background name {name}", null, true, (title ?? "").Trim());
        TempData["Saved"] = (title ?? "").Trim().Length > 0 ? $"{name} shows as {title!.Trim()}." : $"{name} shows under its own name again.";
        return RedirectToPage(null, null, null, "backgrounds");
    }

    private void SetDisplayName(string file, string? title)
    {
        PanelData data = _store.LoadForEdit();
        string? key = data.BackgroundNames.Keys.FirstOrDefault(k => k.Equals(file, StringComparison.OrdinalIgnoreCase));
        if (key != null)
            data.BackgroundNames.Remove(key);
        if ((title ?? "").Trim() is { Length: > 0 } clean)
            data.BackgroundNames[file] = clean;
        _store.Save(data);
    }

    // Names that are safe as file names: letters, digits, _ - and dots inside
    private static string Clean(string name) => new string(name.Trim().Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.').ToArray()).Trim('.') is { Length: > 0 and <= 80 } clean ? clean : "";

    private static string? VideoType(byte[] b) =>
        b.Length > 4 && b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3 ? ".webm"
        : b.Length > 12 && System.Text.Encoding.ASCII.GetString(b, 4, 4) == "ftyp" ? ".mp4"
        : null;

    // Community links (the footer): a label, an https address and one of the icons
    public IActionResult OnPostAddLink(string? label, string? url, string? icon)
    {
        if (!Owner)
            return LocalRedirect("/denied");
        label = (label ?? "").Trim();
        url = (url ?? "").Trim();
        if (CommandCatalog.TextProblem(label, 24, "The label", allowEmpty: false) is string bad)
            return Fail(bad);
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" || url.Length > 300 || url.Contains('"'))
            return Fail("The address starts with https://, like https://discord.gg/yourcode.");
        PanelData data = _store.LoadForEdit();
        if (data.Links.Count >= 8)
            return Fail("Up to 8 links: remove one first.");
        data.Links.Add(new LinkEntry { Label = label, Url = uri.ToString(), Icon = LinkEntry.Icons.Contains(icon ?? "") ? icon! : "link" });
        return Saved(data, $"{label} is in the footer.", $"link add {label}", "links");
    }

    // A link icon's colour in the footer: one of the owner's own, or back to the site's accent
    public IActionResult OnPostLinkColor(int index, string? color, string? accent)
    {
        if (!Owner)
            return LocalRedirect("/denied");
        PanelData data = _store.LoadForEdit();
        if (index < 0 || index >= data.Links.Count)
            return Fail("That link isn't there any more.");
        if (accent == null && !SiteLook.IsColour(color))
            return Fail("Pick a colour.");
        LinkEntry link = data.Links[index];
        link.Color = accent != null ? "" : color!.ToLowerInvariant();
        return Saved(data, $"{link.Label} is saved.", $"link edit {link.Label}", "links");
    }

    // Menu links: text links in the header to other web panels (skins, VIP, a store) or the panel's own pages.
    // index -1 adds one.
    public IActionResult OnPostMenuLink(int index, string? label, string? url, bool newTab, string? icon)
    {
        if (!Owner)
            return LocalRedirect("/denied");
        label = (label ?? "").Trim();
        url = (url ?? "").Trim();
        if (CommandCatalog.TextProblem(label, 20, "The label", allowEmpty: false) is string bad)
            return Fail(bad);
        if (MenuUrl(url) is not string clean)
            return Fail("The address starts with https://, or / for a panel page, like /rules.");
        PanelData data = _store.LoadForEdit();
        bool adding = index < 0;
        if (adding && data.MenuLinks.Count >= MenuLink.Max)
            return Fail($"Up to {MenuLink.Max} menu links: remove one first.");
        if (!adding && index >= data.MenuLinks.Count)
            return Fail("That link isn't there any more.");
        MenuLink link = adding ? new MenuLink() : data.MenuLinks[index];
        link.Label = label;
        link.Url = clean;
        link.NewTab = newTab;
        link.Icon = MenuLink.Icons.Contains(icon ?? "") ? icon! : "link";
        if (adding)
            data.MenuLinks.Add(link);
        return Saved(data, adding ? $"{label} is in the menu." : $"{label} is saved.", $"menu link {(adding ? "add" : "edit")} {label}", "menu");
    }

    public IActionResult OnPostRemoveMenuLink(int index)
    {
        if (!Owner)
            return LocalRedirect("/denied");
        PanelData data = _store.LoadForEdit();
        if (index < 0 || index >= data.MenuLinks.Count)
            return Fail("That link isn't there any more.");
        string label = data.MenuLinks[index].Label;
        data.MenuLinks.RemoveAt(index);
        return Saved(data, $"{label} is removed.", $"menu link remove {label}", "menu");
    }

    public IActionResult OnPostMenuName(string? name)
    {
        if (!Owner)
            return LocalRedirect("/denied");
        name = (name ?? "").Trim();
        if (name.Length > 0 && CommandCatalog.TextProblem(name, 20, "The menu name", allowEmpty: false) is string bad)
            return Fail(bad);
        PanelData data = _store.LoadForEdit();
        data.MenuName = name;
        return Saved(data, "Menu name saved.", $"menu name {name}", "menu");
    }

    // https://... or a panel path (/rules); null for anything else
    private static string? MenuUrl(string url)
    {
        if (url.Length is 0 or > 300 || url.Contains('"') || url.Contains('\\'))
            return null;
        if (url.StartsWith('/') && !url.StartsWith("//"))
            return url;
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == "https" ? uri.ToString() : null;
    }

    public IActionResult OnPostRemoveLink(int index)
    {
        if (!Owner)
            return LocalRedirect("/denied");
        PanelData data = _store.LoadForEdit();
        if (index < 0 || index >= data.Links.Count)
            return Fail("That link isn't there any more.");
        string label = data.Links[index].Label;
        data.Links.RemoveAt(index);
        return Saved(data, $"{label} is gone from the footer.", $"link remove {label}", "links");
    }

    // Features: the panel's own pages and parts the owner can switch off (off hides them everywhere)
    public IActionResult OnPostIntegrations()
    {
        if (!Owner)
            return LocalRedirect("/denied");
        PanelData data = _store.LoadForEdit();
        // Switches for plugins the panel no longer reads (K4, VIPCore, cs2-store) are dropped
        foreach (string old in data.Integrations.Keys.Where(k => !k.StartsWith("feature:")).ToList())
            data.Integrations.Remove(old);
        foreach (var (id, _, _) in Features.All)
            data.Integrations["feature:" + id] = Request.Form[$"feature-{id}"] == "true";
        string list = string.Join(", ", data.Integrations.Select(i => $"{i.Key} {(i.Value ? "on" : "off")}"));
        return Saved(data, "Features saved. Pages and links follow on everyone's next page.", $"features: {list}", "integrations");
    }

    // The server rules: plain text, one rule per line, "## " starts a section (shown on /rules, linked in the footer)
    public const int MaxRules = 8000;

    public IActionResult OnPostRules(string? rules)
    {
        if (!Owner)
            return LocalRedirect("/denied");
        string text = (rules ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length > MaxRules)
            return Fail($"The rules are up to {MaxRules:N0} characters.");
        if (text.Any(c => char.IsControl(c) && c != '\n'))
            return Fail("The rules can't contain control characters.");
        PanelData data = _store.LoadForEdit();
        data.Rules = text;
        return Saved(data, text.Length > 0 ? "Rules saved. They're on the Rules page, linked in the footer." : "Rules removed.", "rules", "rules");
    }

    private IActionResult Saved(PanelData data, string message, string? audit = null, string section = "look")
    {
        _store.Save(data);
        _audit.Write(Admin.Audit, audit ?? "site look", null, true, "");
        TempData["Saved"] = message;
        return RedirectToPage(null, null, null, section);
    }

    private IActionResult Fail(string message)
    {
        Message = message;
        Failed = true;
        return Page();
    }

    private void RemoveLogo(string file)
    {
        if (file.Length > 0 && System.IO.File.Exists(Path.Combine(_theme.Folder, file)))
            System.IO.File.Delete(Path.Combine(_theme.Folder, file));
    }
}
