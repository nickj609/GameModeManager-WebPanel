using GmmPanel.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GmmPanel.Pages;

// The server rules, for everyone: the owner's text from Site settings, one rule per line, numbered; "## " lines start a
// section. Each rule may use a little Markdown (Data/Markdown.cs: bold, italic, code, links). No rules: the page isn't there.
[EnableRateLimiting("public")]
public class RulesModel : PanelPage
{
    private readonly PanelStore _store;

    public RulesModel(PanelStore store)
    {
        _store = store;
    }

    public sealed record Section(string? Title, List<string> Rules);
    public List<Section> Sections { get; } = new();

    public IActionResult OnGet()
    {
        Sections.AddRange(Parse(_store.Current.Rules));
        return Sections.Count == 0 || !FeatureOn("rules") ? NotFound() : Page();
    }

    public static List<Section> Parse(string text)
    {
        var sections = new List<Section>();
        Section? current = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith("##"))
            {
                current = new Section(line.TrimStart('#').Trim(), new());
                sections.Add(current);
                continue;
            }
            if (current == null)
            {
                current = new Section(null, new());
                sections.Add(current);
            }
            // A list marker ("- " or "* ") may start the line; "**bold**" may too
            current.Rules.Add(line.StartsWith("- ") || line.StartsWith("* ") ? line[2..].TrimStart() : line);
        }
        return sections.Where(s => s.Rules.Count > 0 || s.Title != null).ToList();
    }
}
