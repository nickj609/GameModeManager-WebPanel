using System.Text;
using System.Text.Json;
using GmmPanel.Data;
using Microsoft.AspNetCore.Mvc;

namespace GmmPanel.Pages.Audit;

// The audit log (panel admins): searchable by who, what and detail, a server, the result and how far back, newest first,
// 100 a page; and the whole log or a recent part of it as a JSON or CSV file
public class IndexModel : PanelPage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public const int PageSize = 100;
    private readonly AuditLog _audit;
    private readonly PanelStore _store;

    public IndexModel(AuditLog audit, PanelStore store)
    {
        _audit = audit;
        _store = store;
    }

    [BindProperty(SupportsGet = true)] public string Q { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Server { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Result { get; set; } = "";   // "", ok, refused
    [BindProperty(SupportsGet = true)] public int Days { get; set; } = 7;          // 1, 7, 30, 90, 0 = everything
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public List<AuditEntry> Entries { get; private set; } = new();
    public int Total { get; private set; }
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public List<string> ServerNames => _store.Current.Servers.Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public void OnGet()
    {
        List<AuditEntry> all = Filtered();
        all.Reverse();
        Total = all.Count;
        P = Math.Clamp(P, 1, Pages);
        Entries = all.Skip((P - 1) * PageSize).Take(PageSize).ToList();
    }

    // The entries the search's filters keep, oldest first
    private List<AuditEntry> Filtered()
    {
        // An empty box in the search form binds as null, not ""
        Q ??= "";
        Server ??= "";
        Result ??= "";
        DateTimeOffset since = Days is 1 or 7 or 30 or 90 ? DateTimeOffset.UtcNow.AddDays(-Days) : DateTimeOffset.MinValue;
        string q = Q.Trim();
        return _audit.Since(since)
            .Where(e => q.Length == 0 || (e.Who ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) || (e.What ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) || AuditText.Text(e.What ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) || (e.Detail ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
            .Where(e => Server.Length == 0 || string.Equals(e.Server, Server, StringComparison.OrdinalIgnoreCase))
            .Where(e => Result switch { "ok" => e.Ok, "refused" => !e.Ok, _ => true })
            .ToList();
    }

    public string PageUrl(int page) => $"/audit?q={Uri.EscapeDataString(Q)}&server={Uri.EscapeDataString(Server)}&result={Result}&days={Days}&p={page}";

    // What the search shows (q, server, result, days), every page of it; format: json or csv
    public IActionResult OnGetExport(string? format)
    {
        List<AuditEntry> entries = Filtered();
        string filters = string.Join(", ", new[] { Q.Trim().Length > 0 ? $"\"{Q.Trim()}\"" : null, Server.Length > 0 ? Server : null, Result.Length > 0 ? Result : null, Days > 0 ? $"{Days} days" : "everything" }.Where(f => f != null));
        _audit.Write(Admin.Audit, $"audit export {(format == "csv" ? "csv" : "json")} ({filters}, {entries.Count} entries)", null, true, "");
        string stamp = DateTime.Now.ToString("yyyy-MM-dd");
        // The page's own columns: Time (UTC), User, Action (as the page words it), Server, Result, Details
        var rows = entries.Select(e => new
        {
            time = e.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            user = e.Who ?? "",
            action = AuditText.Text(e.What ?? ""),
            server = e.Server ?? "",
            result = e.Ok ? "Success" : "Failure",
            details = e.Detail ?? ""
        }).ToList();
        if (format == "csv")
        {
            var csv = new StringBuilder("Time (UTC),User,Action,Server,Result,Details\r\n");
            foreach (var r in rows)
                csv.Append(string.Join(",", Cell(r.time), Cell(r.user), Cell(r.action), Cell(r.server), r.result, Cell(r.details))).Append("\r\n");
            // A byte-order mark, so Excel reads it as UTF-8
            return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8", $"audit-{stamp}.csv");
        }
        return File(JsonSerializer.SerializeToUtf8Bytes(rows, Json), "application/json", $"audit-{stamp}.json");
    }

    // A CSV cell: quoted, quotes doubled, and a leading = + - @ made harmless so a spreadsheet doesn't run it as a formula
    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
