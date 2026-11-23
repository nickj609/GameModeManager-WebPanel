using System.Security.Cryptography;
using System.Text.Json;

namespace GmmPanel.Data;

// Ban appeals and player reports: the panel keeps them itself (data/tickets.json), as no supported plugin stores them.
// A signed-in player writes one on My profile; staff answer it under Moderate. Accepting an appeal unbans through
// CS2-SimpleAdmin (Moderate does that through the server, so it's checked and logged like any unban).
public sealed class Tickets
{
    public const int MaxText = 1000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly PanelStore _store;
    private readonly object _lock = new();
    private List<Ticket> _tickets;

    public Tickets(PanelStore store)
    {
        _store = store;
        _path = Path.Combine(store.DataDirectory, "tickets.json");
        _tickets = File.Exists(_path) ? JsonSerializer.Deserialize<List<Ticket>>(File.ReadAllText(_path), Json) ?? new() : new();
    }

    public sealed class Ticket
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";              // appeal or report
        public string Server { get; set; } = "";            // Reports: where it happened
        public string SteamId { get; set; } = "";           // Who wrote it
        public string Name { get; set; } = "";
        public string TargetSteamId { get; set; } = "";     // Appeals: the writer; reports: who's reported
        public string TargetName { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTimeOffset Created { get; set; }
        public string Status { get; set; } = "OPEN";        // OPEN, ACCEPTED, REJECTED, CLOSED
        public string HandledBy { get; set; } = "";
        public DateTimeOffset? HandledAt { get; set; }
        public List<Comment> Comments { get; set; } = new();   // Staff's comments; Public ones the writer sees on their profile
        public string Reason { get; set; } = "";            // Reports: cheating, griefing, abuse or other ("" before reasons existed)
        public bool? TargetWasOn { get; set; }              // Reports: whether they were on that server when it was sent (null: not known)
        public string ClaimedBy { get; set; } = "";         // "Looking at it": who, so two staff don't answer the same one
        public string ClaimedBySteamId { get; set; } = "";
        public DateTimeOffset? ClaimedAt { get; set; }

        public Ticket Clone()
        {
            var copy = (Ticket)MemberwiseClone();
            copy.Comments = Comments.ToList();
            return copy;
        }
    }

    public sealed record Comment(string Name, string SteamId, DateTimeOffset At, string Text, bool Public);

    public const int MaxComment = 500;

    // A staff comment on an appeal or report, open or answered (50 at most)
    public bool AddComment(string id, Comment comment)
    {
        lock (_lock)
        {
            Ticket? ticket = _tickets.FirstOrDefault(t => t.Id == id);
            if (ticket == null || ticket.Comments.Count >= 50)
                return false;
            ticket.Comments.Add(comment);
            Save();
            return true;
        }
    }

    public static readonly string[] Reasons = { "cheating", "griefing", "abuse", "other" };

    // The words for a report's reason (translated where shown)
    public static string ReasonLabel(string reason) => reason switch
    {
        "cheating" => "Cheating",
        "griefing" => "Griefing",
        "abuse" => "Abuse or spam",
        "other" => "Other",
        _ => ""
    };

    // "Looking at it": claim it, or let it go (only whoever claimed it, unless it's over 2 hours old)
    public bool Claim(string id, string name, string steamId, bool claim)
    {
        lock (_lock)
        {
            Ticket? ticket = _tickets.FirstOrDefault(t => t.Id == id);
            if (ticket == null || ticket.Status != "OPEN")
                return false;
            bool stale = ticket.ClaimedAt is { } at && at < DateTimeOffset.UtcNow.AddHours(-2);
            if (ticket.ClaimedBySteamId.Length > 0 && ticket.ClaimedBySteamId != steamId && !stale)
                return false;
            ticket.ClaimedBy = claim ? name : "";
            ticket.ClaimedBySteamId = claim ? steamId : "";
            ticket.ClaimedAt = claim ? DateTimeOffset.UtcNow : null;
            Save();
            return true;
        }
    }

