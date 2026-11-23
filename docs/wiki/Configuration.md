> [!NOTE]
> Almost everything is set in the panel itself, under **Site settings** (panel admins only). It's saved in `data/panel.json`; you don't need to edit that file. GameModeManager's own side of the panel is its `Panel` settings: see [GameModeManager's Configuration](https://github.com/nickj609/GameModeManager/wiki/Configuration#panel-settings).

# Site Settings
Site settings has one section at a time, picked in the menu on the left. A link like `/site#discord` opens that section.

| Group | Section | What it sets |
| --- | --- | --- |
| Look | **Site look** | The site's name, a logo (PNG, JPEG, WebP or SVG, up to 512 KB; see [SVG logos](#svg-logos)), an optional smaller logo for the footer, a favicon (the browser tab's icon: PNG, ICO or SVG; the default is a white crosshair), the line under it, the footer text, the colours (accent, notifications, header and footer, panel, page), a pattern, the corner rounding, the panel's and header's opacity and blur everyone starts with, the default language, and the background everyone starts with. |
| Look | **Social links** | Icons in the footer and at the top of the phone menu (Discord, YouTube, Twitch ...). They open in a new tab and use the accent colour unless you give one its own. |
| Look | **Menu links** | Up to 6 text links in the header, like Skins or VIP for other plugins' web panels, or a panel page like `/rules`. One or two show beside the pages; three or more, or a narrower window, go in one menu you name ("Community" by default). Each can open in a new tab and has an icon for the phone menu. |
| Pictures | **Backgrounds** | Upload pictures or short WebM/MP4 videos (up to 25 MB), give each a display name, and choose whether visitors see your own backgrounds too. |
| Pictures | **Map pictures** | The squares on the server cards: one picture per map (up to 2 MB), named after the map. |
| Features | **Features** | Switch off parts of the panel you don't want. See [Features](#features). |
| Features | **Rules** | Your server rules, shown on the Rules page and the report form. |
| Features | **Status widget** | An embeddable server card for your website, and `/status.json` for bots. |
| People | **Panel admins** | Who has full access. |
| People | **Permissions** | The admin flag each part of the panel needs. See [Permissions](https://github.com/nickj609/GameModeManager-WebPanel/wiki/Permissions). |
| Servers | **Servers** | Add and remove game servers, make a new token for one, see the `Panel` settings for its `GameModeManager.json`, and change the address game servers connect to. |
| Servers | **Discord** | A channel's webhook, and what to post there. See [Discord](#discord). |
| Data | **Retention** | How long records are kept. See [What the panel keeps](#what-the-panel-keeps). |
| Data | **Backup** | Download the settings and records as one zip, and restore it on a new machine. |

## SVG Logos
An SVG logo is drawn inside the page instead of as a picture, so it follows the theme, in the header and the footer:

- `fill="currentColor"` (or `stroke="currentColor"`) is the text colour: white in the dark theme, black in the light one.
- `class="accent"` fills a shape with the accent colour, and `class="accent-stroke"` outlines it with it, including each viewer's own accent from Appearance.
- Any other colour stays as it is.

```svg
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 26">
  <text x="0" y="22" font-family="Barlow Condensed" font-weight="700" font-size="26" fill="currentColor">EXAMPLE</text>
  <text x="92" y="22" font-family="Barlow Condensed" font-weight="700" font-size="26" class="accent">.gg</text>
</svg>
```

The panel keeps only the shapes and text: scripts, event handlers, styles, embedded pictures and links to other files are removed when you upload it. Text can use the panel's own fonts, Barlow and Barlow Condensed.

# Appearance
Each person can change the look for themselves on **Appearance** (in the account menu). It's kept in their browser, starts from the site look, and **Back to the panel's look** undoes it:

| Setting | |
| --- | --- |
| Theme | Dark or light. |
| Colour and Notifications | Their own accent and notification colours instead of the site's. |
| Panel opacity and Blur behind the panel | How see-through the panel is over the background. |
| Header and footer opacity | How see-through the header and footer are. |
| Darken the background | 0 to 100%. |
| Pattern on the panel and Pattern strength | None, dots, grid or diagonal lines. |
| Background and Motion | Picked with the button in the bottom-right corner. Motion starts off for anyone whose system asks for less motion. |

# Features
Turning a feature off hides its page, links and buttons. What's saved, like old reports or the rules, is kept.

| Feature | What it is |
| --- | --- |
| Public profiles | Each player's page (`/u/...`), linked from names everywhere. Players can still hide theirs. |
| Player reports | Signed-in players report others; staff answer them under Moderate. |
| Ban appeals | Banned players appeal on My profile; staff answer them under Moderate. Accepting an appeal unbans through CS2-SimpleAdmin. |
| Rules page | The rules from the Rules section, linked in the footer and on the report form. |
| Play-time leaderboards | Most played, This week and Newest on Stats. |
| Player countries | A world map and the top countries on each server's page and on Stats. GameModeManager sends each player's country, never their IP. |
| Status widget | The embeddable server card and `/status.json`. |

# Backgrounds
Behind the panel there's a scene, like CS2's main menu. Everyone sees the backgrounds, visitors included. Name a file after a map (`de_dust2.webm`) and **Auto** shows it while the selected server is on that map. Keep videos short and loop-friendly: every viewer's browser downloads them through the tunnel.

## CS2's map screenshots
The panel ships none of the game's art. `tools/GameArt` takes it from an installed CS2 (your PC, or the game server's own install) and writes it into the data folder:

- CS2's menu scenery (Ancient, Dust II, Mirage ...) as backgrounds, from each scene's full-HD map screenshot (`backgrounds/<map>.jpg`).
- Every official map's screenshot, cropped square, as its map picture (`maps/<map>.jpg`).

```bash
dotnet run --project tools/GameArt -- "C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive" <data folder>
```

Options: `--only backgrounds,maps`, and `--replace` to overwrite. Pictures already there are kept. Run it again after a CS2 update that adds maps. For Workshop maps, upload their pictures under **Site settings → Map pictures**.

# Discord
Paste a channel's webhook address (channel settings, Integrations, Webhooks) and choose what to post:

- Servers going offline (after 2 minutes) and back online.
- New ban appeals and player reports, and players' replies to them.
- Optionally, everything staff do through the panel.

Posts use your site name, accent colour and logo (PNG, JPEG or WebP; Discord can't show an SVG). A map change shows the
map's picture, a mode change the picture of the map it goes to (GameModeManager 2.4.1 or newer), and a server's posts have
**Connect** and **View server** buttons. Posts go out 2 seconds apart, and wait when Discord asks the panel to slow down.
CS2-SimpleAdmin posts its own bans and mutes with its own webhook settings.

> [!NOTE]
> Pictures, the logo and the buttons need the panel's public address. The panel learns it when a panel admin opens it
> from that address (like `https://panel.example.gg`), and follows a new domain the same way. Discord fetches pictures
> itself, so a panel only ever opened from `localhost` or a private address posts without them.

> [!CAUTION]
> A webhook address is a secret: anyone with it can post to your channel.

# What the panel keeps
Everything is in the `data` folder next to the program. Keep it private (`chmod 700` on Linux) and back it up.

| File or folder | What's in it | Kept (Retention) |
| --- | --- | --- |
| `panel.json` | Panel admins, server token hashes, Permissions, Site settings | Always |
| `keys/` | Sign-in cookie keys. Losing them only means signing in again. | Always |
| `audit.jsonl` | Every sign-in, refusal and command | 365 days |
| `history/<server>/counts-*.csv` | Player counts every 5 minutes, for the graphs | 400 days |
| `history/<server>/chat/` | The chat log | 30 days |
| `history/players.json` | Everyone seen: names, country, first and last seen, play time | Forever, or forgotten after a set time |
| `tickets.json` | Ban appeals and player reports, with staff's comments | 365 days after they're answered |
| `logs/` | The panel's own log, one file a day | 14 days |
| `discord.json` | The Discord webhook and what to post | Always |
| `users.json` | What people set about themselves (their Discord username, a hidden profile) | Always |
| `backgrounds/`, `maps/`, `branding/` | Your backgrounds, map pictures and logo | Always |

Visitors see only the graphs and who's on now (names, no SteamIDs). The **Privacy** page, linked in the footer, tells players what's kept about them, using your Retention settings.
