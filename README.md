# ramspeed

a lightweight windows memory optimizer built with .net 8 and wpf.

reduces ram pressure using windows memory management apis including working set trimming, standby list purge, modified page flush, registry cache flush, page deduplication, and file cache management.

## features

- real-time memory monitoring with usage graph
- automatic optimization when ram exceeds configurable threshold
- three optimization levels: conservative, balanced, aggressive
- foreground process protection (never trims the active window)
- system tray integration with live ram percentage display
- windows 11 native ui with mica backdrop and fluent design
- automatic dark/light mode with system accent color
- per-process memory priority and exclusion controls
- scheduled optimization at configurable intervals
- self-trimming to minimize own memory footprint

## requirements

- windows 10 or later
- .net 8.0 runtime
- administrator privileges for memory optimization

## building

```
dotnet build src/RAMSpeed/RAMSpeed.csproj
```

## running

```
dotnet run --project src/RAMSpeed/RAMSpeed.csproj
```

## testing

```
dotnet test tests/RAMSpeed.Tests/RAMSpeed.Tests.csproj
```

## license

mit
