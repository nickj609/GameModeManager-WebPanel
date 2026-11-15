> [!NOTE]
> Nothing on your home network or the game host opens a port. `cloudflared`, next to the panel, connects out to Cloudflare, and GameModeManager connects out to Cloudflare too. RCON is never reached from outside.

# Requirements
- [GameModeManager](https://github.com/nickj609/GameModeManager) **2.4.0 or newer** on each game server.
- A domain on [Cloudflare](https://www.cloudflare.com/) (the free plan is enough).
- A computer that's always on: a Windows PC (x64), or a Raspberry Pi 4 or 5 with a 64-bit OS (with or without Docker).

The panel is one self-contained program: no .NET to install, no database and no Node.

# 1. Cloudflare: one tunnel, two hostnames
In Cloudflare Zero Trust:

1. **Networks > Tunnels > Create a tunnel** (cloudflared). Keep the tunnel token for step 2.
2. Add two **public hostnames** to it, both pointing at the panel:

   | Hostname | Service (Windows / Pi without Docker) | Service (Docker) |
   | --- | --- | --- |
   | `panel.<your domain>` | `http://127.0.0.1:5080` | `http://panel:8080` |
   | `agent.<your domain>` | `http://127.0.0.1:5080` | `http://panel:8080` |

3. **Access > Service auth > Service tokens > Create**. Name it after the game server. Keep the Client ID and Secret for step 4; Cloudflare shows the secret once.
4. **Access > Applications > Add > Self-hosted** for `agent.<your domain>`, with one policy: action **Service Auth**, include **Service Token** = the token from step 3. Browsers can't reach this hostname at all.
5. Optional, recommended while you try it: a second Access application for `panel.<your domain>` with an **Allow** policy for your email (one-time PIN). Steam sign-in still applies behind it.

# 2. Run the panel
<details>
<summary>Windows</summary>

Unzip `gmmpanel-<version>-win-x64.zip` to a folder outside OneDrive or Dropbox (for example `C:\GmmPanel`), then run:

```bat
gmmpanel
```

Run cloudflared as a service with the tunnel token: `cloudflared service install <token>`.
</details>

<details>
<summary>Raspberry Pi with Docker (recommended for 24/7)</summary>

Copy `deploy/docker-compose.yml` and `deploy/.env.example` (as `.env`, with your tunnel token and hostnames) to a folder, then:

```bash
docker compose up -d
docker compose logs panel   # the setup code
```
</details>

<details>
<summary>Raspberry Pi without Docker</summary>

Unzip `gmmpanel-<version>-linux-arm64.zip` and install `deploy/gmmpanel.service` as a systemd unit. Run cloudflared as a service with the tunnel token.
</details>

The panel prints a **setup code**. Open `https://panel.<your domain>/setup` and follow the wizard: the code, your name, Steam sign-in (you become the first panel admin), your first server's token and the settings to paste, and optionally your site's name, logo, colour and background.

> [!TIP]
> Outside Development the panel only answers through the tunnel's https address: `http://127.0.0.1:5080` returns "400 Bad Request" on purpose (only `/healthz` answers there).

# 3. Add each game server
Add a server under **Site settings → Servers**, or from the command line:

```bash
gmmpanel token new Production
```

It shows the server's token once. The panel keeps only its hash; make a new one to replace a lost or leaked token.

# 4. Turn it on in GameModeManager
In `addons/counterstrikesharp/configs/plugins/GameModeManager/GameModeManager.json`:

```json
"Panel": {
  "Enabled": true,
  "Url": "wss://agent.<your domain>/agent",
  "ServerName": "Production",
  "SecretsFile": "panel-secrets.json"
}
```

Next to it, `panel-secrets.json`:

```json
{
  "Token": "<the server's token>",
  "AccessClientId": "<Cloudflare service token Client ID>",
  "AccessClientSecret": "<Cloudflare service token Client Secret>"
}
```

> [!CAUTION]
> **Never put `panel-secrets.json` in git.** Upload it once per server through your host's file manager.

Change map or restart the server: the panel shows it **Online** within a minute. The other `Panel` settings (`Console`, `PublicAddress`) are in GameModeManager's [Configuration](https://github.com/nickj609/GameModeManager/wiki/Configuration#panel-settings).

# Admins
Panel admins (everything, including Site settings and the audit log) are added under **Site settings → Panel admins**, or from the command line:

```bash
gmmpanel admin add 76561198000000000 admin Alex
gmmpanel admin remove 76561198000000000
gmmpanel admin list
```

Everyone else signs in with Steam and gets what their admin flags give them on each server. See [Permissions](https://github.com/nickj609/GameModeManager-WebPanel/wiki/Permissions).

Your SteamID64 is the 17-digit number in your Steam profile's address, or at [steamid.io](https://steamid.io).

# Updating
- **Docker:** `docker compose pull && docker compose up -d`.
- **Single file:** download the new zip and replace `gmmpanel` / `gmmpanel.exe`. The `data` folder stays.

New releases come out automatically when .NET ships a security fix.

# Trying it without a game server
`tools/FakeAgent` stands in for a game server:

```bash
dotnet run --project tools/FakeAgent -- ws://127.0.0.1:5080/agent <token> Production
```

`bash tools/demo.sh` starts six of them with sample data. In Development (`ASPNETCORE_ENVIRONMENT=Development` and `Panel__DevSignInAs=<SteamID64 in the admin list>`) the sign-in page also has a button that skips Steam. It only works from the same machine.

> [!WARNING]
> Never run Development behind the tunnel.
