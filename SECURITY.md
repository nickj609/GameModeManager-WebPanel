# Security Policy
GameModeManager Panel security policy guidelines.

## Supported Versions
We take the security of our users very seriously. Security updates will only be provided for:

- The latest release **(Current Version)**

> [!IMPORTANT]
> New releases come out automatically when .NET ships a security fix, so keep the panel up to date. Older versions are not supported and may contain unaddressed vulnerabilities.

## Reporting a Vulnerability
We encourage responsible disclosure of security vulnerabilities. If you believe you have found a vulnerability in the GameModeManager Panel, please [submit a vulnerability report!](https://github.com/nickj609/GameModeManager-WebPanel/security/advisories/new)
> [!NOTE]
> - We will acknowledge your report within 72 hours.
> - We do not offer rewards for vulnerability reports or fixes.
> - Once a fix is available, we will publicly announce the vulnerability and the corresponding patch version.

Please don't open a public issue for a vulnerability.

We appreciate your help in keeping the GameModeManager Panel secure!

## How the panel is built to be safe
- **No open ports.** The panel and GameModeManager both connect *out* to Cloudflare. Nothing on your home network or the game host listens to the internet, and RCON is never reached from outside.
- **Steam sign-in.** No passwords are stored. Panel admins are listed in `panel.json`; everyone else gets only what their admin flags allow on each server, checked again on the game server.
- **Server tokens are stored as hashes**, and the agent's address sits behind a Cloudflare Access service token that browsers can't pass.
- **A strict Content Security Policy** (no inline scripts or styles) and anti-forgery checks on every form.
- **An audit log** of every sign-in, refusal and command.

### Additional Information
> [!CAUTION]
> Keep the panel's `data` folder private (`chmod 700` on Linux) and never put `panel-secrets.json` in git. Anyone with a server's token and the Cloudflare service token can pose as that server. If one leaks, make a new token in **Site settings → Servers** and a new service token in Cloudflare.

Never run the panel in Development mode behind the tunnel: its sign-in button skips Steam.
