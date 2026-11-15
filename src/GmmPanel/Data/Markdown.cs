using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace GmmPanel.Data;

// A small, safe Markdown for owner-written lines (the rules): **bold**, *italic* or _italic_, ~~struck~~, `code`,
// [text](https://link) and bare https:// links. Everything is HTML-encoded first, so no HTML gets through; links are only
// http(s) or a path on the panel ("/rules"), and outside ones open in a new tab. No block syntax: the caller keeps its
// own lines (one rule per line).
public static partial class Markdown
{
    public static IHtmlContent Inline(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return HtmlString.Empty;
        text = text.Replace("\u0001", "").Replace("\u0002", "");   // The placeholders below
        // Code spans first, so nothing inside them is formatted
        var codes = new List<string>();
        string work = CodeSpan().Replace(text, m => { codes.Add(m.Groups[1].Value); return $"\u0001{codes.Count - 1}\u0001"; });
        // Links next, the same way, so their addresses aren't formatted either
        var links = new List<string>();
        work = LinkSpan().Replace(work, m =>
        {
            string? href = SafeHref(m.Groups[2].Value);
            if (href == null)
                return m.Value;
            links.Add(Anchor(href, Format(WebUtility.HtmlEncode(m.Groups[1].Value))));
            return $"\u0002{links.Count - 1}\u0002";
        });
        work = BareUrl().Replace(work, m =>
        {
            string url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')');
            string rest = m.Value[url.Length..];
            links.Add(Anchor(url, WebUtility.HtmlEncode(url)));
            return $"\u0002{links.Count - 1}\u0002{rest}";
        });

        string html = Format(WebUtility.HtmlEncode(work));
        html = Placeholder('\u0002').Replace(html, m => links[int.Parse(m.Groups[1].Value)]);
        html = Placeholder('\u0001').Replace(html, m => $"<code>{WebUtility.HtmlEncode(codes[int.Parse(m.Groups[1].Value)])}</code>");
        return new HtmlString(html);
    }

    // Emphasis on already-encoded text
    private static string Format(string encoded)
    {
        encoded = Bold().Replace(encoded, "<strong>$1</strong>");
        encoded = Strike().Replace(encoded, "<s>$1</s>");
        encoded = ItalicStar().Replace(encoded, "$1<em>$2</em>");
        encoded = ItalicUnderscore().Replace(encoded, "$1<em>$2</em>");
        return encoded;
    }

    private static string? SafeHref(string href)
    {
        href = href.Trim();
        if (href.StartsWith('/') && !href.StartsWith("//"))
            return href;
        return Uri.TryCreate(href, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri.AbsoluteUri : null;
    }

    private static string Anchor(string href, string inner)
    {
        var sb = new StringBuilder("<a href=\"").Append(WebUtility.HtmlEncode(href)).Append('"');
        if (!href.StartsWith('/'))
            sb.Append(" target=\"_blank\" rel=\"noopener noreferrer nofollow\"");
        return sb.Append('>').Append(inner).Append("</a>").ToString();
    }

    private static Regex Placeholder(char mark) => mark == '\u0001' ? CodeMark() : LinkMark();

    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex CodeSpan();
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")] private static partial Regex LinkSpan();
    [GeneratedRegex(@"(?<![\w/])https?://[^\s<>\u0001\u0002]+")] private static partial Regex BareUrl();
    [GeneratedRegex(@"\*\*(?=\S)(.+?)(?<=\S)\*\*")] private static partial Regex Bold();
    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~")] private static partial Regex Strike();
    [GeneratedRegex(@"(^|[^*\w])\*(?=\S)([^*]+?)(?<=\S)\*(?![*\w])")] private static partial Regex ItalicStar();
    [GeneratedRegex(@"(^|[^_\w])_(?=\S)([^_]+?)(?<=\S)_(?![_\w])")] private static partial Regex ItalicUnderscore();
    [GeneratedRegex("\u0001(\\d+)\u0001")] private static partial Regex CodeMark();
    [GeneratedRegex("\u0002(\\d+)\u0002")] private static partial Regex LinkMark();
}
