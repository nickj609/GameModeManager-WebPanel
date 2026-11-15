# Agent protocol, version 1

How GameModeManager's agent and the panel talk. GameModeManager opens the link; the panel never connects to a game
server.

## Connecting

`wss://<agent hostname>/agent`, with these headers:

| Header | Value |
| --- | --- |
| `CF-Access-Client-Id`, `CF-Access-Client-Secret` | The Cloudflare Access service token (checked by Cloudflare) |
| `Authorization` | `Bearer <server token>` (checked by the panel; made with `gmmpanel token new <server>`) |

The panel answers:

| Status | Meaning | Agent should |
| --- | --- | --- |
| 101 | Accepted | Send `hello` within 10 seconds |
| 401 | Unknown or missing server token | Log once and retry slowly (the token is wrong; retrying fast won't help) |
| 409 | This server already has a live link | Retry with backoff; the old link times out within 75 s |
| 429 | Too many connects from this address (12 a minute) | Back off |

Cloudflare closes idle WebSockets after about 100 seconds. Both sides ping every 30 s; the panel drops a link that
hasn't answered for 75 s. Reconnect with backoff: 1 s, doubling to 60 s, with jitter.

## Messages

JSON text frames, camelCase, each with a `type`. Up to 1 MB. Unknown types are ignored, so a newer side can add
messages without breaking an older one; anything that changes a meaning raises the protocol number.

| Direction | `type` | Fields |
| --- | --- | --- |
| agent → panel | `hello` | `protocol` (1), `server` (display only; the token decides which server it is), `gmmVersion`, `cssVersion` |
| panel → agent | `welcome` | `protocol`, `server` (the name the panel knows it by) |
| panel → agent | `error` | `message`, then the panel closes. For example a protocol mismatch, with which side to update |
| agent → panel | `status` | `status`: the whole snapshot, below. Send on connect, after every command, and every 30 s |
| agent → panel | `event` | `name`, `text` (for people, in English), `at` (ISO 8601) |
| agent → panel | `chat` | `steamId`, `name`, `team` (CT, T, Spectator), `text` (up to 256), `teamOnly`, `at`: a human player's say or say_team line, as it happens. The panel keeps it for the chat log (30 days) |
| panel → agent | `command` | `id`, `name`, `args` (strings), `adminSteamId`, `adminName` |
| agent → panel | `result` | `id`, `ok`, `message` (the same reason a player would get, in English), `followUp` (below, or null), `output` (`console` only), `data` (`query` only: a page, below) |

The panel waits 15 s for a `result`. Send the `result` first, then the new `status`.

### Follow-ups

After a setting change GameModeManager's menus ask a question; the agent returns it as `followUp` and the panel asks it
in the same words, then sends the chosen command.

| `kind` | Asked | Choices the panel offers |
| --- | --- | --- |
| `restart` | "Changes need a match restart" | Restart now (`restart_match`), Restart later |
| `round` | "Changes apply next round" | Replay round (`replay_round`, only if `canReplay`), End round (`end_round`, only if `canEndRound`), Restart match, Next round |

Fields: `kind`, `settings` (the changed settings' names), `canReplay`, `canEndRound`.

## Status

[samples/status.json](samples/status.json) is a full example; `tools/FakeAgent` sends it.

| Field | Contents |
| --- | --- |
| `gmmVersion`, `cssVersion`, `players`, `maxPlayers` | |
| `mode`, `map` | `{ name, displayName }` |
| `nextMap` | As players see it, or null |
| `voteInProgress` | A GameModeManager or GG1MapChooser vote |
| `votes` | `{ canStart, unavailable }`: whether an admin can start a vote now, and why not |
| `modes` | `[{ name, displayName, maps: [map names] }]` |
| `maps` | `[{ name, displayName }]` |
| `settings` | `[{ name, displayName, state: On / Off / Mixed / Unknown, applies: now / round / restart / map, cheats, group }]`. `cheats`: turning it on leaves sv_cheats on; `group`: the cfg's `// group:` (its menu page), "" for none |
| `valueSettings` | `[{ name, displayName, value (or null), choices: [labels], cheatChoices: [labels], applies, group }]` |
| `actions` | `[{ name, displayName, flag, group }]`. `group` is the cfg's `// group:` (or its subfolder), `""` for none; the panel shows actions under their group |
| `warmup` | `{ enabled, scheduled, running, mode, modes: [warmup mode names], unavailable }`. `modes` are the warmup modes, not the game modes; `unavailable` says why the panel can't start one (MatchZy loaded, Arms Race) |
| `rotation` | `{ maps, modes, interval, intervalChoices, modeVote: random / vote, countSkipped, rtv, whenEmpty, emptyMinutes, emptyChoices, schedules, mapsCounted, modeChangeDue }` |
| `rounds` | `{ enabled, pause: Running / Pending / Paused, pauseUnavailable, canTimeout, timeoutsByMatchZy, matchZyLive, frozen, canEndRound, currentRound, backups: [round numbers, newest first] }`. `enabled` is `Actions.Rounds` |
| `matchZy` | `{ live, practice, veto, readyUp, paused, knifeRound, playout, whitelist }`, or null when MatchZy isn't loaded |
| `console` | `{ available, reason }`: whether the agent can reach the server's own RCON on 127.0.0.1 |
| `integrations` | `[{ name, found, detail }]` |
| `server` | `{ hostname, address, port, teamCt, teamT }`. `address` is the public `ip:port` for `steam://connect` (GameModeManager's `Panel.PublicAddress`, else `net_public_adr`, else a public `ip`), or null; then the panel uses the public address the agent connected from with `port` (`hostport`). `teamCt`/`teamT` are `mp_teamname_1`/`_2` |
| `plugins` | `[{ name, module, version, author, description, state, protected }]`, or null when CounterStrikeSharp's plugin list can't be read. `name` is the `plugins/` folder; `state` is `loaded`, `unloaded`, `loading`, `notloaded` (a folder never loaded) or `disabled` (in `plugins/disabled`, which CounterStrikeSharp skips at start); `protected` is GameModeManager itself |
| `roster` | `[{ steamId, name, team, score, kills, deaths, ping, country }]`: the human players on now, with the scoreboard (the panel's Who's on shows it like the in-game Tab board; older agents send no scores). `country` is the ISO 3166-1 alpha-2 code the agent looked up from the player's IP in DB-IP's free country database (`Panel.Countries`, on by default), or null; the IP itself is never sent. The panel keeps each player's last country for "Player countries". The panel keeps player history and "who's on" from it, and samples `players` every 5 minutes for its graphs |
| `data` | `[{ id, name, available, reason, lists: [{ id, label, search, filters, flag, actions: [{ id, label, row, flag, fields, when }] }] }]`. `flag` is the admin flag reading the list or running the action needs: the plugin's own command flag (SimpleAdmin `css_addban` @css/ban, `css_unban` @css/unban, mutes @css/chat, warnings @css/kick, admins and groups @css/root): CS2-SimpleAdmin, whose database GameModeManager reads (id `simpleadmin`). Always first: `server` ("This server"), read on the game thread with no database: `players` (live: name, SteamID, team, score, kills, deaths, ping; row action `kick` runs `kickid`), `admins` and `groups` (CounterStrikeSharp's AdminManager, as `css_admins_list`: admins.json, admin_groups.json and CS2-SimpleAdmin's admins; read only). The panel applies its own Permissions flags to `server`'s lists. `fields` are form fields (`steamid`, `minutes`, `reason`, `group`, `amount`, `name`, `flags`, `immunity`); a `row` action takes its fields from the row (its SteamID, a group's `group`) and shows only on rows matching `when` |

Fields added after the first release (`server`, `plugins`, `data`) may be missing from an older GameModeManager; the panel then says to update it.

### Data pages

`query`'s `data`: `{ columns: [{ key, label, kind }], rows: [{ key: text }], page, pageSize, total }`. `kind` is `text`,
`steamid`, `time`, `status` (shown as a status dot) or `number`. Values are already text for people. A row may carry keys that aren't columns: CS2-SimpleAdmin's bans have `country`, the code GameModeManager looked up from the ban's stored IP (the IP itself is never sent), shown as a flag after the name.

GameModeManager is the only database client: the panel holds no database credentials and opens no database port. It
reads each plugin's own settings (its config, or SimpleAdmin's API), runs fixed parameterized SELECTs from
`Integrations/<Plugin>/<Plugin>Data.cs` inside a `READ ONLY` transaction, 25 rows a page, and never builds SQL from what
the panel sends (a list id picks a query, a search is a parameter).

## Commands

Everything GameModeManager's in-game admin menus do. The panel only sends these, and only with values from the last
status; the agent checks them again.

| `name` | `args` | GameModeManager does |
| --- | --- | --- |
| `change_mode` | `mode` | Changes mode (and map) now |
| `set_next_mode` | `mode` | `SetNextMode` |
| `random_mode` | | The Modes page's Random mode |
| `change_map` | `map` | `ChangeMap` with the usual delay |
| `set_next_map` | `map` | `SetNextMap` (refused while GG1MapChooser's vote runs) |
| `random_map` | | The Maps page's Random map, from the current mode |
| `start_vote` | `kind`: `random`, or `map` + `map`, or `mode` + `mode` | As the in-game Start vote pages: GG1MapChooser's random map vote, or a yes/no vote on one map or mode. GG1's random mode vote needs a player in game, so it isn't offered |
| `set_setting` | `setting`, `on`, `confirm` (`true` when it turns on sv_cheats) | `SetSetting`; result carries `followUp` |
| `set_value` | `setting`, `value`, `confirm` (for a `cheatChoices` value) | `SetValue`; result carries `followUp` |
| `run_action` | `action` | `RunAction` |
| `start_warmup` | `mode` (a warmup mode) | Start warmup now (refused while MatchZy is loaded, or in Arms Race) |
| `schedule_warmup` | `mode` | `ScheduleWarmup` |
| `end_warmup` | | `EndWarmup` |
| `pause` / `cancel_pause` / `unpause` | | The Rounds page's pause (through MatchZy during a live match) |
| `timeout` | `team`: `ct` / `t` | CT / T timeout (refused during a live MatchZy match: players' .tac) |
| `replay_round` / `replay_last` | | Replay this round / Replay last round: the newest and second-newest backup |
| `restore_round` | `round` | `RestoreRound` |
| `freeze` | `on` | Freeze players |
| `end_round` | `settings` | The follow-up's End round: ends the round as a draw and names the settings in chat |
| `restart_match` | `settings` | The follow-up's Restart now / Restart match, naming the settings in chat |
| `set_rotation` | `key`, `value`, optional `save` | The Rotation page and Tasks. Lasts until the server restarts; with `save=true` GameModeManager also writes it into GameModeManager.json (the panel asks for `@css/config` then; the file from before the first save is kept as `GameModeManager.json.before-panel.bak`). Keys: `maps`, `modes`, `countSkipped`, `rtv`, `whenEmpty` (`true`/`false`), `interval`, `emptyMinutes` (a listed choice), `modeVote` (`random`/`vote`) |
| `matchzy` | `action`: `start`, `restart`, `end`, `skipveto`, `knife`, `playout`, `whitelist`, `practice`, `exitpractice`, `pause`, `unpause` | MatchZy's console commands, as the !matchzy page runs them (`knife`, `playout`, `whitelist` toggle). Player-bound ones (ready, coach, bots, smokes, spawns) aren't offered |
| `console` | `line` | **@css/rcon.** Sent to the server's own RCON on 127.0.0.1; the reply comes back as `output` (cap it at 64 KB). Both sides refuse lines that show or change `rcon_password` or `sv_password` |
| `set_hostname` | `value` | `hostname "<value>"`: 1–64 characters. Lasts until the server restarts or a cfg sets it; no cfg is written |
| `set_teamname` | `team`: `ct` / `t`, `value` | `mp_teamname_1` / `mp_teamname_2`: up to 32 characters, empty for the default |
| `plugin` | `action`: `load` / `unload` / `reload`, `plugin` (a `plugins` name) | **@css/root.** `css_plugins load <folder>` (a `disabled` one by its path, for this run only: it stays in `disabled`), or `unload` / `reload` by the plugin's id. Refused for GameModeManager itself |
| `whois` | `steamid` | The admin flags and groups CounterStrikeSharp's AdminManager has for that SteamID on this server (CS2-SimpleAdmin's admins, admins.json, admin_groups.json, group flags merged), as `data` with one row: `flags`, `groups`, `immunity` (space-separated). The panel asks it for the person signed in, at most every 2 minutes per server, and gives them what those flags give in game. Not logged |
| `query` | `source`, `list`, `page` (from 1), `search` (name or SteamID64, up to 64), `filter` (one of the list's `filters`) | A page of the list as `data`. Read only; not in the audit log unless refused |
| `moderate` | `source`, `list`, `action`, and the action's `fields` | The plugin's own console command: SimpleAdmin `css_addban` / `css_unban` / `css_addmute` / `css_addgag` / `css_addsilence` / `css_unmute` / `css_ungag` / `css_unsilence`, `css_warn #<userid>` (warn: only while the player is on that server, else refused), and (@css/root) `css_addadmin` / `css_deladmin` / `css_addgroup` / `css_delgroup`; and `server`'s `kick` (`kickid <userid>`). each action's `flag` is checked against the admin's flags on that server |

Text that goes into a console line (`value`, `reason`, `group`, `name`, `search`; `flags` takes only letters, digits, `@ # / _ , -` and spaces) is refused by both sides when it has quotes,
semicolons, backslashes or control characters; a SteamID64 is 17 digits starting `7656119`.

Players see the usual chat lines. GameModeManager's log adds `[Panel] <adminName> (<adminSteamId>)`.

## Console notes for the agent

CounterStrikeSharp can't read the server console's output, so the agent opens a TCP connection to the server's own RCON
(the `ip` ConVar's address, else 127.0.0.1, on `hostport`, with `rcon_password`) for each `console` command, runs it and returns the reply.
Nothing is exposed: the RCON port is only reached from inside the game server. When RCON isn't enabled (`-usercon` and a
password), `console.available` is false with the reason, and the panel shows it.

## Who may do what

The flags below are the defaults. Panel admins can change the flag for each panel area in Site settings (Permissions,
stored as `Permissions` in `panel.json`; `src/GmmPanel/Auth/PanelPermissions.cs` lists the areas): maps, warmup, rotation,
hostname and team names, plugins, console, MatchZy, live players, kick, chat log, player history, appeals, reports and
server admins. Settings, actions and the plugins' own lists keep their own flags.

The panel's own list (`panel.json`) holds only **panel admins**, made by the setup wizard, Site settings (Panel admins) or
`gmmpanel admin add`: they can do everything, even while the servers are offline. Everyone else signs in with Steam as a
player, and on each server gets what their admin flags there allow (`whois`), the same flags GameModeManager's in-game
pages and the plugins' commands ask for:

| Panel | Needs |
| --- | --- |
| Manage: Maps, Modes, votes | `@css/changemap` |
| Manage: Settings, Actions | each row's own flag (`settings[].flag`, `valueSettings[].flag`, `actions[].flag`; a cfg's `// permission:`, else `@css/cvar`) |
| Manage: Warmup, Rotation; Admin: hostname, team names | `@css/cvar` |
| Manage: Rounds (pause, timeouts, replay, freeze) | `rounds.flag` (`Actions.RoundsPermission`, default `@css/generic`) |
| Manage: MatchZy | `@css/config` |
| Admin: Console | `@css/rcon` |
| Admin: load, unload, reload plugins | `@css/root` |
| Moderate | each list's and action's `flag` |

A flag's domain root counts for all of it (`@css/root` covers every `@css/` flag), as in CounterStrikeSharp. Status items
`settings[].flag`, `valueSettings[].flag`, `actions[].flag` and `rounds.flag` were added with this; an older agent leaves
them out and the panel assumes `@css/cvar` (`@css/generic` for rounds).
