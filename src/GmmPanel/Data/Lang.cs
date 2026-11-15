using System.Reflection;
using System.Text.Json;

namespace GmmPanel.Data;

// The panel's languages: GameModeManager's 13. Each lang/<code>.json maps the English text to its translation, so a missing
// line shows in English instead of breaking. The request's language (the gmm.lang cookie, else the site's default, else the
// browser's own, else English) is set once per request by Program.cs; pages and Format then just call T("...") or F("... {0}", n).
// Player-facing pages are translated; staff tools are in English for now.
public static class Lang
{
    public const string Cookie = "gmm.lang";

    // Code, and the language's own name for the picker
    public static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"), ("de", "Deutsch"), ("es", "Español"), ("fr", "Français"), ("hu", "Magyar"), ("lv", "Latviešu"),
        ("nl", "Nederlands"), ("pl", "Polski"), ("pt-BR", "Português (Brasil)"), ("ru", "Русский"), ("tr", "Türkçe"),
        ("uk", "Українська"), ("zh-Hans", "简体中文")
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Texts = Load();
    private static readonly AsyncLocal<string?> CurrentCode = new();

    public static string Current
    {
        get => CurrentCode.Value ?? "en";
        set => CurrentCode.Value = value;
    }

    // The text in the request's language
    public static string T(string english) =>
        Current != "en" && Texts.TryGetValue(Current, out var texts) && texts.TryGetValue(english, out string? text) && text.Length > 0 ? text : english;

    // With values: F("{0} playing now", 5)
    public static string F(string english, params object?[] values)
    {
        try
        {
            return string.Format(T(english), values);
        }
        catch (FormatException)
        {
            return string.Format(english, values);   // A translation with broken placeholders: English
        }
    }

    // panel.js's text (its t("...")), for the page's meta name="i18n": only what this language translates
    private static readonly string[] ScriptTexts =
    {
        "Too many requests. Wait a moment.", "You're signed out. Reload the page.", "The panel couldn't do that ({0}).", "Copied",
        "Are you sure?", "Cancel", "Yes", "Turn on sv_cheats?", "Turn it on", "Save to GameModeManager.json?", "Schedule it", "Save it",
        "It also writes the change into GameModeManager.json on each ticked server, so it lasts after a restart. The file's comments go; a copy from before the panel's first change is kept as GameModeManager.json.before-panel.bak. A deploy that uploads its own GameModeManager.json replaces it.",
        "Changes need a match restart", "{0} applies after the match restarts.", "Restart now", "Restart later", "Replay round",
        "End round", "Restart match", "Next round", "Changes apply next round", "{0} applies from the next round.", "On", "Off"
    };

    public static Dictionary<string, string> Script() =>
        ScriptTexts.Select(s => (s, T(s))).Where(p => p.Item2 != p.s).ToDictionary(p => p.s, p => p.Item2);

    // A date and time in the request's language's own order (the build is culture-invariant, so the patterns are here):
    // 2026-11-13 21:05 in English, 13.11.2026 21:05 in German ...
    public static string Date(DateTimeOffset at, bool seconds, bool utc = false)
    {
        string time = seconds ? "HH:mm:ss" : "HH:mm";
        string pattern = Current switch
        {
            "de" or "pl" or "ru" or "uk" or "lv" or "tr" => "dd.MM.yyyy",
            "fr" or "es" or "pt-BR" => "dd/MM/yyyy",
            "nl" => "dd-MM-yyyy",
            "hu" => "yyyy. MM. dd.",
            "zh-Hans" => "yyyy/MM/dd",
            _ => "yyyy-MM-dd"
        };
        return (utc ? at.ToUniversalTime() : at.ToLocalTime()).ToString($"{pattern} {time}", System.Globalization.CultureInfo.InvariantCulture);
    }

    // English, singular or plural: N(1, "{0} new player", "{0} new players")
    public static string N(long count, string one, string many) => F(count == 1 ? one : many, count.ToString("N0"));

    // The person's own pick (the cookie), else the site's default (Site settings), else the browser's best match, else English
    public static string Pick(string? cookie, string? siteDefault, string? acceptLanguage)
    {
        if (cookie != null && Languages.Any(l => l.Code == cookie))
            return cookie;
        if (!string.IsNullOrEmpty(siteDefault) && Languages.Any(l => l.Code == siteDefault))
            return siteDefault;
        foreach (string part in (acceptLanguage ?? "").Split(','))
        {
            string tag = part.Split(';')[0].Trim();
            if (tag.Length == 0)
                continue;
            if (Languages.FirstOrDefault(l => l.Code.Equals(tag, StringComparison.OrdinalIgnoreCase)).Code is string exact)
                return exact;
            string primary = tag.Split('-')[0].ToLowerInvariant();
            if (primary == "zh")
                return "zh-Hans";
            if (primary == "pt")
                return "pt-BR";
            if (Languages.FirstOrDefault(l => l.Code == primary).Code is string near)
                return near;
        }
        return "en";
    }

    // Embedded lang/<code>.json (the single-file exe carries them)
    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var all = new Dictionary<string, Dictionary<string, string>>();
        Assembly assembly = typeof(Lang).Assembly;
        foreach (var (code, _) in Languages)
        {
            using Stream? stream = assembly.GetManifestResourceStream($"lang.{code}.json");
            if (stream == null)
                continue;
            all[code] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
        }
        return all;
    }
}
