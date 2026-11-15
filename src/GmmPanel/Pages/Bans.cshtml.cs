using GmmPanel.Agents;
using GmmPanel.Protocol;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GmmPanel.Pages;

// A public list (anyone, signed in or not): Bans shows CS2-SimpleAdmin's bans and mutes, Stats the panel's own play-time boards. Fewer
// columns than Moderate (no admin names), read through PublicData's cache.
[EnableRateLimiting("public")]
public abstract class PublicListModel : PanelPage
{
    private readonly PublicData _data;

    protected PublicListModel(PublicData data)
    {
        _data = data;
    }

    // The lists this page can show: (id in the address, source, list, label)
    public abstract (string Id, string Source, string List, string Label)[] Lists { get; }
    public abstract string Path { get; }

    [BindProperty(SupportsGet = true)]
    public string? Show { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Search { get; set; }

    // The asked list when it's offered, else the first that is
    public (string Id, string Source, string List, string Label) Current =>
        Lists.FirstOrDefault(l => l.Id == Show && Offered(l)) is { Id: not null } found ? found
        : Lists.FirstOrDefault(Offered) is { Id: not null } first ? first : Lists[0];

    // A plugin's list when a connected server has it; the panel's own ("panel") always
    public bool Offered((string Id, string Source, string List, string Label) list) => list.Source == PanelSource ? PanelListsOn : _data.Offers(list.Source, list.List);
    protected virtual bool PanelListsOn => true;
    public const string PanelSource = "panel";

    // A list the panel keeps itself (Stats: play time from player history), one page of it
    protected virtual DataPage? PanelList(string list, string search, int page) => null;
    public CommandResult? Result { get; private set; }

    public string ListUrl(string? show = null, int? page = null, string? search = null) =>
        $"{Path}?show={Uri.EscapeDataString(show ?? Current.Id)}&p={page ?? PageNumber}&q={Uri.EscapeDataString(search ?? Search ?? "")}";

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnGetListAsync()
    {
        await LoadAsync();
        return Partial("_PublicTable", this);
    }

    private async Task LoadAsync()
    {
        string search = (Search ?? "").Trim();
        if (search.Length > 64 || search.Any(c => c is '"' or ';' or '\\' || char.IsControl(c)))
            search = "";
        int number = Math.Clamp(PageNumber, 1, 10000);
        Result = Current.Source == PanelSource
            ? PanelList(Current.List, search, number) is { } own ? new CommandResult(true, "", Data: own) : new CommandResult(false, "That list isn't offered.")
            : await _data.PublicAsync(Current.Source, Current.List, number, search, Current.Source == "simpleadmin" ? "all" : "");
        await PicturesAsync();
    }

    // The page's players' Steam pictures (and names, for a ban made by SteamID alone): the ones not known yet are asked for,
    // and the page waits at most 1.5 s for them; the rest show next time (Steam's answers are kept 6 hours)
    private async Task PicturesAsync()
    {
        if (Result?.Data is not { } data || data.Columns.FirstOrDefault(c => c.Kind == "steamid")?.Key is not { } key)
            return;
        var steam = HttpContext.RequestServices.GetRequiredService<GmmPanel.Auth.SteamProfile>();
        var missing = data.Rows.Select(r => r.GetValueOrDefault(key)).OfType<string>().Distinct()
            .Where(id => Commands.CommandCatalog.IsSteamId(id) && steam.KnownAvatar(id) == null).Take(25)
            .Select(id => steam.AvatarAsync(id)).ToList();
        if (missing.Count > 0)
            await Task.WhenAny(Task.WhenAll(missing), Task.Delay(1500));
    }
}

public class BansModel : PublicListModel
{
    public BansModel(PublicData data) : base(data) { }

    public override (string Id, string Source, string List, string Label)[] Lists { get; } =
    {
        ("bans", "simpleadmin", "bans", "Bans"),
        ("mutes", "simpleadmin", "mutes", "Mutes & gags")
    };

    public override string Path => "/bans";
}
