# Changelog

## 0.2.0-alpha1

### Explanations first, AI second
- Built-in knowledge base: 300+ entries covering 360+ event types (power, crashes, services, storage, hardware, network, domain, security, Defender, updates), each with meaning, common causes and numbered fix steps.
- Explanations use the event's own data: *Service "Print Spooler" crashed (3rd time)*, *Power off requested by HDSentinel.exe*, *Failed logon for admin - Wrong password*.
- Offline error-code decoder: blue-screen stop codes, logon failure codes, Windows Update codes, and any code Windows itself can describe.
- Known noise (DCOM 10016, .NET 1022, Perflib, ESENT…) is shown as grey **Harmless** and kept out of counters and the health score. "Mark as harmless" for anything else.
- The knowledge base can update itself from GitHub (optional, once a day); everything works offline.

### New pages
- **Findings** - problems in short theses (repeated service crashes, failing disk, brute-force logons, third-party shutdowns, failed updates, VSS/backup errors, GPU resets…), with history and trend, notes, and "not seen in the last 7 days".
- **Timeline** - "what happened at…": all logs (incl. Defender, Windows Update, Remote Desktop) on one time line around a moment.
- **Compare** - new / gone / more / less frequent errors: today vs yesterday, last 7 or 30 days vs the period before.

### Events
- Right-click menu, Ctrl+F / F5 / Ctrl+C / Esc, Hide harmless, saved views, CSV and EVTX export, Cancel and "Load more", empty states.
- Detail panel: causes, numbered fix steps, decoded codes, "Seen N× on this PC since…", your notes, copy message.

### AI
- Ask AI sends only the selected event (≈300-800 tokens); **Deep analysis** adds surrounding events on request.
- Follow-up questions no longer resend the evidence. Automatic retry on rate limits, Retry button, formatted answers.
- Answers are saved locally - asking the same thing again costs no tokens.
- Default Groq model is now `llama-3.3-70b-versatile` (fast, no hidden "thinking" tokens).

### Reliability & polish
- Crash log + "Copy details" instead of silent exits; remote connection timeout; window size/position and column widths remembered; Back button.
- New-version notice (GitHub releases), Report issue button, privacy note.
- Unit tests for the knowledge base, rules and parsers run on every build.

## 0.1.0-alpha3
- Clickable overview, quick filters, grouping, restart analysis, Ask AI, remote computers, .evtx files, HTML report, app icon.
