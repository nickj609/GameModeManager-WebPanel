namespace GmmPanel.Data;

// What goes in a game server's GameModeManager.json for the panel: the setup page and Site settings, Servers show it. The
// token isn't part of it (it's in panel-secrets.json and doesn't depend on the address), so changing the address only
// means pasting this block again.
public static class GmmSettings
{
    // The address game servers connect to: the one saved in Site settings, else this panel's own address
    public static string AgentUrl(PanelData data, HttpRequest request)
    {
        if (data.AgentUrl.Length > 0)
            return data.AgentUrl;
        string host = request.Host.Host;
        return host is "localhost" or "127.0.0.1" ? $"ws://127.0.0.1:{request.Host.Port ?? 5080}/agent" : $"wss://{host}/agent";
    }

    public static string? AgentUrlProblem(string url) =>
        !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("wss" or "ws") || url.Contains('"') || url.Contains('\\')
            ? "The agent address starts with wss:// and ends with /agent, like wss://panel.example.com/agent."
            : !parsed.AbsolutePath.TrimEnd('/').EndsWith("/agent", StringComparison.OrdinalIgnoreCase)
                ? "The agent address ends with /agent, like wss://panel.example.com/agent."
                : null;

    // GameModeManager.json's Panel block for one server
    public static string PanelBlock(string url, string server) =>
        "\"Panel\": {\n  \"Enabled\": true,\n  \"Url\": \"" + url + "\",\n  \"ServerName\": \"" + server + "\",\n  \"SecretsFile\": \"panel-secrets.json\",\n"
        + "  \"Console\": true,\n  \"PublicAddress\": \"\",\n  \"PluginData\": true\n}";
}
