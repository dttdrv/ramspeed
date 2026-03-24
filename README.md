# RAMSpeed

A lightweight, high-performance Windows memory optimizer. Reclaims wasted RAM in real time using native Windows memory management APIs.

## What it does

RAMSpeed monitors your system's memory usage and automatically frees RAM when pressure is detected. It uses the same low-level Windows APIs that built-in tools like RAMMap use — working set trimming, standby list management, page deduplication, and file cache control — but does it automatically and intelligently.

### Optimization pipeline

- **Adaptive escalation** — starts with light optimization, escalates only if memory pressure persists
- **Predictive triggering** — detects rapid memory consumption trends and acts before the system starts paging
- **Smart process trimming** — targets the biggest memory consumers first, stops as soon as enough is freed
- **Effectiveness tracking** — learns which optimization steps actually help on your system and skips the rest
- **Compression awareness** — adapts behavior based on how actively Windows is compressing memory

### Interface

- Real-time memory monitoring with live usage graph
- System tray icon showing current RAM usage percentage
- Per-process memory controls (priority, exclusion lists)
- Three optimization levels: Conservative, Balanced, Aggressive
- Windows 11 native UI with Mica backdrop and Fluent design
- Automatic dark/light mode following system theme

### Safety

- Never trims the foreground application
- Hysteresis prevents repeated optimization when memory hovers near the threshold
- Configurable cooldown between optimization cycles
- All optimization is reversible — Windows will page data back in as needed

## Download

Get the latest release from the [Releases](https://github.com/dttdrv/ramspeed/releases) page:

- **RAMSpeed-Setup.exe** — Installer (recommended). Handles elevation, Start Menu shortcuts, startup registration, and clean uninstall.
- **RAMSpeed.exe** — Portable single-file executable. No installation required.

## Requirements

- Windows 10 or later
- Administrator privileges (for memory optimization; monitoring works without admin)

The .NET runtime is bundled — no separate installation needed.

## Configuration

Settings are stored in `%APPDATA%\RAMSpeed\settings.json`. The UI exposes the most common options. Advanced settings available in the JSON file:

| Setting | Default | Description |
|---|---|---|
| `ThresholdPercent` | 80 | RAM usage % that triggers auto-optimization |
| `CooldownSeconds` | 30 | Minimum seconds between optimizations |
| `HysteresisGap` | 10 | Usage must drop this far below threshold before re-triggering |
| `TrendWindowSize` | 10 | Number of readings used for trend prediction |
| `PredictiveLeadSeconds` | 15 | How far ahead to predict threshold breach |
| `AccessedBitsDelayMs` | 2000 | Delay for two-pass page analysis |
| `EffectivenessTrackingEnabled` | true | Skip optimization steps that consistently free < 1 MB |

## Building from source

```
dotnet build src/RAMSpeed/RAMSpeed.csproj
```

### Publish portable exe

```
dotnet publish src/RAMSpeed/RAMSpeed.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o portable/
```

### Build installer

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```
iscc installer/RAMSpeed.iss
```

### Run tests

```
dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj
```

## License

MIT
