using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// The panel's translations (src/GmmPanel/lang/<code>.json), from the repo root:
//   dotnet run --project tools/LangTool -- missing          English strings the code uses that en.json lacks
//   dotnet run --project tools/LangTool -- check            missing, plus every language has en.json's keys and placeholders
//   dotnet run --project tools/LangTool -- add rows.json    adds new strings to every language file
// rows.json: { "English text": { "de": "...", "es": "...", ... every other language }, ... }. A key already there is
// skipped. Strings the scan can't see (passed through a variable, e.g. T(Model.Note)) still need adding by hand.

string root = Directory.GetCurrentDirectory();
string app = Path.Combine(root, "src", "GmmPanel");
string langDir = Path.Combine(app, "lang");
if (!Directory.Exists(langDir))
{
    Console.Error.WriteLine("Run this from the repository root (src/GmmPanel/lang not found).");
    return 2;
}

var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
JsonObject Load(string code) => JsonNode.Parse(File.ReadAllText(Path.Combine(langDir, code + ".json")))!.AsObject();
string[] codes = Directory.GetFiles(langDir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order().ToArray();

switch (args.FirstOrDefault())
{
    case "missing":
        return Missing(Load("en")).Count == 0 ? 0 : 1;
    case "check":
        return Check();
    case "add" when args.Length > 1:
        return Add(args[1]);
    default:
        Console.WriteLine("Usage: missing | check | add <rows.json>");
        return 2;
}

// Every T("..."), F("...") (its first string) and N(n, "...", "...") on one line, in the app's .cshtml and .cs files, and
// the strings inside T(x ? "a" : "b") or T(x switch { ... => "a" })
List<string> Used()
{
    var keys = new SortedSet<string>(StringComparer.Ordinal);
    var call = new Regex(@"(?<![A-Za-z_.])(T|F|N)\((?:[^()\r\n]|\([^()\r\n]*\))*\)");
    var literal = new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"");
    // Lang.cs defines T, F and N; Site.cshtml.cs has its own F for form fields
    string[] skip = { Path.Combine("Data", "Lang.cs"), Path.Combine("Pages", "Site.cshtml.cs") };
    foreach (string file in Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
                 .Where(f => !skip.Any(s => f.EndsWith(s)))
                 .Where(f => (f.EndsWith(".cshtml") || f.EndsWith(".cs")) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        foreach (Match m in call.Matches(File.ReadAllText(file)))
        {
            var strings = literal.Matches(m.Value).Select(s => s.Groups[1].Value.Replace("\\\"", "\"")).ToList();
            foreach (string s in m.Value.StartsWith("F(") ? strings.Take(1) : strings)
                if (s.Length > 0)
                    keys.Add(s);
        }
    return keys.ToList();
}

// Strings that look like keys to the scan but are values compared in code, not text shown
static bool NotText(string s) => s is "ACCEPTED" or "OPEN" or "REJECTED" or "CLOSED" or "appeal" or "report" or "role" or "en" or "...";

List<string> Missing(JsonObject en)
{
    var missing = Used().Where(k => !NotText(k) && !en.ContainsKey(k)).ToList();
    foreach (string k in missing)
        Console.WriteLine($"en.json lacks: {k}");
    return missing;
}

int Check()
{
    JsonObject en = Load("en");
    int problems = Missing(en).Count;
    string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Order());
    foreach (string code in codes.Where(c => c != "en"))
    {
        JsonObject t = Load(code);
        foreach (var (key, _) in en)
        {
            if (t[key] is not JsonValue value) { Console.WriteLine($"{code}: missing '{key}'"); problems++; continue; }
            if (Holes(key) != Holes(value.GetValue<string>())) { Console.WriteLine($"{code}: placeholders differ '{key}'"); problems++; }
        }
        foreach (var (key, _) in t.Where(p => !en.ContainsKey(p.Key)))
        {
            Console.WriteLine($"{code}: extra '{key}'");
            problems++;
        }
    }
    Console.WriteLine($"{problems} problems, {en.Count} keys, {codes.Length} languages");
    return problems == 0 ? 0 : 1;
}

int Add(string rowsFile)
{
    var rows = JsonNode.Parse(File.ReadAllText(rowsFile))!.AsObject();
    var others = codes.Where(c => c != "en").ToList();
    // Every row needs every language first, so no file ends up with a key the others lack
    foreach (var (english, row) in rows)
    {
        var missing = others.Where(c => row?[c] is not JsonValue).ToList();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"'{english}' lacks: {string.Join(", ", missing)}");
            return 1;
        }
    }
    int added = 0;
    foreach (string code in codes)
    {
        JsonObject file = Load(code);
        foreach (var (english, row) in rows)
        {
            if (file.ContainsKey(english))
                continue;
            file[english] = code == "en" ? english : row![code]!.GetValue<string>();
            if (code == "en")
                added++;
        }
        File.WriteAllText(Path.Combine(langDir, code + ".json"), file.ToJsonString(options) + "\n");
    }
    Console.WriteLine($"{added} added to {codes.Length} languages");
    return Check();
}
