# Jellyfin.Xtream Plugin - Development Guide

## Build & Test

```bash
# Build (must disable TreatWarningsAsErrors for Release due to unreachable JFROG NuGet source)
cd Jellyfin.Xtream && dotnet build -c Release -p:TreatWarningsAsErrors=false --no-restore

# Run tests
dotnet test Jellyfin.Xtream.Tests/

# Package plugin (requires jprm)
jprm plugin build Jellyfin.Xtream
```

## Critical Configuration

### Version Bumping
- **Always bump both** `build.yaml` (version field) and `Jellyfin.Xtream/Jellyfin.Xtream.csproj` (AssemblyVersion) together.
- Jellyfin caches plugin DLLs by folder name (`Jellyfin Xtream_{version}/`). Reinstalling the same version does NOT replace the DLL. You must bump the version to force a fresh install.

### Plugin Discovery
- Jellyfin discovers `IScheduledTask` implementations via **assembly scanning**, not DI registration. The DI registration in `PluginServiceRegistrator.cs` is redundant but harmless.
- Tasks are instantiated via `ActivatorUtilities.CreateInstance`.

### NuGet / Build Warnings
- The NuGet config references an unreachable JFROG source. With `TreatWarningsAsErrors=true` (set in csproj), the NU1900 warning becomes a build error in Release mode. Always pass `-p:TreatWarningsAsErrors=false` for Release builds.

## Architecture

### Live TV Restreaming (`Service/Restream.cs`)
- Opens an HTTP connection to the Xtream provider and copies data into a 16MB circular buffer (`WrappedBufferStream`).
- **Auto-reconnects** on upstream disconnection (up to 5 retries with exponential backoff).
- Marks the buffer as `IsCompleted` only when reconnection fully fails or cancellation is requested.
- Readers (`WrappedBufferReadStream`) return EOF when `IsCompleted` is set, allowing ffmpeg to exit cleanly.

### Circular Buffer (`Service/WrappedBufferStream.cs`)
- Fixed 16MB ring buffer. Writers wrap around; readers track a virtual `ReadHead`.
- `IsCompleted` flag signals that no more data will arrive.
- Do NOT reduce the buffer size below 16MB — it must hold enough data for keyframe seeking (2-5 seconds of video at up to 20Mbps).

### Keyframe Seeking (`Service/WrappedBufferReadStream.cs`)
- On playback start, seeks to a clean MPEG-TS position using:
  1. PAT/PMT parsing to identify video PID
  2. Random Access Indicator (RAI) flag on the video PID
  3. Fallback to H.264 SPS NAL scanning (payload-only, skipping TS headers)
  4. Backs up to include the most recent PAT before the keyframe
- This ensures proper A/V sync and eliminates "non-existing PPS 0 referenced" errors.

### Channel Architecture
- `SeriesChannel.cs` — Flattened series channel (all series at root level, seasons/episodes as children)
- `VodChannel.cs` — Flattened VOD channel (23K+ items)
- Both use 1-hour cache, sequential fetch with retry/backoff to the Xtream API.

### Scheduled Tasks
- `Service/SeriesIndexTask.cs` — Indexes all series/seasons/episodes into Jellyfin's DB using `IChannelManager.GetChannelItemsInternal`. Runs daily at 4 AM. Uses 150ms delay between API calls.
- Uses `TaskTriggerInfoType.DailyTrigger` enum (not the old string constants).

## Deployment

- **Production**: https://boxer.kristiansen.cf
- **Fork remote**: `fork` → dank100/Jellyfin.Xtream
- **Branch**: `feature/recording-and-epg-timezone`
- **Manifest branch**: `manifest`
- **Jellyfin version**: 10.11.5

## Common Pitfalls

1. **Do NOT use `GetCallingAssembly()`** in `Plugin.cs` — use `GetType().Assembly` for DataVersion.
2. **Do NOT reduce the initial buffer wait** (2MB minimum) — ffmpeg's probe needs at least one keyframe.
3. **Do NOT scan TS headers for NAL units** — only scan actual payload bytes (after 4-byte header + adaptation field).
4. **inotify watch limit** (93,013) can be exhausted by large library indexing. The Docker container may need `fs.inotify.max_user_watches=524288`.
5. **Socket exhaustion** can occur during heavy API usage. The SeriesIndexTask uses 150ms delays to mitigate this.
