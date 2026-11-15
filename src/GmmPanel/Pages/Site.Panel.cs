using GmmPanel.Agents;
using GmmPanel.Auth;
using GmmPanel.Commands;
using GmmPanel.Data;
using Microsoft.AspNetCore.Mvc;

namespace GmmPanel.Pages;

// Site settings, the running of the panel (panel admins only): who the panel admins are, the flags each part of the panel
// needs, which game servers may connect, Discord, how long records are kept, and backups. What the gmmpanel CLI does,
// from the browser. Everyone else's rights come from the servers' admin flags, so they aren't listed here.
public partial class SiteModel
{
    private readonly AccessService _access;
    private readonly Discord _discord;
    private readonly DataCleanup _cleanup;
    private readonly Backup _backup;

    public PanelData Data => _store.Current;
    public string? NewToken { get; private set; }
    public string? NewTokenServer { get; private set; }

    public ServerView? Find(string server) => _registry.Find(server);

    public Discord.DiscordSettings DiscordSettings => _discord.Settings;

    // Permissions: one select per area; the default is stored as "no override"
    public IActionResult OnPostPermissions()
    {
        PanelData data = _store.LoadForEdit();
        var changed = new List<string>();
        foreach (var area in PanelPermissions.Areas)
        {
            string value = Request.Form[$"flag-{area.Id}"].ToString().Trim();
            if (value.Length == 0)
                continue;
            if (!PanelPermissions.IsFlag(value))
                return Done($"{area.Label}: that isn't an admin flag (like @css/kick).", failed: true);
            string old = PanelPermissions.Need(area.Id);
            if (value == area.Default)
                data.Permissions.Remove(area.Id);
            else
                data.Permissions[area.Id] = value;
            if (old != value)
                changed.Add($"{area.Id} {old} -> {value}");
        }
        _store.Save(data);
        if (changed.Count > 0)
            _audit.Write(Admin.Audit, "permissions", null, true, string.Join(", ", changed));
        return Done(changed.Count == 0 ? "Nothing changed." : "Permissions saved. They count on everyone's next click.");
    }

