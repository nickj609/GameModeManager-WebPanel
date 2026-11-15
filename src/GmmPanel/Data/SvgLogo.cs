using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace GmmPanel.Data;

// An SVG logo, kept to shapes and text so it can be drawn inside the page (not as a picture): then it takes the theme's
// colours. currentColor is the text colour (white on dark, black on light); class="accent" fills with the accent colour
// and class="accent-stroke" strokes with it. Everything else is dropped: scripts, event handlers, styles, links out,
// embedded pictures and anything an <svg> doesn't need to draw a logo.
public static partial class SvgLogo
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    private static readonly HashSet<string> Elements = new()
    {
        "svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "tspan", "title", "desc",
        "defs", "linearGradient", "radialGradient", "stop", "clipPath", "mask", "use", "symbol"
    };

    private static readonly HashSet<string> Attributes = new()
    {
        "id", "class", "viewBox", "preserveAspectRatio", "d", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "fx", "fy",
        "width", "height", "points", "transform", "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-opacity",
        "stroke-linecap", "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "opacity", "paint-order",
        "font-family", "font-size", "font-weight", "font-style", "letter-spacing", "word-spacing", "text-anchor",
        "dominant-baseline", "alignment-baseline", "dx", "dy", "textLength", "lengthAdjust", "offset", "stop-color", "stop-opacity",
        "gradientUnits", "gradientTransform", "spreadMethod", "clip-path", "clip-rule", "clipPathUnits", "mask", "maskUnits",
        "maskContentUnits", "href", "visibility", "display"
    };

    // Only a local reference ("url(#glow)", "#shape"); no other url, no script
    [GeneratedRegex(@"url\s*\(|javascript:|data:|expression|@import|<|>", RegexOptions.IgnoreCase)]
    private static partial Regex Unsafe();

    [GeneratedRegex(@"^url\(#[A-Za-z][\w-]*\)$")]
    private static partial Regex LocalUrl();

    [GeneratedRegex(@"^#[A-Za-z][\w-]*$")]
    private static partial Regex LocalHref();

    [GeneratedRegex(@"^[A-Za-z][\w-]*$")]
    private static partial Regex Id();

    // The cleaned SVG markup, or null when it isn't an SVG (or nothing drawable is left)
    public static string? Clean(byte[] bytes)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
        if (document.Root is not { } root || root.Name != Svg + "svg")
            return null;
        XElement clean = Element(root)!;
        // Sized by the page: a viewBox is needed to scale it, so make one from the width and height when it has none
        if (clean.Attribute("viewBox") == null && Number(root.Attribute("width")?.Value) is double w && Number(root.Attribute("height")?.Value) is double h)
            clean.SetAttributeValue("viewBox", $"0 0 {w.ToString(CultureInfo.InvariantCulture)} {h.ToString(CultureInfo.InvariantCulture)}");
        clean.SetAttributeValue("width", null);
        clean.SetAttributeValue("height", null);
        if (clean.Attribute("viewBox") == null || !clean.Descendants().Any(e => e.Name.LocalName is not ("title" or "desc" or "defs")))
            return null;
        clean.SetAttributeValue("role", "img");
        return clean.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement? Element(XElement source)
    {
        if (source.Name.Namespace != Svg || !Elements.Contains(source.Name.LocalName))
            return null;
        var element = new XElement(Svg + source.Name.LocalName);
        foreach (XAttribute attribute in source.Attributes())
        {
            string name = attribute.Name == XLink + "href" ? "href" : attribute.Name.Namespace == XNamespace.None ? attribute.Name.LocalName : "";
            if (!Attributes.Contains(name))
                continue;
            string value = attribute.Value.Trim();
            bool ok = name switch
            {
                "href" => LocalHref().IsMatch(value),
                "id" => Id().IsMatch(value),
                "class" => true,
                "fill" or "stroke" or "clip-path" or "mask" => LocalUrl().IsMatch(value) || !Unsafe().IsMatch(value),
                _ => !Unsafe().IsMatch(value)
            };
            if (name == "class")
                value = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(c => c is "accent" or "accent-stroke"));
            if (ok && value.Length > 0 && value.Length <= 20000)
                element.SetAttributeValue(name, value);
        }
        foreach (XNode node in source.Nodes())
        {
            if (node is XElement child && Element(child) is { } kept)
                element.Add(kept);
            else if (node is XText text && source.Name.LocalName is "text" or "tspan" or "title" or "desc")
                element.Add(new XText(text.Value));
        }
        return element;
    }

    private static double? Number(string? value) =>
        double.TryParse((value ?? "").Replace("px", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && n > 0 ? n : null;

    // The same logo twice on a page (header and footer) needs its own ids each time, or the second's gradients point at the first
    public static string WithIds(string markup, string prefix) =>
        IdRef().Replace(markup, m => m.Groups[1].Value + prefix + "-" + m.Groups[2].Value);

    [GeneratedRegex(@"(id=""|url\(#|href=""#)([A-Za-z][\w-]*)")]
    private static partial Regex IdRef();
}
