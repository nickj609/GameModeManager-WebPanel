namespace GmmPanel.Agents;

// Which GameModeManager the panel is built for. A server running an older one still connects, but the panel's newer pages
// (Who's on, plugin data, rotation saving ...) don't work there: the Panel page says so, with what to update.
public static class AgentVersion
{
    public static readonly Version Expected = new(2, 4, 0);

    // "2.4.0-test.7b7a6a3" -> 2.4.0; null when it can't be read
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string core = text.Split('-', '+')[0].Trim();
        return Version.TryParse(core, out Version? v) ? v : null;
    }

    // Null when it's fine; else what's wrong, for the Panel page
    public static string? Problem(ServerView? view)
    {
        if (view?.Connection?.Hello is not { } hello)
            return null;
        Version? have = Parse(hello.GmmVersion);
        if (have == null)
            return $"Its GameModeManager version ({hello.GmmVersion}) can't be read.";
        if (have < Expected)
            return $"GameModeManager {have} is older than the panel expects ({Expected}): some pages won't work there. Update it on that server.";
        if (view.Status is { } s && (s.Roster == null || s.Data == null || s.Server == null))
            return "Its GameModeManager doesn't send everything the panel shows (who's on, plugin data). Update it on that server.";
        return null;
    }
}
