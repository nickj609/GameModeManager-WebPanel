using System.IO.Compression;

namespace GmmPanel.Data;

// The panel's settings and records as one zip (Panel page, panel admins), and back. Never in it: data/keys (sign-in keys,
// tied to this Windows account), logs, the dev tokens. The Discord webhook (a secret) only when asked for. A restore takes
// only the files a backup can hold, refuses paths that leave the data folder, and first saves what's there now as
// before-restore-<time>.zip.
public sealed class Backup
{
    private const long MaxUnpackedBytes = 4L * 1024 * 1024 * 1024;   // Unpacked: far above a real backup with its pictures and videos
    private static readonly string[] Files = { "panel.json", "tickets.json", "notes.json", "users.json", "jobs.json", "audit.jsonl" };
    private static readonly string[] Media = { "branding/", "maps/", "ranks/", "backgrounds/" };
    private const string History = "history/";
    private const string Secret = "discord.json";
    public const long MaxRestoreBytes = 300L * 1024 * 1024;

    private readonly PanelStore _store;

    public Backup(PanelStore store)
    {
        _store = store;
    }

    public void Write(Stream output, bool history, bool media, bool secrets)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        string root = _store.DataDirectory;
        foreach (string file in Files.Concat(secrets ? new[] { Secret } : Array.Empty<string>()))
            if (File.Exists(Path.Combine(root, file)))
                zip.CreateEntryFromFile(Path.Combine(root, file), file, CompressionLevel.Optimal);
        foreach (string folder in (media ? Media : Array.Empty<string>()).Concat(history ? new[] { History } : Array.Empty<string>()))
        {
            string dir = Path.Combine(root, folder.TrimEnd('/'));
            if (!Directory.Exists(dir))
                continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                zip.CreateEntryFromFile(file, Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Optimal);
        }
    }

    // Null when restored; else why not
    public string? Restore(Stream input, out int restored)
    {
        restored = 0;
        string root = Path.GetFullPath(_store.DataDirectory);
        ZipArchive zip;
        try { zip = new ZipArchive(input, ZipArchiveMode.Read); }
        catch (InvalidDataException) { return "That isn't a zip file."; }
        using (zip)
        {
            var take = new List<(ZipArchiveEntry Entry, string Path)>();
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                    continue;
                string name = entry.FullName.Replace('\\', '/');
                bool allowed = Files.Contains(name) || name == Secret || Media.Any(m => name.StartsWith(m)) || name.StartsWith(History);
                string full = Path.GetFullPath(Path.Combine(root, name));
                if (!allowed || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || name.Contains(".."))
                    return $"The backup has a file the panel doesn't restore ({name}). Use a zip from \"Download a backup\".";
                take.Add((entry, full));
            }
            // A zip that unpacks far bigger than any real backup (a "zip bomb") would fill the disk: refused before
            // anything is written. .NET also stops an entry that inflates past the size it declares.
            if (take.Sum(t => t.Entry.Length) > MaxUnpackedBytes)
                return "The backup unpacks to more than 4 GB: it isn't a panel backup.";
            if (!take.Any(t => t.Entry.FullName == "panel.json"))
                return "The backup has no panel.json: it isn't a panel backup.";

            // What's there now, in case the restore was a mistake
            using (FileStream before = File.Create(Path.Combine(root, $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.zip")))
                Write(before, history: true, media: true, secrets: true);

            foreach (var (entry, full) in take)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                entry.ExtractToFile(full + ".tmp", overwrite: true);
                File.Move(full + ".tmp", full, overwrite: true);
                restored++;
            }
        }
        return null;
    }
}
