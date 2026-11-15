using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace GmmPanel.Auth;

// A Steam profile's name and picture, from its public XML page (no Web API key needed). Players aren't in panel.json, so
// the name is what the panel shows them by (the SteamID when the profile can't be read). The picture is only a link to
// Steam's own image server, kept for 6 hours; the browser loads it from there (the CSP allows only Steam's avatar hosts).
public sealed partial class SteamProfile
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;

    public SteamProfile(HttpClient http, IMemoryCache cache)
    {
        _http = http;
        _cache = cache;
        _http.Timeout = TimeSpan.FromSeconds(4);
    }

    // Steam's avatar hosts, also listed in the CSP's img-src (SecurityHeaders.cs)
    public static readonly string[] AvatarHosts = { "avatars.steamstatic.com", "avatars.akamai.steamstatic.com", "avatars.cloudflare.steamstatic.com", "avatars.fastly.steamstatic.com" };

    public async Task<string> NameAsync(string steamId)
    {
        string? xml = await ReadAsync(steamId);
        Match match = xml == null ? Match.Empty : SteamName().Match(xml);
        string name = match.Success ? match.Groups[1].Value.Trim() : "";
        return name.Length is > 0 and <= 64 ? name : steamId;
    }

    // The medium (64 px) picture's address, or null. Asked at most every 6 hours per person (a failure: every 10 minutes).
    public async Task<string?> AvatarAsync(string steamId)
    {
        string key = "avatar:" + steamId;
        if (_cache.TryGetValue(key, out string? known))
            return known;
        string? xml = await ReadAsync(steamId);
        Match match = xml == null ? Match.Empty : SteamAvatar().Match(xml);
        string? url = match.Success && Uri.TryCreate(match.Groups[1].Value.Trim(), UriKind.Absolute, out Uri? uri)
            && uri.Scheme == "https" && AvatarHosts.Contains(uri.Host) ? uri.ToString() : null;
        _cache.Set(key, url, url != null ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(10));
        // The same page has the name: kept too, for lists that only have a SteamID (server admins)
        Match name = xml == null ? Match.Empty : SteamName().Match(xml);
        if (name.Success && name.Groups[1].Value.Trim() is { Length: > 0 and <= 64 } steamName)
            _cache.Set("name:" + steamId, steamName, TimeSpan.FromHours(6));
        return url;
    }

    // Without asking Steam: what's known already (the header uses this so a page never waits on Steam)
    public string? KnownAvatar(string steamId) => _cache.TryGetValue("avatar:" + steamId, out string? url) ? url : null;
    public string? KnownName(string steamId) => _cache.TryGetValue("name:" + steamId, out string? name) ? name : null;

    private async Task<string?> ReadAsync(string steamId)
    {
        if (steamId.Length != 17 || !steamId.All(char.IsAsciiDigit))
            return null;
        try
        {
            return await _http.GetStringAsync($"https://steamcommunity.com/profiles/{steamId}?xml=1");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<steamID><!\[CDATA\[(.*?)\]\]></steamID>", RegexOptions.Singleline)]
    private static partial Regex SteamName();

    [GeneratedRegex(@"<avatarMedium><!\[CDATA\[(.*?)\]\]></avatarMedium>", RegexOptions.Singleline)]
    private static partial Regex SteamAvatar();
}
