<img src="docs/eventlens-256.png" width="96" align="right" alt="EventLens icon">

# EventLens

**The Event Viewer++ that explains itself.**

EventLens reads the Windows event logs and tells you in plain English what happened, why, and what to do: why the PC restarted, what keeps crashing, whether a disk is failing, who is trying to log on - and which errors are just harmless noise.

Modern Windows 11 look (Fluent, Mica, light/dark). One portable exe, no install. Works fully offline.

## Features

- **Overview** - health score with a verdict, clickable counters with 30-day trends, error heatmap, the top problems, and a timeline of restarts and crashes.
- **Findings** - problems on this PC in short theses, worked out offline by rules that connect events: repeated service crashes, failing disks, brute-force logons, programs that shut the PC down, failed updates, VSS/backup errors, GPU resets, low memory… with history, trend and your own notes.
- **Events** - fast filtering, quick filters, grouping of repeats, live search, right-click actions, saved views, CSV/EVTX export.
- **Built-in knowledge base** - 300+ entries for 360+ event types: meaning, common causes, numbered fix steps; filled with the event's own data. Decodes blue-screen stop codes, logon failure codes and Windows Update errors offline. Known noise is marked **Harmless**.
- **Timeline** - "what happened at 10:15?": every log (System, Application, Security, Defender, Windows Update, Remote Desktop…) on one time line.
- **Compare** - what is new, gone, or more frequent: today vs yesterday, this week vs last week.
- **Why did it restart?** - every restart in the last 120 days classified (planned, Windows Update, unexpected, blue screen) with the process and user behind it.
- **Ask AI** (optional, your own key) - sends only the selected event; *Deep analysis* adds the surrounding events. Groq, Gemini, OpenAI, Claude, OpenRouter or local Ollama. Answers are cached locally.
- **Remote computers** and **.evtx files**, **HTML health report**.

## Privacy

Everything stays on your PC. Event text is sent only when you press **Ask AI**, only to the service you chose, and you can see exactly what is sent. History, notes and saved answers live in `%AppData%\EventLens`.

## Contributing to the knowledge base

Explanations live in [`knowledge/*.json`](knowledge) - one entry per event type:

```json
{ "provider": "Service Control Manager", "id": 7031, "class": "attention", "category": "Service",
  "title": "Service crashed and was restarted",
  "summary": "Service \"{0}\" crashed ({1|ordinal} time) - Windows restarted it",
  "what": "…", "causes": ["…"], "fix": ["…"], "related": [7034, 1000] }
```

`class` is `harmless`, `info`, `attention` or `critical`. `{0}` inserts event data field 0; filters: `logontype`, `status`, `bugcheck`, `ordinal`, `file`, `cap`. Bump `version` in `knowledge/manifest.json` and EventLens installs pick up the change automatically.

## Build

Requires the .NET 10 SDK on Windows.

```
dotnet test tests/EventLens.Tests
dotnet publish src/EventLens/EventLens.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Every push builds and tests in GitHub Actions; tags `v*` create a release.

## License

MIT © 2026 Đorđe Kaurinović
