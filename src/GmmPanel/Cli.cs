using GmmPanel.Data;

namespace GmmPanel;

// Changing panel.json from the command line, so tokens are made safely and never typed into a file by hand:
//   gmmpanel admin add <SteamID64> admin [name] [--servers A,B]   (a panel admin; "owner" still works)
//   gmmpanel admin remove <SteamID64>
//   gmmpanel admin list
//   gmmpanel token new <server>       (adds the server, or replaces its token; the old one stops working)
//   gmmpanel server remove <server>
//   gmmpanel server list
public static class Cli
{
    public static bool Handles(string command) => command is "admin" or "token" or "server" or "help" or "--help";

    public static string DataDirectory(IConfiguration configuration)
    {
        string directory = configuration["Panel:DataDirectory"] ?? "data";
        return Path.IsPathRooted(directory) ? directory : Path.Combine(AppContext.BaseDirectory, directory);
    }

    public static int Run(string[] args)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var store = new PanelStore(DataDirectory(configuration));

        try
        {
            return (args[0], args.ElementAtOrDefault(1)) switch
            {
                ("admin", "add") when args.Length >= 4 => AddAdmin(store, args),
                ("admin", "remove") when args.Length == 3 => RemoveAdmin(store, args[2]),
                ("admin", "list") => ListAdmins(store),
                ("token", "new") when args.Length == 3 => NewToken(store, args[2]),
                ("server", "remove") when args.Length == 3 => RemoveServer(store, args[2]),
                ("server", "list") => ListServers(store),
                _ => Help()
            };
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Couldn't write {Path.Combine(store.DataDirectory, "panel.json")}: {exception.Message}");
            return 1;
        }
    }

    private static int AddAdmin(PanelStore store, string[] args)
    {
        string steamId = args[2];
        string role = args[3].ToLowerInvariant();
        if (steamId.Length != 17 || !steamId.All(char.IsAsciiDigit))
            return Fail("The SteamID must be a SteamID64: 17 digits, like 76561198000000000.");
        if (!Roles.IsValid(role))
            return Fail("The role is admin: a panel admin. Everyone else's rights come from the servers' own admin flags.");

        var servers = new List<string>();
        var nameParts = new List<string>();
        for (int i = 4; i < args.Length; i++)
        {
            if (args[i] == "--servers" && i + 1 < args.Length)
                servers.AddRange(args[++i].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            else
                nameParts.Add(args[i]);
        }

        PanelData data = store.LoadForEdit();
        data.Admins.RemoveAll(admin => admin.SteamId == steamId);
        data.Admins.Add(new AdminEntry { SteamId = steamId, Role = role, Name = string.Join(" ", nameParts), Servers = servers });
        store.Save(data);
        Console.WriteLine($"{steamId} is now {role}{(servers.Count > 0 ? $" for {string.Join(", ", servers)}" : " for every server")}.");
        return 0;
    }

    private static int RemoveAdmin(PanelStore store, string steamId)
    {
        PanelData data = store.LoadForEdit();
        if (data.Admins.RemoveAll(admin => admin.SteamId == steamId) == 0)
            return Fail($"{steamId} isn't in the admin list.");
        store.Save(data);
        Console.WriteLine($"Removed {steamId}. They're signed out on their next click.");
        return 0;
    }

    private static int ListAdmins(PanelStore store)
    {
        foreach (AdminEntry admin in store.LoadForEdit().Admins)
            Console.WriteLine($"{admin.SteamId}  {admin.Role,-6}  {admin.Name}  {(admin.Servers.Count > 0 ? string.Join(", ", admin.Servers) : "every server")}");
        return 0;
    }

    private static int NewToken(PanelStore store, string server)
    {
        if (!PanelStore.IsServerName(server))
            return Fail("Server names use letters, digits, - and _ (up to 40), like Production or Test.");

        string token = store.NewServerToken(server);

        Console.WriteLine($"Token for {server} (shown once, the panel keeps only its hash):");
        Console.WriteLine();
        Console.WriteLine(token);
        Console.WriteLine();
        Console.WriteLine("Put it in GameModeManager's panel-secrets.json on that server as \"Token\". A connected server keeps its link until it reconnects.");
        return 0;
    }

    private static int RemoveServer(PanelStore store, string server)
    {
        PanelData data = store.LoadForEdit();
        if (data.Servers.RemoveAll(entry => entry.Name.Equals(server, StringComparison.OrdinalIgnoreCase)) == 0)
            return Fail($"There's no server called {server}.");
        store.Save(data);
        Console.WriteLine($"Removed {server}. Its token no longer works.");
        return 0;
    }

    private static int ListServers(PanelStore store)
    {
        foreach (ServerEntry server in store.LoadForEdit().Servers)
            Console.WriteLine(server.Name);
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
            gmmpanel                         start the panel
            gmmpanel admin add <SteamID64> admin [name] [--servers A,B]   (a panel admin; "owner" still works)
            gmmpanel admin remove <SteamID64>
            gmmpanel admin list
            gmmpanel token new <server>      make a server's token (shown once)
            gmmpanel server remove <server>
            gmmpanel server list
            """);
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