    // Backup: settings and records as a zip (history, pictures and the Discord webhook only when ticked). A temp file, so a
    // big one isn't held in memory; it goes when the download ends.
    public IActionResult OnGetBackup(bool history, bool media, bool secrets)
    {
        string temp = Path.GetTempFileName();
        using (FileStream write = System.IO.File.Create(temp))
            _backup.Write(write, history, media, secrets);
        _audit.Write(Admin.Audit, $"backup downloaded (history {history}, pictures {media}, webhook {secrets})", null, true, "");
        var read = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose);
        return File(read, "application/zip", $"gmmpanel-backup-{DateTime.Now:yyyy-MM-dd}.zip");
    }

    public async Task<IActionResult> OnPostRestoreAsync(IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return Done("Choose a backup zip first.", failed: true);
        string temp = Path.GetTempFileName();
        try
        {
            await using (FileStream write = System.IO.File.Create(temp))
                await file.CopyToAsync(write);
            await using FileStream read = System.IO.File.OpenRead(temp);
            string? problem = _backup.Restore(read, out int restored);
            if (problem != null)
                return Done(problem, failed: true);
            _audit.Write(Admin.Audit, $"backup restored ({restored} files)", null, true, "");
            return Done($"Restored {restored} files. Settings, servers and admins apply now; restart the panel for reports, notes, tasks and the Discord settings. What was there before is in data\before-restore-*.zip.");
        }
        finally
        {
            System.IO.File.Delete(temp);
        }
    }

    // Retention: how many days each record is kept (0 = all of it); "clean" also runs the cleanup now
    public Retention Retention => _store.Current.Retention;

    // What each kind of record takes on disk now, for the Retention section
    public long SizeOf(string relative)
    {
        string path = Path.Combine(_store.DataDirectory, relative);
        if (System.IO.File.Exists(path))
            return new FileInfo(path).Length;
        return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
    }

    public IActionResult OnPostRetention(int audit, int chat, int counts, int players, int tickets, int logs, string? clean)
    {
        PanelData data = _store.LoadForEdit();
        Retention keep = data.Retention;
        keep.AuditDays = Retention.Clamp(audit, keep.AuditDays);
        keep.ChatDays = Retention.Clamp(chat, keep.ChatDays);
        keep.CountDays = Retention.Clamp(counts, keep.CountDays);
        keep.PlayerDays = Retention.Clamp(players, keep.PlayerDays);
        keep.TicketDays = Retention.Clamp(tickets, keep.TicketDays);
        keep.LogDays = Retention.Clamp(logs, keep.LogDays);
        _store.Save(data);
        _audit.Write(Admin.Audit, "retention", null, true, $"audit {keep.AuditDays}, chat {keep.ChatDays}, counts {keep.CountDays}, players {keep.PlayerDays}, tickets {keep.TicketDays}, logs {keep.LogDays} (days, 0 = all)");
        if (clean == "1")
            _cleanup.Run();
        return Done(clean == "1" ? "Saved, and cleaned up: older records are gone." : "Saved. The cleanup applies it within 6 hours (chat, counts and players within minutes).");
    }

    // Discord: the webhook is kept only if a new one is typed; empty keeps the old, "off" removes it
    public IActionResult OnPostDiscord(string? url, bool servers, bool tickets, bool actions, string? remove, string? test)
    {
        var settings = _discord.Settings;
        url = (url ?? "").Trim();
        if (remove != null)
            settings.Url = "";
        else if (url.Length > 0)
        {
            if (!Discord.IsWebhook(url))
                return Done("That isn't a Discord webhook address (https://discord.com/api/webhooks/...).", failed: true);
            settings.Url = url;
        }
        settings.Servers = servers;
        settings.Tickets = tickets;
        settings.Actions = actions;
        _discord.Save(settings);
        _audit.Write(Admin.Audit, "discord settings", null, true, settings.Url.Length == 0 ? "off" : $"servers={servers} tickets={tickets} actions={actions}");
        if (test != null && settings.Url.Length > 0)
            _discord.Test();
        return Done(remove != null ? "Discord posts are off." : test != null ? "Saved, and a test post is on its way." : "Discord settings saved.");
    }


    public IActionResult OnPostAddAdmin(string? steamId, string? name, string? servers)
    {
        steamId = (steamId ?? "").Trim();
        name = (name ?? "").Trim();
        if (!CommandCatalog.IsSteamId(steamId))
            return Done("Enter a SteamID64 (17 digits, starting 7656119).", failed: true);
        if (CommandCatalog.TextProblem(name, 40, "The name", allowEmpty: false) is string bad)
            return Done(bad, failed: true);
        List<string> limited = (servers ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (limited.Any(s => !Data.Servers.Any(e => e.Name.Equals(s, StringComparison.OrdinalIgnoreCase))))
            return Done("Only servers on this page can be listed.", failed: true);

        PanelData data = _store.LoadForEdit();
        data.Admins.RemoveAll(a => a.SteamId == steamId);
        data.Admins.Add(new AdminEntry { SteamId = steamId, Name = name, Role = Roles.Admin, Servers = limited });
        _store.Save(data);
        _access.Forget(steamId);
        _audit.Write(Admin.Audit, $"panel admin add {name} ({steamId})", null, true, limited.Count == 0 ? "every server" : string.Join(", ", limited));
        return Done($"{name} is a panel admin.");
    }

    public IActionResult OnPostRemoveAdmin(string? steamId)
    {
        PanelData data = _store.LoadForEdit();
        if (steamId == Admin.SteamId)
            return Done("You can't remove yourself. Another panel admin can.", failed: true);
        AdminEntry? entry = data.Admins.FirstOrDefault(a => a.SteamId == steamId);
        if (entry == null)
            return Done("That panel admin isn't on the list.", failed: true);
        data.Admins.Remove(entry);
        _store.Save(data);
        _access.Forget(entry.SteamId);
        _audit.Write(Admin.Audit, $"panel admin remove {entry.Name} ({entry.SteamId})", null, true, "");
        return Done($"{entry.Name} is no longer a panel admin. Their servers' admin flags still count.");
    }

    // A new server (add), or a new token for one already on the list (the old token stops working at its next connect)
    public IActionResult OnPostToken(string? server, bool add)
    {
        server = (server ?? "").Trim();
        if (!PanelStore.IsServerName(server))
            return Done("Server names use letters, digits, - and _ (up to 40), like Production or Deathmatch-2.", failed: true);
        bool existed = Data.Servers.Any(s => s.Name.Equals(server, StringComparison.OrdinalIgnoreCase));
        // Adding a name that's taken would replace that server's token and cut it off: each server needs its own name
        if (add && existed)
            return Done($"There's already a server called {server}. Give this one its own name, like {server}-2 or {server}-EU.", failed: true);
        NewToken = _store.NewServerToken(server);
        NewTokenServer = server;
        _audit.Write(Admin.Audit, existed ? $"new token for {server}" : $"add server {server}", server, true, "from the Panel page");
        return Done(existed ? $"{server} has a new token. Put it in its panel-secrets.json; the old one stops working." : $"{server} is added. Put its token in its panel-secrets.json.");
    }

    // Where game servers connect: only GameModeManager.json changes when it does (their tokens stay the same)
    public IActionResult OnPostAgentUrl(string? url)
    {
        url = (url ?? "").Trim();
        if (url.Length > 0 && GmmSettings.AgentUrlProblem(url) is string problem)
            return Done(problem, failed: true);
        PanelData data = _store.LoadForEdit();
        data.AgentUrl = url;
        _store.Save(data);
        _audit.Write(Admin.Audit, $"agent address {(url.Length > 0 ? url : "the panel's own")}", null, true, "");
        return Done(url.Length > 0
            ? "Agent address saved. Open each server's GameModeManager settings below and paste the new Url into its GameModeManager.json; its token stays the same."
            : "The agent address is the panel's own again.");
    }

    public string AgentUrl => GmmSettings.AgentUrl(Data, Request);

    public IActionResult OnPostRemoveServer(string? server)
    {
        PanelData data = _store.LoadForEdit();
        if (data.Servers.RemoveAll(s => s.Name.Equals(server, StringComparison.OrdinalIgnoreCase)) == 0)
            return Done("That server isn't on the list.", failed: true);
        _store.Save(data);
        _audit.Write(Admin.Audit, $"remove server {server}", server, true, "");
        return Done($"{server} is removed. Its token no longer works.");
    }

    private IActionResult Done(string message, bool failed = false)
    {
        Message = message;
        Failed = failed;
        return Page();
    }
}
