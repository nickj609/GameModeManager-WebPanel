using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GmmPanel.Protocol;

namespace GmmPanel.Agents;

public sealed record CommandResult(bool Ok, string Message, FollowUp? FollowUp = null, string? Output = null, DataPage? Data = null);

// One game server's live link. Reading happens in AgentHub's loop; sending is locked so frames never interleave.
public sealed class AgentConnection
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> _pending = new();

    public AgentConnection(string server, WebSocket socket, HelloMessage hello)
    {
        Server = server;
        _socket = socket;
        Hello = hello;
    }

    public string Server { get; }

    // Where the agent connected from (cloudflared passes the real address): the Connect fallback when the server reports none
    public System.Net.IPAddress? RemoteAddress { get; set; }
    public HelloMessage Hello { get; }

    public async Task SendAsync<T>(T message, CancellationToken cancel)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, AgentProtocol.Json);
        await _sendLock.WaitAsync(cancel);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancel);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // Send a command and wait for GameModeManager's answer. It runs on the next game frame, so this is quick unless
    // the server is hung or the link dropped.
    public async Task<CommandResult> RunAsync(string name, Dictionary<string, string> args, string adminSteamId, string adminName)
    {
        string id = Guid.NewGuid().ToString("N");
        var waiting = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiting;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await SendAsync(new CommandMessage { Id = id, Name = name, Args = args, AdminSteamId = adminSteamId, AdminName = adminName }, timeout.Token);
            Task finished = await Task.WhenAny(waiting.Task, Task.Delay(Timeout.Infinite, timeout.Token));
            if (finished != waiting.Task)
                return new CommandResult(false, "The server didn't answer within 15 seconds. It may still have run the command.");
            return await waiting.Task;
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(false, "The server didn't answer within 15 seconds. It may still have run the command.");
        }
        catch (WebSocketException)
        {
            return new CommandResult(false, "The link to the server dropped before the command was sent.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public void Complete(ResultMessage result)
    {
        if (_pending.TryRemove(result.Id, out var waiting))
            waiting.TrySetResult(new CommandResult(result.Ok, result.Message, result.FollowUp, result.Output, result.Data));
    }

    public void FailAll()
    {
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var waiting))
                waiting.TrySetResult(new CommandResult(false, "The link to the server dropped. Check the status before trying again."));
    }

    // Ends a link at once, without a close handshake: a stale one, replaced by the server's new link, whose other end is gone
    public void Abort() => _socket.Abort();

    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(status, reason, timeout.Token);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
        }
    }

    public static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancel)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, cancel);
            if (received.MessageType == WebSocketMessageType.Close)
                return null;
            if (received.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Only text messages are allowed.");
            message.Write(buffer, 0, received.Count);
            if (message.Length > AgentProtocol.MaxMessageBytes)
                throw new InvalidDataException("Message too large.");
            if (received.EndOfMessage)
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }
}
