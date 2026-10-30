# EventLens

**Readable Windows Event Log.** EventLens translates cryptic event IDs into plain language: why the PC restarted, what crashed, who failed to log on - and what to do about it.

Modern Windows 11 look (Fluent, Mica, light/dark), single portable exe, no install.

## Features (v0.1)

- **Overview** - uptime, unexpected shutdowns, blue screens, app crashes, errors/warnings, failed logons, and a timeline of what happened recently
- **Events** - fast filtering by log, level and time window, live search, virtualized list for thousands of events
- **Explanations** - built-in knowledge base for common event IDs (Kernel-Power 41, 6008, 1074, BugCheck 1001, service crashes, disk errors, logon failures, DCOM noise...)
- **Run as administrator** from Settings to read the Security log

## Roadmap

- Remote computers (EventLogSession, domain)
- "Why did my PC restart?" wizard
- Export report (HTML/PDF/CSV)
- Optional AI explanation for unknown events (bring your own key)
- Microsoft Store + winget

## Build

Requires the .NET 10 SDK on Windows.

```
dotnet publish src/EventLens/EventLens.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Every push builds an exe in GitHub Actions; tags `v*` create a Release.

## License

MIT © 2026 Đorđe Kaurinović
