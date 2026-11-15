using System.Text.RegularExpressions;

namespace GmmPanel.Data;

// How an audit entry's action reads on the Audit log page: a proper title ("Community link added: Twitch") and, for a server
// command, its raw parameters as code ("map=de_dust2"). The log keeps what was written (exports and old entries are
// unchanged); this only words it, so entries written before it read the same as new ones. Unknown actions get a capital.
public static partial class AuditText
{
    public sealed record Action(string Title, string? Code);

    private static readonly (Regex Pattern, string Title)[] Rules =
    {
        (new(@"^sign in$"), "Signed in"),
        (new(@"^connect$"), "Server connection"),
        (new(@"^link add (.+)$"), "Community link added: $1"),
        (new(@"^link remove (.+)$"), "Community link removed: $1"),
        (new(@"^rules$"), "Rules updated"),
        (new(@"^(benefits|vip page)$"), "VIP page updated"),
        (new(@"^site look$"), "Site look updated"),
        (new(@"^permissions$"), "Permissions updated"),
        (new(@"^retention$"), "Retention updated"),
        (new(@"^discord settings$"), "Discord settings updated"),
        (new(@"^map picture (.+)$"), "Map picture uploaded: $1"),
        (new(@"^background name (.+)$"), "Background renamed: $1"),
        (new(@"^background (.+)$"), "Background uploaded: $1"),
        (new(@"^delete map picture (.+)$"), "Map picture deleted: $1"),
        (new(@"^delete background (.+)$"), "Background deleted: $1"),
        (new(@"^panel admin add (.+)$"), "Panel admin added: $1"),
        (new(@"^panel admin remove (.+)$"), "Panel admin removed: $1"),
        (new(@"^add server (.+)$"), "Server added: $1"),
        (new(@"^remove server (.+)$"), "Server removed: $1"),
        (new(@"^new token for (.+)$"), "Server token reissued: $1"),
        (new(@"^note added on (\d+)$"), "Staff note added: $1"),
        (new(@"^note deleted on (\d+)$"), "Staff note deleted: $1"),
        (new(@"^appeal sent$"), "Appeal submitted"),
        (new(@"^report sent about (.+)$"), "Report submitted: $1"),
        (new(@"^task scheduled: (.+)$"), "Task scheduled: $1"),
        (new(@"^task (\S+) (\S+)$"), "Task $1: $2"),
        (new(@"^appeal (\S+) (accepted|rejected) (.+)$"), "Appeal #$1 $2 $3"),
        (new(@"^report (\S+) closed (.+)$"), "Report #$1 closed $2"),
        (new(@"^report (\S+) (ban|mute|gag|kick|warn|slay) (.+)$"), "Report #$1: $2 applied $3"),
        (new(@"^(report|appeal) (\S+) comment(.*)$"), "$1 #$2: comment added$3"),
        (new(@"^audit export (csv|json) (.+)$"), "Audit log exported ($1) $2"),
        (new(@"^backup downloaded (.+)$"), "Backup downloaded $1"),
        (new(@"^backup restored (.+)$"), "Backup restored $1"),
    };

    public static Action Describe(string what)
    {
        what = what.Trim();
        // Settings saved with their new values: the title, then the values as code
        Match settings = Settings().Match(what);
        if (settings.Success)
            return new(settings.Groups[1].Value == "features" ? "Features updated" : "Integrations updated", settings.Groups[2].Value.Trim() is { Length: > 0 } values ? values : null);
        foreach (var (pattern, title) in Rules)
            if (pattern.IsMatch(what))
                return new(Capital(Upper(pattern.Replace(what, title))), null);
        // A server command (CommandService): its id as words, its parameters as they were sent
        Match command = Command().Match(what);
        if (command.Success)
            return new(Capital(command.Groups[1].Value.Replace('_', ' ')), command.Groups[2].Value.Trim() is { Length: > 0 } code ? code : null);
        return new(Capital(what), null);
    }

    // For searching: the title and the code together, as they show
    public static string Text(string what) { var a = Describe(what); return a.Code == null ? a.Title : $"{a.Title} {a.Code}"; }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    // File formats and the like in capitals ("(csv)" reads "(CSV)")
    private static string Upper(string text) => Formats().Replace(text, m => m.Value.ToUpperInvariant());

    [GeneratedRegex(@"^([a-z]+(?:_[a-z]+)*)((?: \S+=.*)?)$")] private static partial Regex Command();
    [GeneratedRegex(@"\b(csv|json)\b")] private static partial Regex Formats();
    [GeneratedRegex(@"^(features|integrations): ?(.*)$")] private static partial Regex Settings();
}
