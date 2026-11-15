<details>
<summary>My server shows Offline. What should I check first?</summary>
<br>

Work from the game server towards the panel:

1. **GameModeManager's log** (`/csgo/addons/counterstrikesharp/logs/`): lines starting `[Panel]` say whether it's connecting and why it can't.
2. **`Panel.Enabled`** is `true`, and **`Panel.Url`** is your panel's address with `wss://` in front and `/agent` at the end, like `wss://panel.example.com/agent`.
3. **`panel-secrets.json`** is next to `GameModeManager.json`, with the server's `Token` (and, if Cloudflare Access is in front of the address, the service token's `AccessClientId` and `AccessClientSecret`).
4. **cloudflared** is running next to the panel, and the tunnel's public hostname points at the panel.
5. **The token** is still the server's: making a new one under **Site settings → Servers** replaces the old one.

GameModeManager reads its config on a map change, so change map or restart the server after editing it.

</details>

<details>
<summary>Why does http://127.0.0.1:5080 say "400 Bad Request"?</summary>
<br>

On purpose. Outside Development the panel only answers through its https address, so sign-in cookies are never sent without encryption. Open `https://panel.<your domain>` instead. Only `/healthz` answers on 127.0.0.1, for health checks.

If you get it through your https address too, the panel doesn't trust the proxy in front of it: add the proxy's address to `Panel:TrustedProxies`. See [Other ways to host](https://github.com/nickj609/GameModeManager-WebPanel/wiki/Installation#other-ways-to-host).

</details>

<details>
<summary>A staff member can't see Manage or Moderate. Why?</summary>
<br>

The panel gives them what their admin flags give them **on the server picked in the header**. Check that:

- They have the flag on that server (`admins.json`, `admin_groups.json` or CS2-SimpleAdmin).
- The server is online, because GameModeManager is what tells the panel their flags.
- The flag matches **Site settings → Permissions** for that part.

`@css/vip` and `@css/reservation` are perks, not staff powers. See [Permissions](https://github.com/nickj609/GameModeManager-WebPanel/wiki/Permissions).

</details>

<details>
<summary>Why is the Connect button missing or wrong?</summary>
<br>

The panel uses the address GameModeManager reports. A hosted server is often behind an address the game can't see itself, so set `Panel.PublicAddress` to the `ip:port` players connect to. See GameModeManager's [Panel Settings](https://github.com/nickj609/GameModeManager/wiki/Configuration#panel-settings).

</details>

<details>
<summary>Why is the Console empty, or missing?</summary>
<br>

The console needs `Panel.Console` turned on in GameModeManager, and the server started with `-usercon` and an `rcon_password`. GameModeManager sends the commands through the server's own RCON on 127.0.0.1, so their output comes back to the panel; RCON is never opened to the internet. The Console also needs `@css/rcon` by default.

</details>

<details>
<summary>Why are Bans, Mutes and Warnings empty?</summary>
<br>

They come from [CS2-SimpleAdmin](https://github.com/daffyyyy/CS2-SimpleAdmin)'s database, which GameModeManager reads. Install CS2-SimpleAdmin on the server picked in the header and check that it's loaded. Bans and mutes go through CS2-SimpleAdmin's own commands, so its Discord posts and in-game messages work as usual.

</details>

<details>
<summary>Why can't I warn a player who's offline?</summary>
<br>

CS2-SimpleAdmin's `css_warn` only works on a player who is on the server. The panel offers **Warn** while they're on the server picked in the header. Bans, mutes and gags work offline, by SteamID.

</details>

<details>
<summary>Where do the backgrounds and map pictures come from?</summary>
<br>

The panel ships none of Counter-Strike 2's art. `tools/GameArt` takes CS2's menu scenery and map screenshots from an installed game into your data folder. You can also upload your own pictures and short videos under **Site settings → Backgrounds** and **Map pictures**. See [Backgrounds](https://github.com/nickj609/GameModeManager-WebPanel/wiki/Configuration#backgrounds).

</details>

<details>
<summary>Why do some players have no country flag?</summary>
<br>

GameModeManager looks each player's country up on the game server when they join, with the free [DB-IP Lite](https://db-ip.com) database, and sends only the country code. A player gets a flag the first time they join after the panel is set up, so a ban of someone who hasn't joined since has none. Players on a local network have none either. The panel never stores IP addresses for this. **Player countries** can be turned off under **Site settings → Features**.

</details>

<details>
<summary>How do I move the panel to another computer?</summary>
<br>

Download a backup under **Site settings → Backup**, set up the panel on the new computer, and restore it there. Point cloudflared's tunnel at the new computer. Game servers don't need any change, because their address and tokens stay the same.

</details>

<details>
<summary>Does it work with game hosts that only give game ports?</summary>
<br>

Yes. GameModeManager connects out to the panel through Cloudflare, like a browser does, so the game host opens no port and needs no extra service.

</details>

<details>
<summary>Can players hide their profile?</summary>
<br>

Yes, on **My profile**. A hidden profile only says that it's private. Staff still see everything on the player's staff page. The owner can turn public profiles off for everyone under **Site settings → Features**.

</details>
