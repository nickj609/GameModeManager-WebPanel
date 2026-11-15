using System.Security.Cryptography;
using System.Text.Json;

namespace GmmPanel.Data;

// Staff's private notes on a player ("warned about mic spam"), in data/notes.json by SteamID. Only staff who can open the
// player page see them; the player never does. The writer, or a panel admin, can delete one.
public sealed class PlayerNotes
{
    public const int MaxText = 500;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<Note>> _notes;

    public PlayerNotes(PanelStore store)
    {
        _path = Path.Combine(store.DataDirectory, "notes.json");
        _notes = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, List<Note>>>(File.ReadAllText(_path), Json) ?? new() : new();
    }

    public sealed record Note(string Id, DateTimeOffset At, string BySteamId, string ByName, string Text);

    public List<Note> For(string steamId)
    {
        lock (_lock)
            return _notes.TryGetValue(steamId, out var list) ? list.OrderByDescending(n => n.At).ToList() : new();
    }

    public int Count(string steamId)
    {
        lock (_lock)
            return _notes.TryGetValue(steamId, out var list) ? list.Count : 0;
    }

    public void Add(string steamId, string bySteamId, string byName, string text)
    {
        lock (_lock)
        {
            if (!_notes.TryGetValue(steamId, out var list))
                _notes[steamId] = list = new();
            list.Add(new Note(Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant(), DateTimeOffset.UtcNow, bySteamId, byName, text));
            Save();
        }
    }

    // Null when deleted; else why not
    public string? Delete(string steamId, string id, string bySteamId, bool panelAdmin)
    {
        lock (_lock)
        {
            if (!_notes.TryGetValue(steamId, out var list) || list.FirstOrDefault(n => n.Id == id) is not { } note)
                return "That note isn't there any more.";
            if (note.BySteamId != bySteamId && !panelAdmin)
                return "Only whoever wrote it, or a panel admin, can delete a note.";
            list.Remove(note);
            if (list.Count == 0)
                _notes.Remove(steamId);
            Save();
            return null;
        }
    }

    private void Save()
    {
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_notes, Json));
        File.Move(_path + ".tmp", _path, overwrite: true);
    }
}
