using System.Collections.Concurrent;

namespace GmmPanel.Data;

// Every 6 hours (and a minute after start): applies the Panel page's Retention to the audit log, answered tickets and the
// panel's own log files. History prunes chat, counts and players itself as it saves.
public sealed class DataCleanup : BackgroundService
{
    private readonly PanelStore _store;
    private readonly AuditLog _audit;
    private readonly Tickets _tickets;
    private readonly ILogger<DataCleanup> _logger;

    public DataCleanup(PanelStore store, AuditLog audit, Tickets tickets, ILogger<DataCleanup> logger)
    {
        _store = store;
        _audit = audit;
        _tickets = tickets;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stop);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            do
                Run();
            while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Run()
    {
        Retention keep = _store.Current.Retention;
        try
        {
            if (keep.AuditDays > 0 && _audit.Prune(DateTimeOffset.UtcNow.AddDays(-keep.AuditDays)) is int dropped and > 0)
                _logger.LogInformation("Cleanup: {Count} audit entries older than {Days} days removed", dropped, keep.AuditDays);
            _tickets.Prune();
            FileLog.Prune(keep.LogDays);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Cleanup couldn't finish: {Error}", error.Message);
        }
    }
}

// The panel's own log: one file a day in data/logs (panel-2026-11-13.log), as well as the console. Files older than
// Retention.LogDays go (DataCleanup). Information and up; a line per entry; exceptions with their stack.
public sealed class FileLog : ILoggerProvider
{
    private static string? _folder;
    private readonly BlockingCollection<string> _lines = new(10000);
    private readonly Thread _writer;

    public FileLog(string dataDirectory)
    {
        _folder = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(_folder);
        _writer = new Thread(Write) { IsBackground = true, Name = "panel file log" };
        _writer.Start();
    }

    public ILogger CreateLogger(string category) => new Logger(this, category);

    public void Dispose()
    {
        _lines.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    // Deletes the panel's log files older than this many days (0 keeps them)
    public static void Prune(int days)
    {
        if (days <= 0 || _folder == null || !Directory.Exists(_folder))
            return;
        DateTime cut = DateTime.Now.Date.AddDays(-days);
        foreach (string file in Directory.GetFiles(_folder, "panel-*.log"))
            if (DateTime.TryParse(Path.GetFileNameWithoutExtension(file)["panel-".Length..], out DateTime day) && day < cut)
                File.Delete(file);
    }

    private void Add(string line)
    {
        _lines.TryAdd(line);   // When the disk can't keep up, lines are dropped rather than slowing the panel
    }

    private void Write()
    {
        foreach (string line in _lines.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(Path.Combine(_folder!, $"panel-{DateTime.Now:yyyy-MM-dd}.log"), line);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class Logger(FileLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information && !category.StartsWith("System.Net.Http", StringComparison.Ordinal);

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (!IsEnabled(level))
                return;
            string text = $"{DateTime.Now:HH:mm:ss} {level.ToString()[..4].ToLowerInvariant()} {category}: {format(state, error)}";
            if (error != null)
                text += Environment.NewLine + error;
            log.Add(text + Environment.NewLine);
        }
    }
}
