<img src="docs/eventlens-256.png" width="96" align="right" alt="EventLens icon">

# EventLens

**Readable Windows Event Log.** EventLens translates cryptic event IDs into plain language: why the PC restarted, what crashed, who failed to log on - and what to do about it.

Modern Windows 11 look (Fluent, Mica, light/dark), single portable exe, no install.

## Features

- **Overview** - health score (0-100) with verdict, clickable stat cards with 30-day trends, error heatmap, timeline of restarts/crashes/BSODs
- **Events** - System + Application (or any log), quick filters (Restarts, Crashes, Blue screens, Disk, Services, Logons, Updates, Network), grouped repeats, live search
- **Plain-language explanations** for common event IDs, with what to do next
- **Why did it restart?** - every restart in the last 120 days classified (planned, Windows Update, unexpected, blue screen) with the process and user behind it
- **Ask AI** - send an event or a restart with surrounding context to your own AI (Groq, Gemini, OpenAI, Claude, OpenRouter, Ollama); key stored encrypted (DPAPI)
- **Remote computers** (current or other credentials) and **.evtx files**
- **HTML health report** for a manager or client

## Roadmap

- Live mode and tray notifications for critical events
- Localization (Serbian, Bosnian, Croatian...)
- Microsoft Store + winget

## Build

Requires the .NET 10 SDK on Windows.

```
dotnet publish src/EventLens/EventLens.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Every push builds an exe in GitHub Actions; tags `v*` create a Release.

## License

MIT © 2026 Đorđe Kaurinović
