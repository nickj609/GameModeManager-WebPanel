using System.Text;
using SkiaSharp;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

// GameArt: takes CS2's own map screenshots out of an installed game and writes them where the panel reads them, so the
// panel can show the game's art without carrying any of it:
//   data/backgrounds/<map>.jpg   for each of CS2's menu scenes (Settings, Menu Scenery), its map's 1080p screenshot; the
//                                panel shows it as the scene's name ("Dust II") and "Auto" while a server is on that map
//   data/maps/<map>.jpg          the same, cropped square, for the server cards and pages
// Run it on a machine with CS2 (or the game server's own install) and point it at the panel's data folder; run it again
// after a CS2 update that adds maps. Nothing is overwritten unless --replace is given, so the owner's own pictures stay.
//
//   dotnet run --project tools/GameArt -- <CS2 folder or pak01_dir.vpk> <panel data folder> [--only backgrounds,maps] [--replace]

const string Screenshots = "panorama/images/map_icons/screenshots/1080p/";
// CS2's menu scenes (maps/<map>_vanity.vpk) that have a map screenshot, without Ancient's night one; Warehouse is menu-only,
// with no picture in the game.
// The panel names them (Data/Backgrounds.cs MenuScenes).
string[] menuScenes = { "de_ancient", "de_anubis", "ar_baggage", "de_cache", "de_dust2", "de_inferno", "cs_italy",
    "de_mirage", "de_nuke", "cs_office", "de_overpass", "de_train", "de_vertigo" };

if (args.Length < 2 || args.Contains("--help"))
{
    Console.WriteLine("""
        GameArt: CS2's menu scenery as backgrounds, and its map screenshots as map pictures, for the GameModeManager panel.

          dotnet run --project tools/GameArt -- <CS2 folder or pak01_dir.vpk> <panel data folder> [options]

          --only <list>    backgrounds, maps (comma-separated; both without it)
          --replace        overwrite pictures already there (the owner's own uploads are kept without it)
        """);
    return 1;
}

string vpkPath = FindVpk(args[0]);
string dataFolder = Path.GetFullPath(args[1]);
bool replace = args.Contains("--replace");
HashSet<string> only = (Value("--only") ?? "backgrounds,maps").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

if (!Directory.Exists(dataFolder))
{
    Console.Error.WriteLine($"No folder at {dataFolder}. Point it at the panel's data folder (the one with panel.json).");
    return 1;
}

using var package = new Package();
package.Read(vpkPath);
Console.WriteLine($"Reading {vpkPath}");

int written = 0, kept = 0;

// Map screenshots: <map>_png.vtex_c. A map with several (ar_baggage_1, _2 ...) also has the plain one; only the plain one
// is used, so each map gets one picture
if (only.Contains("backgrounds") || only.Contains("maps"))
{
    var shots = package.Entries!.TryGetValue("vtex_c", out var textures)
        ? textures.Where(e => (e.DirectoryName + "/").Equals(Screenshots, StringComparison.OrdinalIgnoreCase) && e.FileName.EndsWith("_png", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Entry: e, Map: e.FileName[..^4]))
            .Where(s => !System.Text.RegularExpressions.Regex.IsMatch(s.Map, @"_\d+$") && s.Map is not ("random" or "lobby_mapveto"))
            .OrderBy(s => s.Map).ToList()
        : new();
    Console.WriteLine($"{shots.Count} map screenshots");
    foreach (var (entry, map) in shots)
    {
        using SKBitmap? picture = Texture(entry);
        if (picture == null)
        {
            Console.WriteLine($"  {map}: couldn't decode, skipped");
            continue;
        }
        if (only.Contains("backgrounds") && menuScenes.Contains(map))
            Save(picture, Path.Combine(dataFolder, "backgrounds"), map, ".jpg", 1920, 1080, square: false);
        if (only.Contains("maps"))
            Save(picture, Path.Combine(dataFolder, "maps"), map, ".jpg", 512, 512, square: true);
    }
}

Console.WriteLine($"Done: {written} written, {kept} already there (kept{(replace ? "" : "; --replace overwrites them")}).");
return 0;

string? Value(string name)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

static string FindVpk(string path)
{
    if (File.Exists(path))
        return path;
    foreach (string candidate in new[] { Path.Combine(path, "game", "csgo", "pak01_dir.vpk"), Path.Combine(path, "csgo", "pak01_dir.vpk"), Path.Combine(path, "pak01_dir.vpk") })
        if (File.Exists(candidate))
            return candidate;
    throw new FileNotFoundException($"No pak01_dir.vpk under {path}. Give the CS2 folder (…\\Counter-Strike Global Offensive) or the file itself.");
}

SKBitmap? Texture(PackageEntry entry)
{
    package.ReadEntry(entry, out byte[] bytes);
    using var resource = new Resource();
    resource.Read(new MemoryStream(bytes));
    return resource.DataBlock is Texture texture ? texture.GenerateBitmap() : null;
}

// Writes the picture at most width x height (the middle square when square), as JPEG or PNG; keeps a picture already
// there (any type) unless --replace
void Save(SKBitmap source, string folder, string name, string ext, int width, int height, bool square)
{
    Directory.CreateDirectory(folder);
    string[] existing = new[] { ".png", ".jpg", ".jpeg", ".webp" }.Select(e => Path.Combine(folder, name + e)).Where(File.Exists).ToArray();
    if (existing.Length > 0 && !replace)
    {
        kept++;
        return;
    }
    foreach (string old in existing)
        File.Delete(old);

    SKRectI crop = square
        ? new SKRectI((source.Width - Math.Min(source.Width, source.Height)) / 2, (source.Height - Math.Min(source.Width, source.Height)) / 2,
            (source.Width + Math.Min(source.Width, source.Height)) / 2, (source.Height + Math.Min(source.Width, source.Height)) / 2)
        : new SKRectI(0, 0, source.Width, source.Height);
    float scale = Math.Min(1f, Math.Min((float)width / crop.Width, (float)height / crop.Height));
    int w = Math.Max(1, (int)Math.Round(crop.Width * scale)), h = Math.Max(1, (int)Math.Round(crop.Height * scale));
    using var output = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(output))
    {
        canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, new SKRect(crop.Left, crop.Top, crop.Right, crop.Bottom), new SKRect(0, 0, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
    }
    using SKData data = output.Encode(ext == ".png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 88);
    File.WriteAllBytes(Path.Combine(folder, name + ext), data.ToArray());
    written++;
}
