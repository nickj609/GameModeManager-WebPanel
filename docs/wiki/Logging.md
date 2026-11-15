# Logging
The panel writes its log to the console and to `data/logs/panel-<yyyy-MM-dd>.log`, one file a day, kept 14 days (**Site settings → Retention**).

```
03:24:03 info GmmPanel.Agents.AgentHub: Agent Production disconnected
03:24:04 info GmmPanel.Agents.AgentHub: Agent Production connected from 203.0.113.10: GameModeManager 2.4.0, CounterStrikeSharp 1.0.380
09:40:26 info GmmPanel.Data.AuditLog: Audit: Alex (76561198000000000) sign in on -: ok panel admin, from 198.51.100.7
```

| Log | Where | What's in it |
| --- | --- | --- |
| The panel's log | `data/logs/` | Starting up, servers connecting and going offline, errors |
| The audit log | The **Audit log** page, `/audit` (panel admins), and `data/audit.jsonl` | Every sign-in, refusal and command: who, what, which server, the result. Search it, and export what you find as CSV or JSON. |
| GameModeManager's log | `/csgo/addons/counterstrikesharp/logs/` on the game server | The agent's side: connecting, and each command the panel sent (`[Panel] ...`) |

When something goes wrong, the panel shows "Something went wrong in the panel. It's been logged." Look for `erro` lines in that day's log, and include them when you [report a bug](https://github.com/nickj609/GameModeManager-WebPanel/issues/new/choose) (without tokens or IP addresses).
