using static GmmPanel.Data.Lang;

namespace GmmPanel.Pages;

// Times and lengths in the request's language (Lang.cs)
public static class Format
{
    // "2 min ago", for status and events
    public static string Ago(DateTimeOffset? at)
    {
        if (at == null)
            return T("never");
        TimeSpan age = DateTimeOffset.UtcNow - at.Value;
        if (age < TimeSpan.FromSeconds(10)) return T("just now");
        if (age < TimeSpan.FromMinutes(1)) return F("{0} s ago", (int)age.TotalSeconds);
        if (age < TimeSpan.FromHours(1)) return F("{0} min ago", (int)age.TotalMinutes);
        if (age < TimeSpan.FromDays(1)) return F("{0} h ago", (int)age.TotalHours);
        return Date(at.Value, seconds: false);
    }

    // A date and time: yyyy-MM-dd HH:mm:ss in English, else in the language's own short format
    public static string Time(DateTimeOffset at) => Date(at, seconds: true);
    // The same in UTC, for logs (the Audit log says "Time (UTC)")
    public static string TimeUtc(DateTimeOffset at) => Date(at, seconds: true, utc: true);

    // Play time: "45 min", "12 h", "3 d 4 h"
    public static string Hours(long seconds)
    {
        if (seconds < 3600) return F("{0} min", Math.Max(0, seconds / 60));
        if (seconds < 86400 * 2) return F("{0} h", seconds / 3600);
        return F("{0} d {1} h", seconds / 86400, seconds % 86400 / 3600);
    }

    // How long: "2 h 5 min"
    public static string Span(TimeSpan span) => span.TotalMinutes < 1 ? T("under a minute")
        : span.TotalHours < 1 ? F("{0} min", (int)span.TotalMinutes)
        : span.TotalDays < 1 ? F("{0} h {1} min", (int)span.TotalHours, span.Minutes)
        : F("{0} d {1} h", (int)span.TotalDays, span.Hours);

    // A country's flag (wwwroot/img/flags, flag-icons, MIT), or nothing for an unknown code. panel.js adds the country's
    // name in the page's language as the tooltip (data-region)
    private static readonly HashSet<string> FlagCodes = new(StringComparer.OrdinalIgnoreCase) { "AD", "AE", "AF", "AG", "AI", "AL", "AM", "AO", "AQ", "AR", "AS", "AT", "AU", "AW", "AX", "AZ", "BA", "BB", "BD", "BE", "BF", "BG", "BH", "BI", "BJ", "BL", "BM", "BN", "BO", "BQ", "BR", "BS", "BT", "BV", "BW", "BY", "BZ", "CA", "CC", "CD", "CF", "CG", "CH", "CI", "CK", "CL", "CM", "CN", "CO", "CP", "CR", "CU", "CV", "CW", "CX", "CY", "CZ", "DE", "DG", "DJ", "DK", "DM", "DO", "DZ", "EC", "EE", "EG", "EH", "ER", "ES", "ET", "EU", "FI", "FJ", "FK", "FM", "FO", "FR", "GA", "GB", "GD", "GE", "GF", "GG", "GH", "GI", "GL", "GM", "GN", "GP", "GQ", "GR", "GS", "GT", "GU", "GW", "GY", "HK", "HM", "HN", "HR", "HT", "HU", "IC", "ID", "IE", "IL", "IM", "IN", "IO", "IQ", "IR", "IS", "IT", "JE", "JM", "JO", "JP", "KE", "KG", "KH", "KI", "KM", "KN", "KP", "KR", "KW", "KY", "KZ", "LA", "LB", "LC", "LI", "LK", "LR", "LS", "LT", "LU", "LV", "LY", "MA", "MC", "MD", "ME", "MF", "MG", "MH", "MK", "ML", "MM", "MN", "MO", "MP", "MQ", "MR", "MS", "MT", "MU", "MV", "MW", "MX", "MY", "MZ", "NA", "NC", "NE", "NF", "NG", "NI", "NL", "NO", "NP", "NR", "NU", "NZ", "OM", "PA", "PC", "PE", "PF", "PG", "PH", "PK", "PL", "PM", "PN", "PR", "PS", "PT", "PW", "PY", "QA", "RE", "RO", "RS", "RU", "RW", "SA", "SB", "SC", "SD", "SE", "SG", "SH", "SI", "SJ", "SK", "SL", "SM", "SN", "SO", "SR", "SS", "ST", "SV", "SX", "SY", "SZ", "TC", "TD", "TF", "TG", "TH", "TJ", "TK", "TL", "TM", "TN", "TO", "TR", "TT", "TV", "TW", "TZ", "UA", "UG", "UM", "UN", "US", "UY", "UZ", "VA", "VC", "VE", "VG", "VI", "VN", "VU", "WF", "WS", "XK", "XX", "YE", "YT", "ZA", "ZM", "ZW" };
    public static Microsoft.AspNetCore.Html.IHtmlContent Flag(string? country) => country is { Length: 2 } && FlagCodes.Contains(country)
        ? new Microsoft.AspNetCore.Html.HtmlString($"<img class=\"flag\" src=\"/img/flags/{country.ToLowerInvariant()}.svg\" alt=\"{country.ToUpperInvariant()}\" title=\"{country.ToUpperInvariant()}\" data-region=\"{country.ToUpperInvariant()}\" width=\"20\" height=\"15\" loading=\"lazy\">")
        : Microsoft.AspNetCore.Html.HtmlString.Empty;
}
