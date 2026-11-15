namespace GmmPanel.Data;

// Map pictures for the dashboard cards and server pages: data/maps/<map>.png (or .jpg, .webp), shown to everyone. tools/GameArt
// fills it with CS2's own map screenshots (cropped square) from the owner's installed game; the Workshop addon's pictures
// (build-menu-images.ps1, from each Workshop map's Steam preview) cover Workshop maps. Nothing here is in git or a release.
public sealed class MapPictures
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".webp"] = "image/webp"
    };

    public MapPictures(PanelStore store)
    {
        Folder = Path.Combine(store.DataDirectory, "maps");
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    // The picture's address for a map name, or null
    public string? UrlFor(string? map)
    {
        if (string.IsNullOrEmpty(map) || !IsName(map))
            return null;
        foreach (string ext in Types.Keys)
            if (File.Exists(Path.Combine(Folder, map + ext)))
                return $"/maps/{Uri.EscapeDataString(map + ext)}";
        return null;
    }

    public (string Path, string Type)? Find(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
        if (!IsName(name) || !Types.TryGetValue(ext, out string? type))
            return null;
        string path = Path.Combine(Folder, name + ext);
        return File.Exists(path) ? (path, type) : null;
    }

    // Map names only: letters, digits, _ - and dots inside
    private static bool IsName(string name) => name.Length is > 0 and <= 80 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') && !name.Contains("..");
}
