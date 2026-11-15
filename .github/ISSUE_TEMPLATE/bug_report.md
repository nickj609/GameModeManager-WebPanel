---
name: Bug report
about: Create a report to help us improve.
title: ''
labels: ''
assignees: ''
---

### Describe the bug
_A clear and concise description of what the bug is._

### To Reproduce
_Steps to reproduce the behavior: the page, what you clicked, what you saw._

### Expected behavior
_A clear and concise description of what you expected to happen._

### Screenshots
_If applicable, add screenshots to help explain your problem._

### Versions
- Panel version: _(the footer of Site settings, or the release you downloaded)_
- GameModeManager version: _(`css_plugins list`)_
- Runs on: _(Windows, Raspberry Pi, Docker)_
- Browser: _(Chrome, Edge, Firefox, Safari)_

### Logging
_Please provide the lines around the error from the panel's log, `data/logs/panel-<date>.log`, and if it's about a server, GameModeManager's log from `/csgo/addons/counterstrikesharp/logs/`. Remove any tokens or IP addresses first._

_Example_
```
16:42:19 erro Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware: An unhandled exception has occurred while executing the request.
System.NullReferenceException: Object reference not set to an instance of an object.
   at GmmPanel.Pages.Audit.IndexModel.OnGet()
```

### Additional context
_Add any other context about the problem here._
