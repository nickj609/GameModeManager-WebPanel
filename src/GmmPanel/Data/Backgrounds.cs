namespace GmmPanel.Data;

// The backgrounds behind the panel, like CS2's main menu: the owner's files in data/backgrounds, videos (.webm, .mp4) or
// pictures. tools/GameArt fills it with CS2's own menu scenery (each scene's map screenshot, from the owner's installed
// game); those show under their names in the game ("Dust II", "Mirage") and to everyone. Other files are the
// owner's own (clips they recorded ...), shown to signed-in people, or everyone with Site settings' option. None ship.
// Site settings can give any of them a display name (panel.json BackgroundNames), which wins over the scene's or file's name.
// A file named after a map (de_dust2.jpg) is what "Auto" shows while a server is on that map.
// Kind: still or video. Map: the file's name, which "Auto" matches. Scene: one of CS2's menu scenes.
public sealed record Background(string Id, string Name, string Url, string Kind, string Map, bool Scene);

public sealed class Backgrounds
{
    // CS2's Menu Scenery (Settings, Game), by the map each scene is built from, with the name the game shows (Ancient: its
    // day scene; the night one is left out). Warehouse is a menu-only scene with no screenshot in the game, so it has no picture
    public static readonly IReadOnlyDictionary<string, string> MenuScenes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["de_ancient"] = "Ancient", ["de_anubis"] = "Anubis", ["ar_baggage"] = "Baggage",
        ["de_cache"] = "Cache", ["de_dust2"] = "Dust II", ["de_inferno"] = "Inferno", ["cs_italy"] = "Italy", ["de_mirage"] = "Mirage",
        ["de_nuke"] = "Nuke", ["cs_office"] = "Office", ["de_overpass"] = "Overpass", ["de_train"] = "Train", ["de_vertigo"] = "Vertigo"
    };

    private static readonly Dictionary<string, (string Type, string Kind)> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".webm"] = ("video/webm", "video"),
        [".mp4"] = ("video/mp4", "video"),
        [".jpg"] = ("image/jpeg", "still"),
        [".jpeg"] = ("image/jpeg", "still"),
        [".png"] = ("image/png", "still"),
        [".webp"] = ("image/webp", "still")
    };

    private const string Readme = """
        Backgrounds for the GameModeManager panel.

        Put videos (.webm or .mp4) or pictures (.jpg, .png, .webp) here. Each one shows in the background picker
        (bottom right of the panel) for signed-in admins. Name a file after a map, like de_dust2.webm, and "Auto"
        plays it while a server is on that map.

        Files here stay on this machine: they aren't in git or in any release. Keep videos short and loop-friendly,
        and under about 20 MB, since each admin's browser downloads them through the tunnel.
        """;

    private readonly PanelStore _store;

    public Backgrounds(PanelStore store)
    {
        _store = store;
        Folder = Path.Combine(store.DataDirectory, "backgrounds");
        Directory.CreateDirectory(Folder);
        string readme = Path.Combine(Folder, "README.txt");
        if (!File.Exists(readme))
            File.WriteAllText(readme, Readme);
    }

    public string Folder { get; }

    // The owner's files, by name
    public List<Background> Own()
    {
        return Directory.EnumerateFiles(Folder)
            .Select(path => (Name: Path.GetFileName(path), Ext: Path.GetExtension(path)))
            .Where(file => Types.ContainsKey(file.Ext))
            .Select(file => (file.Name, file.Ext, Map: Path.GetFileNameWithoutExtension(file.Name)))
            .Select(file => new Background(
                "own:" + file.Name,
                DisplayName(file.Name) ?? (MenuScenes.TryGetValue(file.Map, out string? scene) ? scene : file.Map),
                "/media/" + Uri.EscapeDataString(file.Name),
                Types[file.Ext].Kind,
                file.Map,
                MenuScenes.ContainsKey(file.Map)))
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // The owner's display name for a file, or null
    public string? DisplayName(string file) =>
        _store.Current.BackgroundNames.FirstOrDefault(n => n.Key.Equals(file, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } name ? name : null;

    // A file in the folder by its exact name, and its type; null for anything else (no paths, no other types)
    public (string Path, string Type)? Find(string name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || !Types.TryGetValue(Path.GetExtension(name), out var type))
            return null;
        string? path = Directory.EnumerateFiles(Folder).FirstOrDefault(file => Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase));
        return path == null ? null : (path, type.Type);
    }
}
