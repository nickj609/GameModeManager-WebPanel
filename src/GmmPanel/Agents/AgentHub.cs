using System.Net.WebSockets;
using System.Text.Json;
using GmmPanel.Data;
using GmmPanel.Protocol;

namespace GmmPanel.Agents;

// /agent: where GameModeManager's agent connects. Cloudflare Access has already checked the service token on the
// agent hostname; this checks the server's own token, then reads messages until the link closes.
public sealed class AgentHub
{
    private readonly PanelStore _store;
    private readonly AgentRegistry _registry;
    private readonly AuditLog _audit;
    private readonly ILogger<AgentHub> _logger;
    private readonly History _history;
    private readonly Discord _discord;

    public AgentHub(PanelStore store, AgentRegistry registry, AuditLog audit, History history, Discord discord, ILogger<AgentHub> logger)
    {
        _history = history;
        _discord = discord;
        _store = store;
        _registry = registry;
        _audit = audit;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string header = context.Request.Headers.Authorization.ToString();
        string token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : "";
        ServerEntry? server = token.Length == 0 ? null : _store.FindServerByToken(token);
        string from = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (server == null)
        {
            _audit.Write($"agent from {from}", "connect", null, false, "unknown or missing server token");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (_registry.Find(server.Name)?.Online == true)
        {
            // Two links for one server: an old agent loop is still running (a hot reload), or the token is used twice
            _audit.Write($"agent from {from}", "connect", server.Name, false, "this server is already connected");
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            KeepAliveTimeout = TimeSpan.FromSeconds(75)
        });

        CancellationToken stopping = context.RequestAborted;
        HelloMessage? hello = await ReadHelloAsync(socket, stopping);
        if (hello == null)
        {
            await SendErrorAsync(socket, "Expected hello first.", stopping);
            return;
        }
        if (hello.Protocol != AgentProtocol.Version)
        {
            string text = hello.Protocol > AgentProtocol.Version
                ? $"GameModeManager speaks protocol {hello.Protocol}; this panel speaks {AgentProtocol.Version}. Update the panel."
                : $"GameModeManager speaks protocol {hello.Protocol}; this panel speaks {AgentProtocol.Version}. Update GameModeManager.";
            _audit.Write($"agent {server.Name}", "connect", server.Name, false, text);
            await SendErrorAsync(socket, text, stopping);
            return;
        }

        var connection = new AgentConnection(server.Name, socket, hello) { RemoteAddress = context.Connection.RemoteIpAddress };
        if (!_registry.TryClaim(server.Name, connection))
        {
            await SendErrorAsync(socket, "This server is already connected.", stopping);
            return;
        }

        _logger.LogInformation("Agent {Server} connected from {From}: GameModeManager {Gmm}, CounterStrikeSharp {Css}",
            server.Name, from, hello.GmmVersion, hello.CssVersion);
        ServerView view = _registry.Get(server.Name);
        try
        {
            // Back after a short drop (a map change, a restart) says nothing; back after an "offline" post says so
            if (_reportedOffline.TryRemove(server.Name, out _))
                _discord.Server($"**{server.Name}** is back online.", true);
            await connection.SendAsync(new WelcomeMessage { Server = server.Name }, stopping);
            await ReadLoopAsync(socket, connection, view, server.Name, stopping);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or InvalidDataException or JsonException)
        {
            _logger.LogInformation("Agent {Server} link ended: {Reason}", server.Name, exception.Message);
        }
        finally
        {
            connection.FailAll();
            _registry.Release(server.Name, connection);
            _history.OnOffline(server.Name);
            _ = ReportIfStillOfflineAsync(server.Name);
            await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
            _logger.LogInformation("Agent {Server} disconnected", server.Name);
        }
    }

    private static async Task<HelloMessage?> ReadHelloAsync(WebSocket socket, CancellationToken stopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            string? text = await AgentConnection.ReceiveTextAsync(socket, timeout.Token);
            if (text == null)
                return null;
            using JsonDocument document = JsonDocument.Parse(text);
            if (TypeOf(document) != "hello")
                return null;
            return document.Deserialize<HelloMessage>(AgentProtocol.Json);
        }
        catch (Exception exception) when (exception is OperationCanceledException or JsonException or InvalidDataException or WebSocketException)
        {
            return null;
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _reportedOffline = new(StringComparer.OrdinalIgnoreCase);

    // Discord hears about a server only when it stays away 2 minutes: restarts and map changes drop the link briefly
    private async Task ReportIfStillOfflineAsync(string server)
    {
        await Task.Delay(TimeSpan.FromMinutes(2));
        if (_registry.Find(server)?.Online != true && _reportedOffline.TryAdd(server, true))
            _discord.Server($"**{server}** has been offline for 2 minutes.", false);
    }

    private async Task ReadLoopAsync(WebSocket socket, AgentConnection connection, ServerView view, string server, CancellationToken stopping)
    {
        while (true)
        {
            string? text = await AgentConnection.ReceiveTextAsync(socket, stopping);
            if (text == null)
                return;

            using JsonDocument document = JsonDocument.Parse(text);
            switch (TypeOf(document))
            {
                case "status":
                    StatusMessage? status = document.Deserialize<StatusMessage>(AgentProtocol.Json);
                    if (status != null)
                    {
                        view.SetStatus(status.Status, document.RootElement.GetProperty("status").GetRawText());
                        _history.OnStatus(server, status.Status);
                    }
                    break;
                case "event":
                    EventMessage? message = document.Deserialize<EventMessage>(AgentProtocol.Json);
                    if (message != null)
                        view.AddEvent(message);
                    break;
                case "chat":
                    ChatMessage? chat = document.Deserialize<ChatMessage>(AgentProtocol.Json);
                    if (chat != null)
                        _history.OnChat(server, chat);
                    break;
                case "result":
                    ResultMessage? result = document.Deserialize<ResultMessage>(AgentProtocol.Json);
                    if (result != null)
                        connection.Complete(result);
                    break;
                // Anything else is from a newer agent; ignored, not fatal
            }
        }
    }

    private static string? TypeOf(JsonDocument document) =>
        document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("type", out JsonElement type)
            ? type.GetString()
            : null;

    private static async Task SendErrorAsync(WebSocket socket, string text, CancellationToken stopping)
    {
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ErrorMessage { Message = text }, AgentProtocol.Json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, stopping);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "refused", stopping);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
        }
    }
}
