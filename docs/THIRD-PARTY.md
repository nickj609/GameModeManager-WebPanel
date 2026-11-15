# Third-party files in the panel

Everything here ships inside the panel's executable and Docker image. Each licence allows that.

| What | Where | Licence |
| --- | --- | --- |
| htmx 2.0.4 | `wwwroot/js/htmx-2.0.4.min.js` | BSD Zero Clause (0BSD), https://github.com/bigskysoftware/htmx |
| Barlow and Barlow Condensed (Jeremy Tribby) | `wwwroot/fonts/*.woff2` | SIL Open Font License 1.1, `wwwroot/fonts/OFL.txt` |
| Phosphor Icons 2.1.1, fill weight | `Pages/Shared/_Icons.cshtml` | MIT, Copyright (c) 2023 Phosphor Icons, https://github.com/phosphor-icons/core |
| AspNet.Security.OpenId.Steam (aspnet-contrib) | NuGet | Apache 2.0 |
| AngleSharp | NuGet | MIT |
| World map: Natural Earth 1:110m countries, as one SVG shape per country | `wwwroot/img/world.svg` | Public domain, https://www.naturalearthdata.com |
| Country flags: flag-icons 7.2.3 (Panayiotis Lipiridis), the 4:3 flags | `wwwroot/img/flags/*.svg` | MIT, `wwwroot/img/flags/LICENSE.txt`, https://github.com/lipis/flag-icons |

Player countries come from GameModeManager, which looks them up on the game server in DB-IP's free "IP to Country Lite"
database (CC BY 4.0, https://db-ip.com); the panel shows the credit "IP geolocation by DB-IP" under the map, as that
licence asks. The panel itself carries no part of that database.

The panel's executable and image carry no Counter-Strike 2 files. The icons carry the names CS2-SimpleAdmin's in-game
menu uses (crosshair, random, trophy, check, timer, fast, refresh, star) so sections match, but they're Phosphor's
drawings, not Valve's. The logo's crosshair is drawn from the owner's crosshair settings, not taken from the game.

CS2's map screenshots can be shown as backgrounds and map pictures, but they come from the owner's own installed game: `tools/GameArt`
copies them into the panel's data folder (not into git or a release). The tool itself uses these libraries; they're
built into the tool only, not the panel:

| What | Where | Licence |
| --- | --- | --- |
| ValveResourceFormat 20.1 (and ValvePak, ValveKeyValue), SteamDatabase | NuGet, `tools/GameArt` | MIT, https://github.com/ValveResourceFormat/ValveResourceFormat |
| SkiaSharp (Microsoft) | NuGet, `tools/GameArt` | MIT |

## Phosphor Icons licence

MIT License

Copyright (c) 2023 Phosphor Icons

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
