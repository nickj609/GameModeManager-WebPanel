namespace GmmPanel.Data;

// A server's name for people: its display name (Site settings, Servers), else its ID. Pages show Label(id); links, tokens,
// history and the audit log keep the ID.
public static class ServerNames
{
    public const int MaxLabel = 24;   // Like the in-game menu rows, so tabs stay tidy
    internal static PanelStore? Store;

    public static string Label(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return id ?? "";
        ServerEntry? entry = Store?.Current.Servers.FirstOrDefault(s => s.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
        return entry is { DisplayName.Length: > 0 } ? entry.DisplayName : id;
    }

    // A new server's ID from its display name: "Deathmatch #2" -> "Deathmatch-2", with a number added when it's taken
    public static string NewId(string label, IEnumerable<string> taken)
    {
        var chars = label.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-').ToArray();
        string id = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (id.Length == 0)
            id = "Server";
        id = id.Length > 32 ? id[..32].TrimEnd('-') : id;
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        string candidate = id;
        for (int n = 2; used.Contains(candidate); n++)
            candidate = $"{id}-{n}";
        return candidate;
    }
}
