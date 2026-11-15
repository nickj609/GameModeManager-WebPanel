// The viewer's look from the Appearance page: theme, accent colour, warnings colour, panel opacity and blur, header and footer
// opacity, and how dark the backdrop is. Loaded in <head> without defer, so the saved look is set before the page first draws. Stored in
// this browser only (gmm.look); with nothing saved, or storage blocked, the site's look (theme.css: the Appearance
// page's Site look, set by panel admins) and site.css apply.
(function () {
    "use strict";

    // The defaults are the site's look (this script runs after the stylesheets load, so theme.css is read here)
    // theme "" = the site's (Site settings: dark, light or auto, on <html data-site-theme>)
    var defaults = { theme: "", accent: "#d4801a", warn: "#ef5350", glass: 0.88, blur: 16, bars: 0.94, dim: 0.05, pattern: "", pstrength: 6 };   // pattern "" = the site's (Site settings)
    try {
        var siteAccent = getComputedStyle(document.documentElement).getPropertyValue("--accent").trim();
        if (/^#[0-9a-fA-F]{6}$/.test(siteAccent)) defaults.accent = siteAccent;
        var siteWarn = getComputedStyle(document.documentElement).getPropertyValue("--warn").trim();
        if (/^#[0-9a-fA-F]{6}$/.test(siteWarn)) defaults.warn = siteWarn;
        var css = getComputedStyle(document.documentElement);   // Site settings' starting look
        var siteDim = parseFloat(css.getPropertyValue("--dim"));
        if (siteDim >= 0 && siteDim <= 1) defaults.dim = siteDim;
        var siteGlass = parseFloat(css.getPropertyValue("--glass"));
        if (siteGlass >= 0.4 && siteGlass <= 1) defaults.glass = siteGlass;
        var siteBlur = parseFloat(css.getPropertyValue("--blur"));
        if (siteBlur >= 0 && siteBlur <= 32) defaults.blur = siteBlur;
        var siteBars = parseFloat(css.getPropertyValue("--bar-alpha"));
        if (siteBars >= 0.4 && siteBars <= 1) defaults.bars = siteBars;
    } catch (e) { }
    var key = "gmm.look";

    function read() {
        var look = {};
        try { look = JSON.parse(window.localStorage.getItem(key) || "{}") || {}; } catch (e) { look = {}; }
        var result = {};
        Object.keys(defaults).forEach(function (name) { result[name] = look[name] === undefined ? defaults[name] : look[name]; });
        return result;
    }

    function valid(look) {
        return (look.theme === "" || look.theme === "dark" || look.theme === "light") && /^#[0-9a-fA-F]{6}$/.test(look.accent) && /^#[0-9a-fA-F]{6}$/.test(look.warn) && look.glass >= 0.4 && look.glass <= 1 && look.blur >= 0 && look.blur <= 32 &&
            look.bars >= 0.4 && look.bars <= 1 && look.dim >= 0 && look.dim <= 1 &&
            ["", "none", "dots", "grid", "lines"].indexOf(look.pattern) >= 0 && look.pstrength >= 1 && look.pstrength <= 20;
    }

    // A viewer's accent is kept deep enough for white button text and, in the dark look, bright enough to see; the
    // notifications colour bright enough to read on dark, or deep enough not to vanish on light. A colour outside that is
    // nudged towards black or white until it fits (the limits of SiteLook.ColourProblem on the server, for this theme)
    function luminance(hex) {
        function ch(i) { var c = parseInt(hex.substr(i, 2), 16) / 255; return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); }
        return 0.2126 * ch(1) + 0.7152 * ch(3) + 0.0722 * ch(5);
    }
    function mix(hex, toward, share) {
        var out = "#";
        for (var i = 1; i < 7; i += 2) {
            var c = parseInt(hex.substr(i, 2), 16);
            out += ("0" + Math.round(c + (toward - c) * share).toString(16)).slice(-2);
        }
        return out;
    }
    function readable(hex, min, max) {
        for (var step = 1; step <= 20; step++) {
            var l = luminance(hex);
            if (l > max) hex = mix(hex, 0, 0.08);
            else if (l < min) hex = mix(hex, 255, 0.08);
            else break;
        }
        return hex;
    }

    // The theme in use: the viewer's pick, else the site's; "auto" follows the device. Set on <html> before the first paint
    var html = document.documentElement;
    var device = window.matchMedia ? window.matchMedia("(prefers-color-scheme: light)") : null;
    function siteTheme() { return html.getAttribute("data-site-theme") || "dark"; }
    function theme() {
        var pick = read().theme;
        if (!valid(read())) pick = "";
        var wanted = pick || siteTheme();
        if (wanted === "auto") wanted = device && device.matches ? "light" : "dark";
        return wanted === "light" ? "light" : "dark";
    }
    function applyTheme() {
        html.setAttribute("data-theme", theme());
        if (saved()) apply(read());   // The colours' limits depend on the theme
    }

    function apply(look) {
        if (!valid(look)) look = defaults;
        var root = html.style;
        var light = theme() === "light";
        root.setProperty("--accent", readable(look.accent, light ? 0 : 0.03, 0.30));
        root.setProperty("--warn", readable(look.warn, 0.15, light ? 0.75 : 1));
        root.setProperty("--glass", String(look.glass));
        root.setProperty("--blur", look.blur + "px");
        root.setProperty("--bar-alpha", String(look.bars));
        root.setProperty("--dim", String(look.dim));
        // Their own pattern on the panel, over the site's (site.css :root[data-pattern])
        if (look.pattern) html.setAttribute("data-pattern", look.pattern); else html.removeAttribute("data-pattern");
        root.setProperty("--pattern-a", String(look.pstrength / 100));
    }

    function save(look) {
        try { window.localStorage.setItem(key, JSON.stringify(look)); } catch (e) { }
        html.setAttribute("data-theme", theme());
        apply(look);
    }

    // Back to the site's look: drop this browser's own values
    function reset() {
        try { window.localStorage.removeItem(key); } catch (e) { }
        html.setAttribute("data-theme", theme());
        var root = html.style;
        ["--accent", "--warn", "--glass", "--blur", "--bar-alpha", "--dim", "--pattern-a"].forEach(function (name) { root.removeProperty(name); });
        html.removeAttribute("data-pattern");
    }

    function saved() {
        try { return !!window.localStorage.getItem(key); } catch (e) { return false; }
    }

    window.GmmLook = { defaults: defaults, read: read, apply: apply, save: save, reset: reset, theme: theme };
    // Only a look this browser saved: otherwise the site's look stands
    applyTheme();
    if (device) {
        var follow = function () { applyTheme(); };
        if (device.addEventListener) device.addEventListener("change", follow); else if (device.addListener) device.addListener(follow);
    }
})();
