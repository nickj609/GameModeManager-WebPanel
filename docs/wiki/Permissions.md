> [!NOTE]
> The panel uses the same admin flags as the game, so staff get the same powers in the browser as in the in-game menus, and nothing more. Changes count on the next click; no restart is needed.

# Who Can Do What
| Who | How they get it | What they see |
| --- | --- | --- |
| **Visitors** | Not signed in | The servers, who's on, the graphs, Bans, Stats, Rules and public profiles (each one only if its [feature](https://github.com/nickj609/GameModeManager-WebPanel/wiki/Configuration#features) is on). |
| **Players** | Signed in through Steam, with no admin flags | Also My profile: their bans, mutes and warnings, and their own appeals and reports. |
| **VIPs** | `@css/vip` (or only `@css/reservation`) | The same as players, with a VIP tag. These flags are perks, not staff powers. |
| **Staff** | Any other admin flag on a server | Manage, Moderate and each player's page, limited to what their flags allow on that server. Reports, the chat log, who's on and player history show only the servers they have flags on. |
| **Panel admins** | Listed under **Site settings → Panel admins** | Everything, on every server, including Site settings and the audit log. |

Anyone who can read a list on Moderate can also export it (CSV or JSON, up to 2,000 rows); each export is in the audit log.

Admin flags come from each server: CounterStrikeSharp's `admins.json` and `admin_groups.json`, and CS2-SimpleAdmin's admins. GameModeManager tells the panel the flags of whoever signs in, and checks them again before it runs anything.

# Panel Permissions
**Site settings → Permissions** sets the flag each part of the panel needs. These are the defaults:

| Part | Default flag | Where |
| --- | --- | --- |
| Maps, modes and votes | `@css/changemap` | Manage: change, set next, random, start a vote |
| Warmup | `@css/cvar` | Manage: start, schedule and end warmups |
| Rotation | `@css/cvar` | Manage: map and mode rotation, RTV |
| Hostname and team names | `@css/cvar` | Admin: the Server section |
| Load, unload and reload plugins | `@css/root` | Admin: the Plugins section's buttons |
| Console | `@css/rcon` | Admin: the server's console |
| MatchZy | `@css/config` | Manage: MatchZy's commands |
| Live players | `@css/generic` | Moderate: who's on, with score and ping |
| Kick | `@css/kick` | Moderate: Kick on the live players list |
| Chat log | `@css/chat` | Moderate: what players said |
| Player history | `@css/generic` | Moderate: everyone seen, and each player's page |
| Ban appeals | `@css/unban` | Moderate: read, accept (unban) and reject appeals |
| Player reports | `@css/kick` | Moderate: read, claim and close reports, and kick the reported player |
| Server admins | `@css/generic` | Moderate: CounterStrikeSharp's admins and groups, read only |

# Set by the Server
Some flags aren't in Permissions, because the server decides them, the same as in game:

- **Settings and actions:** each cfg's `// permission:` header, else `@css/cvar`. See GameModeManager's [FAQ](https://github.com/nickj609/GameModeManager/wiki/FAQ-(Frequently-Asked-Questions)).
- **Rounds:** GameModeManager's rounds flag.
- **CS2-SimpleAdmin:** its own command flags. Ban `@css/ban`, unban `@css/unban`, mute and gag `@css/chat`, warn `@css/kick`, and admins and groups `@css/root`.
