using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GmmPanel.Data;
using Microsoft.AspNetCore.DataProtection;

namespace GmmPanel.Auth;

// The setup wizard's state. While panel.json has no admin, the panel prints a one-time code in its own log (only someone
// who can see the panel's console or log has it), and /setup asks for it before the Steam sign-in that makes that account
// the first panel admin. After that the code means nothing and /setup only adds servers, for panel admins.
public sealed class SetupState
{
    private const string Cookie = "gmm.setup";
    private readonly PanelStore _store;
    private readonly ILogger<SetupState> _logger;
    private readonly IDataProtector _protector;
    private readonly object _lock = new();

    public SetupState(PanelStore store, ILogger<SetupState> logger, IDataProtectionProvider protection)
    {
        _store = store;
        _logger = logger;
        _protector = protection.CreateProtector("gmmpanel.setup");
        // 10 characters from an alphabet without look-alikes (no 0/O, 1/I/L), shown as XXXXX-XXXXX
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        Code = new string(Enumerable.Range(0, 10).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
    }

    public string Code { get; }
    public string ShownCode => $"{Code[..5]}-{Code[5..]}";

    // No panel admin yet: everything sends you to /setup
    public bool Needed => !_store.Current.Admins.Any(admin => Roles.IsValid(admin.Role));

    public void LogCode()
    {
        if (Needed)
            _logger.LogWarning("No panel admin yet. Open the panel's /setup page and enter this setup code: {Code}", ShownCode);
    }

    // Ten tries a minute from one address, so the code can't be guessed (the wait step's polling isn't counted)
    private readonly System.Threading.RateLimiting.PartitionedRateLimiter<string> _tries = System.Threading.RateLimiting.PartitionedRateLimiter.Create<string, string>(address =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(address, _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    public bool MayTry(HttpContext context)
    {
        using var lease = _tries.AttemptAcquire(context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        return lease.IsAcquired;
    }

    public bool CheckCode(string? typed)
    {
        string given = new string((typed ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(Code));
    }

    // After the right code: a short-lived, signed cookie that carries the name to give the new admin through the Steam round trip
    public void Prove(HttpContext context, string name)
    {
        string value = _protector.Protect(JsonSerializer.Serialize(new Proof(name, DateTimeOffset.UtcNow.AddMinutes(15))));
        context.Response.Cookies.Append(Cookie, value, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,   // Steam's return is a top-level GET, which Lax still sends
            Secure = context.Request.IsHttps,
            MaxAge = TimeSpan.FromMinutes(15),
            IsEssential = true
        });
    }

    public string? ProvenName(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(Cookie, out string? value))
            return null;
        try
        {
            Proof? proof = JsonSerializer.Deserialize<Proof>(_protector.Unprotect(value));
            return proof != null && proof.Until > DateTimeOffset.UtcNow ? proof.Name : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    // The Steam (or Development) sign-in that finishes step 1: the first panel admin, only while there's none and only with the
    // cookie the right code gave. Null otherwise.
    public AdminEntry? Claim(HttpContext context, string steamId)
    {
        lock (_lock)
        {
            string? name = ProvenName(context);
            if (!Needed || name == null)
                return null;
            PanelData data = _store.LoadForEdit();
            var entry = new AdminEntry { SteamId = steamId, Name = name, Role = Roles.Admin };
            data.Admins.Add(entry);
            _store.Save(data);
            context.Response.Cookies.Delete(Cookie);
            _logger.LogInformation("Setup: {Name} ({SteamId}) is the first panel admin", name, steamId);
            return entry;
        }
    }

    private sealed record Proof(string Name, DateTimeOffset Until);
}
