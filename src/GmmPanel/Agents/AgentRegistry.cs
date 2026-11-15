using System.Collections.Concurrent;
using GmmPanel.Protocol;

namespace GmmPanel.Agents;

// What the panel knows about one server. Kept after a disconnect, so a page can say "offline since ...".
public sealed class ServerView
{
    private readonly object _lock = new();
    private readonly LinkedList<EventMessage> _events = new();

    public ServerView(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public AgentConnection? Connection { get; private set; }
    public StatusSnapshot? Status { get; private set; }
    public DateTimeOffset? StatusAt { get; private set; }
    public DateTimeOffset? OfflineSince { get; private set; }

    // Goes up whenever anything a page shows changes, so a page polling with its last revision gets "no change" cheaply
    public long Revision { get; private set; }

    public bool Online => Connection != null;

    // Since when this link is up (the public pages' "online for")
    public DateTimeOffset? OnlineSince { get; private set; }

    // Where players connect: what the server reports (Panel.PublicAddress, net_public_adr, a public ip), else the address its
    // agent connected from with its game port. A hosted server usually connects out from its own address.
    public string? ConnectAddress
    {
        get
        {
            if (Status?.Server?.Address is { Length: > 0 } reported)
                return reported;
            System.Net.IPAddress? ip = Connection?.RemoteAddress;
            int port = Status?.Server?.Port ?? 0;
            if (ip == null || port <= 0)
                return null;
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            byte[] b = ip.GetAddressBytes();
            bool isPrivate = ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || System.Net.IPAddress.IsLoopback(ip) || b[0] is 0 or 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            return isPrivate ? null : $"{ip}:{port}";
        }
    }

    public List<EventMessage> Events
    {
        get { lock (_lock) return _events.ToList(); }
    }

    public void SetConnection(AgentConnection? connection)
    {
        lock (_lock)
        {
            Connection = connection;
            OfflineSince = connection == null ? DateTimeOffset.UtcNow : null;
            OnlineSince = connection == null ? null : DateTimeOffset.UtcNow;
            Revision++;
        }
    }

    // The agent resends its status every 30 s; only a real change moves the revision, so pages aren't redrawn under the mouse
    public void SetStatus(StatusSnapshot status, string json)
    {
        lock (_lock)
        {
            StatusAt = DateTimeOffset.UtcNow;
            if (json == _statusJson)
                return;
            _statusJson = json;
            // A map change GameModeManager didn't make (changelevel from a console, the web console included) sends no
            // event, so the new map in the status is noted here, unless GMM's own "map" event came just before
            string before = Status?.Map.Name ?? "", after = status.Map.Name ?? "";
            if (before.Length > 0 && after.Length > 0 && !string.Equals(before, after, StringComparison.OrdinalIgnoreCase)
                && !_events.Any(e => e.Name == "map" && e.At > StatusAt.Value.AddMinutes(-3)))
            {
                _events.AddFirst(new EventMessage { Name = "map", Text = $"Map changed to {after}", At = StatusAt.Value });
                while (_events.Count > 30)
                    _events.RemoveLast();
            }
            Status = status;
            ChangedAt = StatusAt;
            Revision++;
        }
    }

    private string? _statusJson;

    public DateTimeOffset? ChangedAt { get; private set; }

    public void AddEvent(EventMessage message)
    {
        lock (_lock)
        {
            _events.AddFirst(message);
            while (_events.Count > 30)
                _events.RemoveLast();
            Revision++;
        }
    }
}

public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<string, ServerView> _servers = new(StringComparer.OrdinalIgnoreCase);

    public ServerView Get(string name) => _servers.GetOrAdd(name, key => new ServerView(key));

    public ServerView? Find(string name) => _servers.TryGetValue(name, out var view) ? view : null;

    public IEnumerable<ServerView> All() => _servers.Values;

    // A server takes its place only when no live link holds it
    public bool TryClaim(string name, AgentConnection connection)
    {
        ServerView view = Get(name);
        lock (view)
        {
            if (view.Connection != null)
                return false;
            view.SetConnection(connection);
            return true;
        }
    }

    public void Release(string name, AgentConnection connection)
    {
        ServerView view = Get(name);
        lock (view)
        {
            if (view.Connection == connection)
                view.SetConnection(null);
        }
    }
}
