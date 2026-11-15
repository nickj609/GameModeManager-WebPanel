// The panel's only script besides htmx. No inline scripts anywhere, so the Content-Security-Policy can forbid them.
//  1. Send the antiforgery token with every htmx request, and show refused requests as toasts.
//  2. The server page's sections (the rail on the left), kept across the 5-second refresh.
//  2b. Questions in the in-game menus' style instead of the browser's confirm(): a button's data-confirm asks first, a
//      setting that turns on sv_cheats warns, and after a setting change GameModeManager's follow-up is asked
//      ("Changes need a match restart", "Changes apply next round").
//  2c. The console: history with the arrow keys, the log kept scrolled to the bottom.
//  3. The backdrop: a slowly turning panorama or the owner's video, dust in the light, and the picker in the corner.
(function () {
    "use strict";

    // The page's own translations of this script's text (meta i18n, in the viewer's language); English when missing
    var I18N = {};
    try { I18N = JSON.parse((document.querySelector('meta[name="i18n"]') || {}).content || "{}"); } catch (e) { }
    function t(english) { return I18N[english] || english; }

    // Browser storage can be missing or throw (private windows, blocked site data); the page works without it
    function load(key) { try { return window.localStorage.getItem(key); } catch (e) { return null; } }
    function save(key, value) { try { window.localStorage.setItem(key, value); } catch (e) { } }

    // ---------- 1. htmx ----------

    document.addEventListener("htmx:configRequest", function (event) {
        var meta = document.querySelector('meta[name="request-token"]');
        if (meta) event.detail.headers["RequestVerificationToken"] = meta.content;
    });

    function toast(text) {
        var toasts = document.getElementById("toasts");
        if (!toasts) return;
        var item = document.createElement("div");
        item.className = "toast err";
        item.setAttribute("role", "alert");
        item.textContent = text;
        toasts.appendChild(item);
    }

    document.addEventListener("htmx:responseError", function (event) {
        var status = event.detail.xhr.status;
        toast(status === 429 ? t("Too many requests. Wait a moment.")
            : status === 401 || status === 403 ? t("You're signed out. Reload the page.")
            : t("The panel couldn't do that ({0}).").replace("{0}", status));
    });

    // ---------- 2. Sections ----------

    // The page that has sections: a server's Manage or Admin page, or Moderation. Each remembers its own section.
    function tabBody() {
        return document.getElementById("server-body") || document.getElementById("mod-body") || document.querySelector(".tab-body");
    }

    function tabKey() {
        var body = tabBody();
        return body && body.dataset.tabkey ? body.dataset.tabkey : "gmm.tab";
    }

    function showTab(name) {
        var body = tabBody();
        if (!body) return;
        var panes = body.querySelectorAll(".pane");
        var found = false;
        panes.forEach(function (pane) { if (pane.dataset.pane === name) found = true; });
        if (!found) name = panes.length ? panes[0].dataset.pane : "";
        body.querySelectorAll(".rail-item").forEach(function (item) {
            var on = item.dataset.tab === name;
            item.classList.toggle("on", on);
            item.setAttribute("aria-current", on ? "true" : "false");
            if (on) railLabel(item);
        });
        panes.forEach(function (pane) {
            var on = pane.dataset.pane === name;
            pane.classList.toggle("on", on);
            // Moderation's lists load when first shown (htmx "revealed" doesn't fire for a hidden pane)
            if (on && pane.dataset.load && !pane.dataset.loaded && window.htmx) {
                pane.dataset.loaded = "1";
                window.htmx.trigger(pane.querySelector("[data-list]") || pane, "load-list");
            }
        });
    }

    // On a phone the rail folds into a picker: a button showing the open section (its icon, name and any count) that drops
    // the list down. The button is added here, so the pages' rails stay plain lists.
    function railToggle(rail) {
        var button = rail.querySelector(":scope > .rail-toggle");
        if (button) return button;
        button = document.createElement("button");
        button.type = "button";
        button.className = "rail-toggle";
        button.setAttribute("aria-expanded", "false");
        rail.insertBefore(button, rail.firstChild);
        return button;
    }

    function railLabel(item) {
        var rail = item.closest(".rail");
        if (!rail) return;
        var button = railToggle(rail);
        button.textContent = "";
        var icon = item.querySelector(".ic");
        if (icon) button.appendChild(icon.cloneNode(true));
        var name = document.createElement("span");
        name.textContent = (item.querySelector("span") || item).textContent;
        button.appendChild(name);
        var count = item.querySelector(".count");
        if (count) button.appendChild(count.cloneNode(true));
        var caret = document.createElementNS("http://www.w3.org/2000/svg", "svg");
        caret.setAttribute("class", "ic caret");
        caret.setAttribute("aria-hidden", "true");
        var use = document.createElementNS("http://www.w3.org/2000/svg", "use");
        use.setAttribute("href", "#i-caret");
        caret.appendChild(use);
        button.appendChild(caret);
    }

    function railOpen(rail, open) {
        rail.classList.toggle("open", open);
        var button = rail.querySelector(":scope > .rail-toggle");
        if (button) button.setAttribute("aria-expanded", open ? "true" : "false");
    }

    document.addEventListener("click", function (event) {
        var toggle = event.target.closest(".rail-toggle");
        if (toggle) railOpen(toggle.parentElement, !toggle.parentElement.classList.contains("open"));
        document.querySelectorAll(".rail.open").forEach(function (rail) { if (!rail.contains(event.target)) railOpen(rail, false); });
        var item = event.target.closest(".rail-item");
        if (item) {
            save(tabKey(), item.dataset.tab);
            showTab(item.dataset.tab);
            var rail = item.closest(".rail");
            if (rail) railOpen(rail, false);
        }
        // The header's server switcher and account menu close on a click anywhere else
        ["switcher", "account", "nav-menu"].forEach(function (id) {
            var menu = document.getElementById(id);
            if (menu && menu.open && !event.target.closest("#" + id)) menu.open = false;
        });
    });

    document.addEventListener("keydown", function (event) {
        if (event.key !== "Escape") return;
        document.querySelectorAll(".rail.open").forEach(function (rail) {
            railOpen(rail, false);
            var button = rail.querySelector(":scope > .rail-toggle");
            if (button) button.focus();
        });
        ["switcher", "account", "nav-menu"].forEach(function (id) {
            var menu = document.getElementById(id);
            if (menu && menu.open) menu.open = false;
        });
    });

    // The menu (from the right): a modal dialog, so Escape and the focus work on their own; a click on the dimmed page closes it
    (function () {
        var drawer = document.getElementById("drawer");
        var open = document.getElementById("menu-button");
        if (!drawer || !open || !drawer.showModal) return;
        function shut() { if (drawer.open) drawer.close(); }
        open.addEventListener("click", function () {
            drawer.showModal();
            open.setAttribute("aria-expanded", "true");
            var on = drawer.querySelector(".drawer-link.on") || drawer.querySelector(".drawer-link");
            if (on) on.focus({ focusVisible: false });
        });
        drawer.addEventListener("close", function () { open.setAttribute("aria-expanded", "false"); open.focus(); });
        drawer.addEventListener("click", function (event) { if (event.target === drawer) shut(); });
        document.getElementById("drawer-close").addEventListener("click", shut);
    })();

    // Copy buttons (the setup wizard's config snippets): data-copy names the element whose text is copied
    document.addEventListener("click", function (event) {
        var button = event.target.closest("[data-copy]");
        if (!button || !navigator.clipboard) return;
        var source = document.getElementById(button.dataset.copy);
        if (!source) return;
        navigator.clipboard.writeText(source.textContent).then(function () {
            var label = button.textContent;
            button.textContent = t("Copied");
            window.setTimeout(function () { button.textContent = label; }, 1500);
        });
    });

    // After a ban, unban, mute ...: reload the list that's open (the others load fresh when they're opened)
    document.body.addEventListener("refreshList", function () {
        var list = document.querySelector("#mod-body .pane.on [data-list]");
        if (list && window.htmx) window.htmx.trigger(list, "load-list");
        document.querySelectorAll("#mod-body .pane:not(.on)[data-loaded]").forEach(function (pane) { delete pane.dataset.loaded; });
    });

    // The dashboard's map pictures: the address is in data-art, set through CSSOM (the CSP forbids style= attributes)
    function showArt() {
        document.querySelectorAll(".card-art[data-art]").forEach(function (art) {
            var url = art.dataset.art;
            if (url && /^\/(media|maps)\/[^"'()\\]+$/.test(url)) art.style.backgroundImage = 'url("' + url + '")';
        });
    }

    // The dashboard's filter bar: hides cards by mode, map or "with players", and can group them by mode. Kept for this
    // tab (sessionStorage) and applied again after each 10-second refresh.
    function filterCards() {
        var form = document.querySelector("[data-card-filter]");
        if (!form) return;
        var mode = form.elements.mode.value, withPlayers = form.elements.players.checked;
        try { sessionStorage.setItem("gmm.filter", JSON.stringify({ mode: mode, players: withPlayers })); } catch (e) { }
        document.querySelectorAll("#servers .chip-server").forEach(function (chip) {
            var count = chip.querySelector(".chip-count");
            var empty = count && /^0/.test(count.textContent.trim());
            chip.hidden = (mode && chip.dataset.mode !== mode) || (withPlayers && empty);
        });
        // The picked server filtered out: pick the first one left (the only one, when the filter leaves one), so the card
        // below always matches a tab in view
        var picked = document.querySelector("#servers .chip-server.on");
        if (!picked || picked.hidden) {
            var first = document.querySelector("#servers .chip-server:not([hidden])");
            if (first) {
                first.click();
                if (typeof showChip === "function") showChip(first, false);
            }
        }
    }

    // A picked chip lights up at once (the strip's next refresh agrees: the server remembers the pick)
    document.addEventListener("click", function (event) {
        var chip = event.target.closest && event.target.closest(".chip-server");
        if (!chip) return;
        document.querySelectorAll(".chip-server.on").forEach(function (c) { c.classList.remove("on"); c.setAttribute("aria-selected", "false"); });
        chip.classList.add("on");
        chip.setAttribute("aria-selected", "true");
        carouselState();
    });

    (function () {
        var form = document.querySelector("[data-card-filter]");
        if (!form) return;
        try {
            var saved = JSON.parse(sessionStorage.getItem("gmm.filter") || "null");
            if (saved) {
                form.elements.mode.value = saved.mode || "";
                form.elements.players.checked = !!saved.players;
            }
        } catch (e) { }
        form.addEventListener("change", function () { filterCards(); carousel(); var row = strip(); if (row) showChip(row.querySelector(".chip-server.on"), false); });
        filterCards(); carousel();
    })();

    // ---------- 2b. Questions ----------

    var dialog = document.getElementById("dlg");

    // choices: [{ label, kind: "go" | "danger" | "cancel", run }]. Escape or a click outside is the last (cancel) choice.
    // One answer and Cancel: both as buttons at the bottom right, like any pop-up. Several answers (a setting's follow-up
    // question): a list of them, as in game, and Cancel at the bottom right.
    function ask(title, text, choices) {
        if (!dialog || !dialog.showModal) return;
        document.getElementById("dlg-title").textContent = title;
        document.getElementById("dlg-text").textContent = text || "";
        var list = document.getElementById("dlg-choices");
        var actions = document.getElementById("dlg-actions");
        list.textContent = "";
        actions.textContent = "";
        var answers = choices.filter(function (c) { return c.kind !== "cancel"; });
        var cancels = choices.filter(function (c) { return c.kind === "cancel"; });
        function make(choice, className) {
            var button = document.createElement("button");
            button.type = "button";
            button.className = className;
            button.textContent = choice.label;
            button.addEventListener("click", function () {
                dialog.close();
                if (choice.run) choice.run();
            });
            return button;
        }
        var single = answers.length === 1;
        cancels.forEach(function (c) { actions.appendChild(make(c, "small dlg-cancel")); });
        if (single)
            actions.appendChild(make(answers[0], "small " + (answers[0].kind === "danger" ? "danger-solid" : "primary")));
        else
            answers.forEach(function (c) { list.appendChild(make(c, "dlg-choice " + (c.kind || "go"))); });
        list.hidden = single;
        actions.hidden = actions.children.length === 0;
        dialog.showModal();
        var first = (single ? actions.lastElementChild : list.querySelector("button"));
        if (first) first.focus();
    }

    if (dialog) {
        dialog.addEventListener("click", function (event) { if (event.target === dialog) dialog.close(); });
    }

    // Plain (non-htmx) forms: a submit button's data-confirm asks first too (the Panel page's Remove buttons)
    document.addEventListener("submit", function (event) {
        var form = event.target;
        var submitter = event.submitter;
        if (form.hasAttribute("hx-post") || form.hasAttribute("hx-get") || !submitter || !submitter.dataset.confirm) return;
        if (form.dataset.confirmed) { delete form.dataset.confirmed; return; }
        event.preventDefault();
        ask(submitter.dataset.confirmTitle || "Are you sure?", submitter.dataset.confirm, [
            { label: submitter.dataset.confirmYes || "Yes", kind: submitter.dataset.confirmDanger === "true" ? "danger" : "go",
              run: function () { form.dataset.confirmed = "1"; form.requestSubmit(submitter); } },
            { label: t("Cancel"), kind: "cancel" }
        ]);
    });

    // The server page's command address, for follow-up commands
    function send(command, values) {
        var body = document.getElementById("server-body");
        if (!body || !window.htmx) return;
        var data = values || {};
        data.command = command;
        window.htmx.ajax("POST", body.dataset.post, { target: "#toasts", swap: "beforeend", values: data });
    }

    // Every htmx request passes here first. A form's submit button can carry data-confirm (with data-confirm-title,
    // -yes, -danger); a value setting's form checks whether the chosen value turns on sv_cheats.
    document.addEventListener("htmx:confirm", function (event) {
        var trigger = event.detail.triggeringEvent;
        var submitter = trigger && trigger.submitter;
        var form = event.detail.elt;

        // htmx forgets which button was pressed once a dialog takes the focus, so keep its value in the form
        if (submitter && submitter.name && form && form.tagName === "FORM") {
            var kept = form.querySelector('input[data-submitter]');
            if (!kept) {
                kept = document.createElement("input");
                kept.type = "hidden";
                kept.setAttribute("data-submitter", "");
                form.appendChild(kept);
            }
            kept.name = submitter.name;
            kept.value = submitter.value;
        }

        var question = submitter && submitter.dataset.confirm;
        var title = submitter && submitter.dataset.confirmTitle;
        var yes = submitter && submitter.dataset.confirmYes;
        var danger = submitter && submitter.dataset.confirmDanger === "true";

        if (!question && form && form.dataset && form.dataset.cheatsCheck) {
            var chosen = form.querySelector("select option:checked");
            if (chosen && chosen.dataset.cheats) {
                question = chosen.value + " turns on sv_cheats: every player can use cheat commands like noclip and god mode until it's turned off.";
                title = t("Turn on sv_cheats?");
                yes = t("Turn it on");
                danger = true;
                var confirmInput = form.querySelector('input[name="confirm"]') || document.createElement("input");
                confirmInput.type = "hidden";
                confirmInput.name = "confirm";
                confirmInput.value = "true";
                form.appendChild(confirmInput);
            }
        }

        // A task with "Also save in GameModeManager.json" ticked: say what that does to the file first (Run now and Schedule)
        if (form && form.querySelector && form.querySelector('input[name="save"]:checked')) {
            question = t("It also writes the change into GameModeManager.json on each ticked server, so it lasts after a restart. The file's comments go; a copy from before the panel's first change is kept as GameModeManager.json.before-panel.bak. A deploy that uploads its own GameModeManager.json replaces it.");
            title = t("Save to GameModeManager.json?");
            yes = submitter && submitter.classList.contains("w-later-btn") ? t("Schedule it") : t("Save it");
            danger = false;
        }

        if (!question) return;
        event.preventDefault();

        // data-confirm-choose="minutes" with data-choices="60=1 hour|1440=1 day|...": the question's buttons are the
        // choices, and the one picked fills that field (how long a ban from a report lasts)
        var field = submitter && submitter.dataset.confirmChoose;
        if (field && submitter.dataset.choices) {
            var buttons = submitter.dataset.choices.split("|").map(function (pair) {
                var at = pair.indexOf("="), value = pair.slice(0, at), label = pair.slice(at + 1);
                return { label: label, kind: danger ? "danger" : "go", run: function () {
                    var input = form.querySelector('input[name="' + field + '"]') || document.createElement("input");
                    input.type = "hidden";
                    input.name = field;
                    input.value = value;
                    form.appendChild(input);
                    event.detail.issueRequest(true);
                } };
            });
            buttons.push({ label: t("Cancel"), kind: "cancel" });
            ask(title || t("Are you sure?"), question, buttons);
            return;
        }

        ask(title || t("Are you sure?"), question, [
            { label: yes || t("Yes"), kind: danger ? "danger" : "go", run: function () { event.detail.issueRequest(true); } },
            { label: t("Cancel"), kind: "cancel" }
        ]);
    });

    // GameModeManager's follow-up after a setting change, the same choices its menus give
    document.body.addEventListener("followup", function (event) {
        var f = event.detail && (event.detail.kind ? event.detail : event.detail.value);
        if (!f) return;
        if (f.kind === "restart") {
            ask(t("Changes need a match restart"), t("{0} applies after the match restarts.").replace("{0}", f.settings), [
                { label: t("Restart now"), kind: "go", run: function () { send("restart_match", { settings: f.settings }); } },
                { label: t("Restart later"), kind: "cancel" }
            ]);
        } else if (f.kind === "round") {
            var choices = [];
            if (f.canReplay) choices.push({ label: t("Replay round"), kind: "go", run: function () { send("replay_round"); } });
            if (f.canEndRound) choices.push({ label: t("End round"), kind: "go", run: function () { send("end_round", { settings: f.settings }); } });
            choices.push({ label: t("Restart match"), kind: "go", run: function () { send("restart_match", { settings: f.settings }); } });
            choices.push({ label: t("Next round"), kind: "cancel" });
            ask(t("Changes apply next round"), t("{0} applies from the next round.").replace("{0}", f.settings), choices);
        }
    });

    // ---------- 2c. Console ----------

    var history = [];
    var historyAt = -1;

    document.addEventListener("keydown", function (event) {
        var input = event.target;
        if (!input.closest || !input.closest(".con-form") || history.length === 0) return;
        if (event.key === "ArrowUp") {
            historyAt = historyAt < 0 ? history.length - 1 : Math.max(0, historyAt - 1);
        } else if (event.key === "ArrowDown") {
            historyAt = historyAt < 0 ? -1 : historyAt + 1;
            if (historyAt >= history.length) historyAt = -1;
        } else {
            return;
        }
        event.preventDefault();
        input.value = historyAt < 0 ? "" : history[historyAt];
    });

    document.addEventListener("htmx:beforeRequest", function (event) {
        var form = event.detail.elt;
        if (!form.classList || !form.classList.contains("con-form")) return;
        var line = form.querySelector("input").value.trim();
        if (!line) { event.preventDefault(); return; }
        if (history[history.length - 1] !== line) history.push(line);
        if (history.length > 50) history.shift();
        historyAt = -1;
    });

    document.addEventListener("htmx:afterRequest", function (event) {
        var form = event.detail.elt;
        if (!form.classList || !form.classList.contains("con-form")) return;
        var input = form.querySelector("input");
        input.value = "";
        window.setTimeout(function () { input.focus(); }, 0);
        var log = document.querySelector(".con-log");
        if (log) log.scrollTop = log.scrollHeight;
    });

    // ---------- 2d. Appearance page ----------

    (function () {
        var page = document.getElementById("look");
        if (!page || !window.GmmLook) return;
        var look = window.GmmLook.read();
        var sliders = {
            glass: { input: "look-glass", text: function (v) { return Math.round(v * 100) + "%"; } },
            blur: { input: "look-blur", text: function (v) { return v + " px"; } },
            bars: { input: "look-bars", text: function (v) { return Math.round(v * 100) + "%"; } },
            pstrength: { input: "look-pstrength", text: function (v) { return v; } },
            dim: { input: "look-dim", text: function (v) { return Math.round(v * 100) + "%"; } }
        };

        function show() {
            Object.keys(sliders).forEach(function (name) {
                document.getElementById(sliders[name].input).value = look[name];
                document.getElementById(sliders[name].input + "-out").textContent = sliders[name].text(look[name]);
            });
            document.getElementById("look-accent").value = look.accent;
            var themePick = document.getElementById("look-theme");
            if (themePick) themePick.value = look.theme || "";
            var patternPick = document.getElementById("look-pattern");
            if (patternPick) patternPick.value = look.pattern || "";
            var warn = document.getElementById("look-warn");
            if (warn) warn.value = look.warn;
            page.querySelectorAll(".swatch").forEach(function (swatch) {
                swatch.setAttribute("aria-pressed", swatch.dataset.accent.toLowerCase() === look.accent.toLowerCase() ? "true" : "false");
            });
        }

        function change(name, value) {
            look[name] = value;
            window.GmmLook.save(look);
            show();
        }

        Object.keys(sliders).forEach(function (name) {
            document.getElementById(sliders[name].input).addEventListener("input", function (event) {
                change(name, parseFloat(event.target.value));
            });
        });
        page.querySelectorAll(".swatch").forEach(function (swatch) {
            swatch.addEventListener("click", function () { change("accent", swatch.dataset.accent); });
        });
        document.getElementById("look-accent").addEventListener("input", function (event) { change("accent", event.target.value); });
        var warnInput = document.getElementById("look-warn");
        if (warnInput) warnInput.addEventListener("input", function (event) { change("warn", event.target.value); });
        var themeInput = document.getElementById("look-theme");
        if (themeInput) themeInput.addEventListener("change", function (event) { change("theme", event.target.value); });
        var patternInput = document.getElementById("look-pattern");
        if (patternInput) patternInput.addEventListener("change", function (event) { change("pattern", event.target.value); });
        document.getElementById("look-reset").addEventListener("click", function () {
            window.GmmLook.reset();
            look = window.GmmLook.read();
            show();
        });
        show();
    })();

    // ---------- 2e. Details pop-up (Bans): one record in full, from the row's data-details ----------

    (function () {
        var dialog = document.getElementById("details-dlg");
        if (!dialog) return;
        var title = document.getElementById("details-title");
        var list = document.getElementById("details-list");
        var profile = document.getElementById("details-profile");
        var steam = document.getElementById("details-steam");
        var leetify = document.getElementById("details-leetify");
        var sub = document.getElementById("details-sub");
        document.addEventListener("click", function (event) {
            var button = event.target.closest && event.target.closest("[data-details]");
            if (!button) return;
            var record;
            try { record = JSON.parse(button.dataset.details); } catch (e) { return; }
            title.textContent = record.title || "";
            sub.textContent = record.steamid || "";
            list.textContent = "";
            (record.rows || []).forEach(function (pair) {
                if (!pair[1]) return;
                var term = document.createElement("dt");
                term.textContent = pair[0];
                var value = document.createElement("dd");
                if (pair[2]) {
                    var dot = document.createElement("span");   // A status: the coloured dot, as in the table
                    dot.className = "ind " + pair[2];
                    dot.textContent = pair[1];
                    value.appendChild(dot);
                } else {
                    value.textContent = pair[1];
                }
                list.appendChild(term);
                list.appendChild(value);
            });
            var id = /^7656119\d{10}$/.test(record.steamid || "") ? record.steamid : null;
            profile.hidden = steam.hidden = leetify.hidden = !id;
            if (id) {
                profile.href = "/u/" + id;
                steam.href = "https://steamcommunity.com/profiles/" + id;
                leetify.href = "https://leetify.com/app/profile/" + id;
            }
            dialog.showModal();
        });
        document.getElementById("details-close").addEventListener("click", function () { dialog.close(); });
        dialog.addEventListener("click", function (event) { if (event.target === dialog) dialog.close(); });   // A click outside
    })();

    // ---------- 2f. Pop-ups drawn by the page (a report or appeal on the profile): data-dlg opens, data-dlg-close shuts ----------

    document.addEventListener("click", function (event) {
        var opener = event.target.closest && event.target.closest("[data-dlg]");
        if (opener) {
            var target = document.getElementById(opener.dataset.dlg);
            if (target && target.showModal) target.showModal();
            return;
        }
        var closer = event.target.closest && event.target.closest("[data-dlg-close]");
        if (closer && closer.closest("dialog")) { closer.closest("dialog").close(); return; }
        if (event.target.matches && event.target.matches("dialog.ticket-dlg")) event.target.close();   // A click outside
    });

    // A colour picker's hex code beside it (a community link's Edit pop-up)
    document.addEventListener("input", function (event) {
        var id = event.target.dataset && event.target.dataset.colourCode;
        var code = id && document.getElementById(id);
        if (code) code.textContent = event.target.value;
    });

    // ---------- 3. Backdrop ----------

    var bg = document.getElementById("bg");
    var still = document.getElementById("bg-still");
    var video = document.getElementById("bg-video");
    var canvas = document.getElementById("bg-dust");
    var choices = [];
    try { choices = JSON.parse(bg ? bg.dataset.backgrounds : "[]"); } catch (e) { choices = []; }

    // Each viewer's own pick, else the site's (Site settings: motion on or off, the default background; "" = Auto)
    var motion = load("gmm.motion") ? load("gmm.motion") === "on" : bg ? bg.dataset.motion !== "off" : true;
    var choice = load("gmm.bg") || (bg && bg.dataset.default) || "auto";
    var shown = null;

    function find(id) {
        for (var i = 0; i < choices.length; i++) if (choices[i].id === id) return choices[i];
        return null;
    }

    // "Auto": like CS2's menu, the scene of the map being played: the background named after the map of the server on
    // screen (Manage, the dashboard's picked server, a server's Details), else the last background picked by hand
    function resolve() {
        if (choice !== "auto") return find(choice) || find(bg.dataset.default || "") || find(bg.dataset.fallback || "") || choices[0];
        var onScreen = document.querySelector("#server-body[data-map], .spot[data-map], .info-banner[data-map]");
        // The server the page shows, else the one picked in the header (so Auto keeps its map on every page)
        var map = ((onScreen && onScreen.dataset.map) || bg.dataset.map || "").toLowerCase();
        if (map) {
            for (var i = 0; i < choices.length; i++) {
                if ((choices[i].map || "").toLowerCase() === map) return choices[i];
            }
        }
        // Then the last one picked by hand, then Dust II (the panel's own default), then the first one
        return find(load("gmm.bg.fallback") || "") || find(bg.dataset.fallback || "") || choices[0];
    }

    function apply() {
        if (!bg || choices.length === 0) return;
        var next = resolve();
        document.documentElement.classList.toggle("no-motion", !motion);
        if (shown && next.id === shown.id) { syncVideo(); return; }
        shown = next;

        still.hidden = next.kind !== "still";
        video.hidden = next.kind !== "video";
        if (next.kind === "still") still.style.backgroundImage = 'url("' + next.url + '")';
        if (next.kind === "video") {
            video.src = next.url;
        } else {
            video.removeAttribute("src");
            video.load();
        }
        syncVideo();
        renderOptions();
    }

    function syncVideo() {
        if (video.hidden || !video.getAttribute("src")) return;
        if (motion) {
            var playing = video.play();
            if (playing && playing.catch) playing.catch(function () { });
        } else {
            video.pause();
        }
    }

    // ---------- The picker ----------

    var button = document.getElementById("bg-button");
    var menu = document.getElementById("bg-menu");
    var options = document.getElementById("bg-options");
    var motionButton = document.getElementById("bg-motion");
    var motionState = document.getElementById("bg-motion-state");

    function option(id, name, note) {
        var item = document.createElement("button");
        item.type = "button";
        item.className = "bg-option";
        item.setAttribute("role", "option");
        item.setAttribute("aria-selected", choice === id ? "true" : "false");
        item.dataset.choice = id;
        var label = document.createElement("span");
        label.textContent = name;
        item.appendChild(label);
        if (note) {
            var small = document.createElement("small");
            small.textContent = note;
            item.appendChild(small);
        }
        return item;
    }

    function renderOptions() {
        if (!options) return;
        options.textContent = "";
        options.appendChild(option("auto", "Auto", choice === "auto" && shown ? shown.name : "by map"));
        choices.forEach(function (c) {
            options.appendChild(option(c.id, c.name, c.kind === "video" ? "video" : c.scene ? "" : "yours"));
        });
        motionButton.setAttribute("aria-checked", motion ? "true" : "false");
        motionState.textContent = motion ? t("On") : t("Off");
        motionState.classList.toggle("on", motion);
    }

    if (button && menu) {
        button.addEventListener("click", function () {
            var open = menu.hidden;
            menu.hidden = !open;
            button.setAttribute("aria-expanded", open ? "true" : "false");
            if (open) renderOptions();
        });
        document.addEventListener("click", function (event) {
            if (!menu.hidden && !event.target.closest(".bg-picker")) {
                menu.hidden = true;
                button.setAttribute("aria-expanded", "false");
            }
        });
        document.addEventListener("keydown", function (event) {
            if (event.key === "Escape" && !menu.hidden) {
                menu.hidden = true;
                button.setAttribute("aria-expanded", "false");
                button.focus();
            }
        });
        options.addEventListener("click", function (event) {
            var item = event.target.closest(".bg-option");
            if (!item) return;
            choice = item.dataset.choice;
            save("gmm.bg", choice);
            if (choice !== "auto") save("gmm.bg.fallback", choice);
            shown = null;
            apply();
        });
        motionButton.addEventListener("click", function () {
            motion = !motion;
            save("gmm.motion", motion ? "on" : "off");
            apply();
            renderOptions();
            dust.toggle();
        });
    }

    // ---------- Dust drifting through the light ----------

    var dust = (function () {
        var context = canvas ? canvas.getContext("2d") : null;
        var motes = [];
        var running = false;
        var last = 0;

        function size() {
            var ratio = Math.min(window.devicePixelRatio || 1, 2);
            canvas.width = Math.floor(window.innerWidth * ratio);
            canvas.height = Math.floor(window.innerHeight * ratio);
            context.setTransform(ratio, 0, 0, ratio, 0, 0);
        }

        function seed() {
            var count = Math.round(Math.min(90, window.innerWidth * window.innerHeight / 16000));
            motes = [];
            for (var i = 0; i < count; i++) {
                motes.push({
                    x: Math.random() * window.innerWidth,
                    y: Math.random() * window.innerHeight,
                    r: 0.6 + Math.random() * 1.8,
                    vx: 4 + Math.random() * 10,          // pixels a second, drifting right like the camera's light
                    vy: -3 + Math.random() * 6,
                    a: 0.12 + Math.random() * 0.4,
                    t: Math.random() * Math.PI * 2
                });
            }
        }

        function frame(now) {
            if (!running) return;
            var dt = Math.min(0.05, (now - last) / 1000 || 0);
            last = now;
            var w = window.innerWidth, h = window.innerHeight;
            context.clearRect(0, 0, w, h);
            for (var i = 0; i < motes.length; i++) {
                var m = motes[i];
                m.t += dt * 0.6;
                m.x += (m.vx + Math.sin(m.t) * 3) * dt;
                m.y += (m.vy + Math.cos(m.t * 0.8) * 2) * dt;
                if (m.x > w + 10) m.x = -10;
                if (m.y < -10) m.y = h + 10;
                if (m.y > h + 10) m.y = -10;
                context.beginPath();
                context.fillStyle = "rgba(255, 214, 160, " + (m.a * (0.75 + Math.sin(m.t * 1.7) * 0.25)).toFixed(3) + ")";
                context.arc(m.x, m.y, m.r, 0, Math.PI * 2);
                context.fill();
            }
            window.requestAnimationFrame(frame);
        }

        function start() {
            if (!context || running || !motion || document.hidden) return;
            running = true;
            last = performance.now();
            window.requestAnimationFrame(frame);
        }

        function stop() {
            running = false;
            if (context) context.clearRect(0, 0, canvas.width, canvas.height);
        }

        if (context) {
            size();
            seed();
            window.addEventListener("resize", function () { size(); seed(); });
            // No drawing while the tab is in the background
            document.addEventListener("visibilitychange", function () { if (document.hidden) stop(); else start(); });
        }

        return {
            start: start,
            toggle: function () { if (motion) start(); else stop(); }
        };
    })();

    // Search in the middle of the screen: the header's icon, Ctrl+K or / opens it; Escape or a click outside closes it
    (function () {
        var dialog = document.getElementById("search-dlg");
        var box = document.getElementById("palette-input");
        if (!dialog || !box || !dialog.showModal) return;
        function open() {
            if (!dialog.open) dialog.showModal();
            box.focus();
            box.select();
        }
        var button = document.getElementById("search-open");
        if (button) button.addEventListener("click", open);
        dialog.addEventListener("click", function (event) { if (event.target === dialog) dialog.close(); });
        document.addEventListener("keydown", function (event) {
            var typing = /^(INPUT|TEXTAREA|SELECT)$/.test((event.target && event.target.tagName) || "");
            if ((event.key === "k" && (event.ctrlKey || event.metaKey)) || (event.key === "/" && !typing)) {
                event.preventDefault();
                open();
            }
        });
        box.addEventListener("keydown", function (event) {
            // A search box's first Escape only clears its text: here it closes the pop-up straight away
            if (event.key === "Escape") { event.preventDefault(); dialog.close(); return; }
            // Up and Down pick a result (the first is picked as results arrive); Enter opens the picked one
            var hits = [].slice.call(document.querySelectorAll("#search-results .search-hit"));
            var at = hits.findIndex(function (h) { return h.classList.contains("on"); });
            if (event.key === "ArrowDown" || event.key === "ArrowUp") {
                if (!hits.length) return;
                event.preventDefault();
                at = event.key === "ArrowDown" ? Math.min(hits.length - 1, at + 1) : Math.max(0, at - 1);
                hits.forEach(function (h, i) { h.classList.toggle("on", i === at); });
                hits[at].scrollIntoView({ block: "nearest" });
            }
            if (event.key === "Enter") {
                var pick = hits[at >= 0 ? at : 0];
                if (pick) { event.preventDefault(); location.href = pick.getAttribute("href"); }
            }
        });
        document.addEventListener("htmx:afterSwap", function (event) {
            if (event.target.id !== "search-results") return;
            var first = event.target.querySelector(".search-hit");
            if (first) first.classList.add("on");
        });
    })();

    // The Site look's colour pickers fill the text box next to them (empty text = the default colour)
    document.querySelectorAll("[data-colour-for]").forEach(function (picker) {
        var text = picker.form && picker.form.elements[picker.dataset.colourFor];
        if (!text) return;
        picker.addEventListener("input", function () { text.value = picker.value; });
        text.addEventListener("input", function () { if (/^#[0-9a-fA-F]{6}$/.test(text.value)) picker.value = text.value; });
    });

    // Appearance: filter the map pictures by name; a missing map's chip fills the upload form's map name
    (function () {
        var filter = document.querySelector("[data-map-filter]");
        if (filter) filter.addEventListener("input", function () {
            var q = filter.value.trim().toLowerCase();
            document.querySelectorAll(".map-thumb").forEach(function (t) { t.hidden = q !== "" && t.dataset.name.indexOf(q) < 0; });
        });
        // A slider that shows its value as a percentage beside it (Site settings: Darken the background)
        document.querySelectorAll("input[type='range'][data-percent]").forEach(function (range) {
            var out = document.getElementById(range.dataset.percent);
            if (out) range.addEventListener("input", function () { out.textContent = range.value + (range.dataset.unit || "%"); });
        });
        document.querySelectorAll("[data-fill-map]").forEach(function (chip) {
            chip.addEventListener("click", function () {
                var box = document.querySelector("input[name='map']");
                if (box) { box.value = chip.dataset.fillMap; box.focus(); }
            });
        });
    })();

    // A select that applies itself (the footer's language)
    document.addEventListener("change", function (event) {
        if (event.target.matches && event.target.matches("select[data-autosubmit]") && event.target.form) event.target.form.submit();
    });

    // ---------- The bell's menu: opening marks the notices read, a click elsewhere closes it, the refresh waits while open ----------

    document.addEventListener("toggle", function (event) {
        var menu = event.target;
        if (!menu.matches || !menu.matches("details[data-bell]") || !menu.open) return;
        var meta = document.querySelector('meta[name="request-token"]');
        fetch("/bell?handler=Read", { method: "POST", headers: { "RequestVerificationToken": meta ? meta.content : "" } }).catch(function () { });
        var badge = menu.querySelector(".bell-count");
        if (badge) badge.remove();
        menu.querySelector("summary").classList.remove("on");
    }, true);
    document.addEventListener("click", function (event) {
        document.querySelectorAll("details[data-bell][open]").forEach(function (menu) {
            if (!menu.contains(event.target)) menu.open = false;
        });
    });
    document.addEventListener("htmx:beforeRequest", function (event) {
        var elt = event.detail.elt;
        if (elt && elt.classList && elt.classList.contains("bell-slot") && elt.querySelector("details[data-bell][open]")) event.preventDefault();
    });

    // Esc closes an open header menu (bell, account, server switcher) and puts the focus back on its button
    document.addEventListener("keydown", function (event) {
        if (event.key !== "Escape") return;
        document.querySelectorAll("header details[open]").forEach(function (menu) {
            menu.open = false;
            var summary = menu.querySelector("summary");
            if (summary) summary.focus();
        });
    });

    // ---------- "All" beside a group of checkboxes (Tasks' servers): ticks them all, or clears them when all are ticked ----------

    document.addEventListener("click", function (event) {
        var button = event.target.closest("[data-check-all]");
        if (!button) return;
        var form = button.closest("form");
        var boxes = form ? Array.prototype.slice.call(form.querySelectorAll('input[name="' + button.dataset.checkAll + '"]')) : [];
        var all = boxes.every(function (box) { return box.checked; });
        boxes.forEach(function (box) { box.checked = !all; });
    });

    // ---------- The dashboard's server carousel: arrows page through the chips; the 10-second refresh keeps the place ----------

    var stripAt = 0;
    function strip() { return document.querySelector("[data-carousel] .strip"); }
    var CHIP_MIN = 190, CHIP_GAP = 0;   // Tabs sit edge to edge (site.css)
    // Whether the chips overflow, and if so a width that fits a whole number of them in the row (none cut off at the edge)
    function carousel() {
        var box = document.querySelector("[data-carousel]"), row = strip();
        if (!box || !row) return;
        var chips = Array.prototype.filter.call(row.querySelectorAll(".chip-server"), function (c) { return !c.hidden; });
        // Room with the arrows hidden decides whether they're needed at all
        var room = box.clientWidth;
        var more = chips.length * 210 + (chips.length - 1) * CHIP_GAP > room + 1;
        box.classList.toggle("overflow", more);
        if (more) {
            var width = row.clientWidth;
            var fit = Math.max(1, Math.floor((width + CHIP_GAP) / (CHIP_MIN + CHIP_GAP)));
            row.style.setProperty("--chip-w", ((width - (fit - 1) * CHIP_GAP) / fit) + "px");
        } else {
            row.style.removeProperty("--chip-w");
        }
        carouselState();
    }
    function carouselState() {
        var box = document.querySelector("[data-carousel]"), row = strip();
        if (!box || !row) return;
        // Greyed at the first and last server
        var chips = visibleChips(row);
        var at = chips.findIndex(function (c) { return c.classList.contains("on"); });
        box.querySelector(".prev").disabled = at <= 0;
        box.querySelector(".next").disabled = at < 0 || at >= chips.length - 1;
    }
    // The arrows switch to the previous or next server, like moving between browser tabs; the row scrolls along when the
    // new tab is off the page, so the tab picked is always the server shown below it
    document.addEventListener("click", function (event) {
        var button = event.target.closest("[data-car]");
        var row = strip();
        if (!button || !row) return;
        var chips = visibleChips(row);
        var at = chips.findIndex(function (c) { return c.classList.contains("on"); });
        var next = chips[Math.min(chips.length - 1, Math.max(0, at + Number(button.dataset.car)))];
        if (next && !next.classList.contains("on")) {
            next.click();
            showChip(next, true);
        }
        carouselState();
    });
    function visibleChips(row) {
        return Array.prototype.filter.call(row.querySelectorAll(".chip-server"), function (c) { return !c.hidden; });
    }
    // Scroll the row to the page holding a tab (the picked one, after a resize or a switch on a phone)
    function showChip(chip, smooth) {
        var row = strip();
        if (!row || !chip) return;
        var chips = Array.prototype.filter.call(row.querySelectorAll(".chip-server"), function (c) { return !c.hidden; });
        var index = chips.indexOf(chip);
        if (index < 0) return;
        var step = chip.offsetWidth + CHIP_GAP;
        var perPage = Math.max(1, Math.round((row.clientWidth + CHIP_GAP) / step));
        var left = Math.floor(index / perPage) * perPage * step;
        if (row.scrollLeft > chip.offsetLeft - chips[0].offsetLeft || row.scrollLeft + row.clientWidth < chip.offsetLeft - chips[0].offsetLeft + chip.offsetWidth)
            row.scrollTo({ left: left, behavior: smooth ? "smooth" : "auto" });
    }
    document.addEventListener("scroll", function (event) {
        if (event.target.classList && event.target.classList.contains("strip")) { stripAt = event.target.scrollLeft; carouselState(); }
    }, true);
    document.addEventListener("htmx:beforeSwap", function (event) {
        if (event.target.id === "servers" && strip()) stripAt = strip().scrollLeft;
    });
    window.addEventListener("resize", function () {
        carousel();
        var row = strip();
        if (row) showChip(row.querySelector(".chip-server.on"), false);
    });

    // List rows' Actions menus: open (just the buttons) when wide, folded when narrow, matching the 900px rule in site.css
    var narrow = window.matchMedia("(max-width: 900px)");
    function foldActions() {
        document.querySelectorAll("details.acts-menu:not(.single)").forEach(function (menu) { menu.open = !narrow.matches; });
    }
    if (narrow.addEventListener) narrow.addEventListener("change", foldActions);
    document.addEventListener("htmx:afterSettle", foldActions);

    // Toasts leave once they've slid out (so the ones after them move up), and a tap closes one early
    (function () {
        var toasts = document.getElementById("toasts");
        if (!toasts) return;
        toasts.addEventListener("animationend", function (event) {
            if (event.animationName === "toast-out" && event.target.parentNode === toasts) toasts.removeChild(event.target);
        });
        toasts.addEventListener("click", function (event) {
            var toast = event.target.closest && event.target.closest(".toast");
            if (toast && !(event.target.closest("a, button"))) toast.classList.add("dismiss");
        });
    })();

    // ---------- Start, and after every refresh ----------

    document.addEventListener("htmx:afterSwap", function (event) {
        if (event.target.id === "servers" && strip()) strip().scrollLeft = stripAt;
        carousel();
        var toasts = document.getElementById("toasts");
        if (toasts && event.target === toasts) {
            while (toasts.children.length > 4) toasts.removeChild(toasts.firstElementChild);
        }
        if (document.getElementById("server-body")) showTab(load(tabKey()) || "");
        apply();   // The map, or the picked server, may have changed, which "Auto" follows
        showArt();
        filterCards();
        carousel();
    });

    // After htmx has set up the page (its DOMContentLoaded handler was added first), so a list's load-list is heard
    // A link to a section (#reports from the bell) opens it; else the one picked last time
    function startTab() {
        var wanted = location.hash.slice(1);
        var body = tabBody();
        if (wanted && body && body.querySelector('[data-tab="' + CSS.escape(wanted) + '"]')) { save(tabKey(), wanted); return wanted; }
        return load(tabKey()) || "";
    }
    document.addEventListener("DOMContentLoaded", function () { showTab(startTab()); });
    // A #section link on the same page (the bell, a note) switches the section without a reload
    window.addEventListener("hashchange", function () { var t = startTab(); if (t) showTab(t); });
    showArt();
    carousel();
    (function () {   // Start the carousel at the page with the picked server (after the chips are sized)
        var row = strip(), on = row && row.querySelector(".chip-server.on");
        if (!on) return;
        var first = row.querySelector(".chip-server:not([hidden])");
        var step = on.offsetWidth + CHIP_GAP;
        var at = on.offsetLeft - first.offsetLeft;
        if (at + on.offsetWidth > row.clientWidth) {
            var perPage = Math.max(1, Math.round((row.clientWidth + CHIP_GAP) / step));
            row.scrollLeft = stripAt = Math.floor(Math.round(at / step) / perPage) * perPage * step;
        }
        carouselState();
    })();
    // Graph marks: a faint line and a label at each midnight (7 days) or every 6 hours (24 hours), in this viewer's own
    // time zone and language. Graph.cs leaves the times on the wrapper; positions are set here (the CSP allows that)
    function graphMarks() {
        var lang = document.documentElement.lang || "en";
        document.querySelectorAll(".graph-wrap[data-from]").forEach(function (wrap) {
            if (wrap.dataset.marked) return;
            wrap.dataset.marked = "1";
            var from = +wrap.dataset.from, to = +wrap.dataset.to, days = wrap.dataset.marks === "days";
            var axis = wrap.querySelector(".graph-axis");
            var at = new Date(from);
            at.setMinutes(0, 0, 0);
            if (days) at.setHours(24); else at.setHours(Math.ceil((at.getHours() + 1) / 6) * 6);
            for (; at.getTime() < to; days ? at.setDate(at.getDate() + 1) : at.setHours(at.getHours() + 6)) {
                var x = (at.getTime() - from) / (to - from) * 100;
                if (x < 2 || x > 98) continue;
                var mark = document.createElement("span");
                mark.className = "graph-mark";
                mark.style.left = x + "%";
                wrap.insertBefore(mark, axis);
                var text = document.createElement("span");
                // A day's name sits in the middle of its day (midnight + 12 h), an hour's on its line
                var labelX = days ? (at.getTime() + 43200000 - from) / (to - from) * 100 : x;
                if (labelX > 97) continue;
                text.style.left = labelX + "%";
                try {
                    text.textContent = days ? at.toLocaleDateString(lang, { weekday: "short" }) : at.toLocaleTimeString(lang, { hour: "2-digit", minute: "2-digit" });
                } catch (e) { text.textContent = days ? at.toDateString().slice(0, 3) : at.getHours() + ":00"; }
                axis.appendChild(text);
            }
        });
    }
    // An act form's button names the action picked in its "pick" list ("Ban player", "Close only"), and so does its
    // confirm pop-up ("Ban player?"), instead of a generic "Do it"
    function actLabels() {
        document.querySelectorAll("button[data-act-from]").forEach(function (button) {
            var pick = button.form && button.form.querySelector("select[name='" + button.dataset.actFrom + "']");
            if (!pick || pick.dataset.actBound) return;
            pick.dataset.actBound = "1";
            var name = function () {
                var option = pick.options[pick.selectedIndex];
                var text = option ? option.text.trim() : "";
                if (!text) return;
                button.textContent = text;
                button.dataset.confirmTitle = text + "?";
                button.dataset.confirmYes = text;
                // Only the fields the choice uses: "Close only" needs no length or reason, a kick no length
                if (option.hasAttribute("data-fields")) {
                    var wanted = option.dataset.fields.split(" ");
                    button.form.querySelectorAll("[data-field]").forEach(function (field) {
                        field.hidden = wanted.indexOf(field.dataset.field) < 0;
                    });
                }
                // Closing without an action isn't a dangerous step: a plain button and confirm
                var calm = option.dataset.calm === "true";
                button.classList.toggle("danger", !calm);
                button.classList.toggle("primary", calm);
                button.dataset.confirmDanger = calm ? "false" : "true";
            };
            pick.addEventListener("change", name);
            name();
        });
    }
    document.addEventListener("htmx:afterSettle", actLabels);
    actLabels();
    document.addEventListener("htmx:afterSettle", graphMarks);
    graphMarks();

    // Country names in the page's language ("DE" -> "Germany", "Deutschland" ...): the browser knows them all
    var regionNamer = null;
    try { regionNamer = new Intl.DisplayNames([document.documentElement.lang || "en"], { type: "region" }); } catch (e) { }
    function regionNames() {
        if (!regionNamer) return;
        document.querySelectorAll("[data-region]:not([data-named])").forEach(function (el) {
            el.dataset.named = "1";
            try {
                var name = regionNamer.of(el.dataset.region) || el.dataset.region;
                if (el.tagName === "IMG") { el.title = name; el.alt = name; } else el.textContent = name;   // A flag: its name on hover
            } catch (e) { }
        });
        document.querySelectorAll(".worldmap use.hot title:not([data-named])").forEach(function (title) {
            title.setAttribute("data-named", "1");
            var parts = title.textContent.split(":");
            try { title.textContent = (regionNamer.of(parts[0]) || parts[0]) + ":" + parts.slice(1).join(":"); } catch (e) { }
        });
    }
    document.addEventListener("htmx:afterSettle", regionNames);
    regionNames();
    foldActions();
    apply();
    renderOptions();
    dust.start();
})();