    // A player's other reports (newest first), for the report page
    public List<Ticket> ReportsAbout(string targetSteamId, string exceptId)
    {
        lock (_lock)
            return _tickets.Where(t => t.Kind == "report" && t.TargetSteamId == targetSteamId && t.Id != exceptId)
                .OrderByDescending(t => t.Created).Select(Copy).ToList();
    }

    public List<Ticket> List(string kind, string filter, string search)
    {
        lock (_lock)
            return _tickets.Where(t => t.Kind == kind && (filter == "all" || t.Status == "OPEN"))
                .Where(t => search.Length == 0 || t.SteamId == search || t.TargetSteamId == search
                    || t.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || t.TargetName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.Created).Select(Copy).ToList();
    }

    public List<Ticket> By(string steamId)
    {
        lock (_lock)
            return _tickets.Where(t => t.SteamId == steamId || t.TargetSteamId == steamId).OrderByDescending(t => t.Created).Select(Copy).ToList();
    }

    public Ticket? Find(string id)
    {
        lock (_lock)
            return _tickets.FirstOrDefault(t => t.Id == id) is { } t ? Copy(t) : null;
    }

    public int Open(string kind)
    {
        lock (_lock)
            return _tickets.Count(t => t.Kind == kind && t.Status == "OPEN");
    }

    // Null when added; otherwise why not
    public string? Add(Ticket ticket)
    {
        lock (_lock)
        {
            if (ticket.Kind == "appeal" && _tickets.Any(t => t.Kind == "appeal" && t.SteamId == ticket.SteamId && t.Status == "OPEN"))
                return "You already have an open appeal. Staff will answer it here.";
            if (_tickets.Count(t => t.SteamId == ticket.SteamId && t.Created > DateTimeOffset.UtcNow.AddDays(-1)) >= 5)
                return "That's 5 in a day. Try again tomorrow.";
            ticket.Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
            ticket.Created = DateTimeOffset.UtcNow;
            ticket.Status = "OPEN";
            _tickets.Add(ticket);
            Save();
            return null;
        }
    }

    public bool SetStatus(string id, string status, string by)
    {
        lock (_lock)
        {
            Ticket? ticket = _tickets.FirstOrDefault(t => t.Id == id);
            if (ticket == null || ticket.Status != "OPEN")
                return false;
            ticket.Status = status;
            ticket.HandledBy = by;
            ticket.HandledAt = DateTimeOffset.UtcNow;
            Save();
            return true;
        }
    }

    // Answering one report can close the player's other open ones with it (the same answer, noted as such)
    public int CloseOthers(string targetSteamId, string exceptId, string by, string note)
    {
        lock (_lock)
        {
            var others = _tickets.Where(t => t.Kind == "report" && t.TargetSteamId == targetSteamId && t.Id != exceptId && t.Status == "OPEN").ToList();
            foreach (Ticket t in others)
            {
                t.Status = "CLOSED";
                t.HandledBy = by;
                t.HandledAt = DateTimeOffset.UtcNow;
                t.Comments.Add(new Comment(by, "", DateTimeOffset.UtcNow, note, false));
            }
            if (others.Count > 0)
                Save();
            return others.Count;
        }
    }

    // The cleanup's call: saving applies the retention
    public void Prune()
    {
        lock (_lock)
            Save();
    }

    private void Save()
    {
        // Answered tickets older than Retention.TicketDays go (0 keeps them)
        int days = _store.Current.Retention.TicketDays;
        if (days > 0)
            _tickets.RemoveAll(t => t.Status != "OPEN" && (t.HandledAt ?? t.Created) < DateTimeOffset.UtcNow.AddDays(-days));
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_tickets, Json));
        File.Move(_path + ".tmp", _path, overwrite: true);
    }

    private static Ticket Copy(Ticket t) => t.Clone();
}
