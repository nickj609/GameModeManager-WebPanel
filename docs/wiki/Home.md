> [!IMPORTANT]
> The panel is an add-on for [GameModeManager](https://github.com/nickj609/GameModeManager). Set up GameModeManager on your server first; the panel shows and changes what it manages.

# Getting Started
To get started, make sure all of the following prerequisites are met:

- [GameModeManager](https://github.com/nickj609/GameModeManager) **2.4.0 or newer** is running on each game server.
- You have a domain for the panel's https address. The guide uses [Cloudflare](https://www.cloudflare.com/)'s free plan, but [any https setup works](https://github.com/nickj609/GameModeManager-WebPanel/wiki/Installation#other-ways-to-host).
- You have somewhere to run it that's always on: a Windows PC, a Raspberry Pi 4 or 5 with a 64-bit OS, or a server or Docker host.

## Installation
1. In Cloudflare, create a tunnel and give the panel an address on your domain, like `panel.example.com`. People open it in their browser, and your game servers connect to it too.
2. Download the panel from [Releases](https://github.com/nickj609/GameModeManager-WebPanel/releases), run it, and open the setup page with the code it prints.
3. Sign in through Steam, add your first server, and paste the settings it gives you into `GameModeManager.json`.

For more information, see [Installation](https://github.com/nickj609/GameModeManager-WebPanel/wiki/Installation), [Configuration](https://github.com/nickj609/GameModeManager-WebPanel/wiki/Configuration), [Permissions](https://github.com/nickj609/GameModeManager-WebPanel/wiki/Permissions) and [Languages](https://github.com/nickj609/GameModeManager-WebPanel/wiki/Languages).

# Need Help?
If you have a question, check out our [FAQ](https://github.com/nickj609/GameModeManager-WebPanel/wiki/FAQ-(Frequently-Asked-Questions)) and if you still need help, [create a new issue](https://github.com/nickj609/GameModeManager-WebPanel/issues/new/choose).
