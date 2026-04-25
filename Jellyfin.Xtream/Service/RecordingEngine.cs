// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Background service that monitors timers and records live TV streams to disk.
/// </summary>
public class RecordingEngine : IHostedService, IDisposable
{
    private const string FinalExtension = ".mkv";
    private const string HlsPlaylistName = "live.m3u8";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RecordingEngine> _logger;
    private readonly LiveTvService _liveTvService;
    private readonly IServerConfigurationManager _config;
    private readonly IServerApplicationHost _appHost;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ConnectionMultiplexer _multiplexer;
    private readonly ConcurrentDictionary<string, ActiveRecording> _activeRecordings = new();
    private readonly ConcurrentDictionary<string, CompletedRecording> _completedRecordings = new();
    // HLS directories that are still being served (kept through merge so viewers aren't interrupted)
    private readonly ConcurrentDictionary<string, string> _servableHlsDirs = new();
    // TS file paths kept servable through cleanup so clients can drain remaining data
    private readonly ConcurrentDictionary<string, string> _servableTsPaths = new();
    private Timer? _pollTimer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingEngine"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{RecordingEngine}"/> interface.</param>
    /// <param name="liveTvService">Instance of the <see cref="LiveTvService"/> class.</param>
    /// <param name="config">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="multiplexer">Instance of the <see cref="ConnectionMultiplexer"/> class.</param>
    public RecordingEngine(IHttpClientFactory httpClientFactory, ILogger<RecordingEngine> logger, LiveTvService liveTvService, IServerConfigurationManager config, IServerApplicationHost appHost, ILibraryMonitor libraryMonitor, ConnectionMultiplexer multiplexer)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _liveTvService = liveTvService;
        _config = config;
        _appHost = appHost;
        _libraryMonitor = libraryMonitor;
        _multiplexer = multiplexer;
    }

    /// <summary>
    /// Gets the directory where recordings are stored (Jellyfin's default recording path).
    /// </summary>
    public string RecordingsPath
    {
        get
        {
            string path = Path.Combine(_config.CommonApplicationPaths.DataPath, "livetv", "recordings");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>
    /// Gets the active recordings.
    /// </summary>
    public IReadOnlyDictionary<string, ActiveRecording> ActiveRecordings => _activeRecordings;

    /// <summary>
    /// Gets the completed recordings.
    /// </summary>
    public IReadOnlyDictionary<string, CompletedRecording> CompletedRecordings => _completedRecordings;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Recording engine started. Recordings path: {Path}", RecordingsPath);
        CleanupOrphanedRecordingArtifacts();
        _pollTimer = new Timer(CheckTimers, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _pollTimer?.Change(Timeout.Infinite, 0);

        foreach (var recording in _activeRecordings.Values)
        {
            recording.Cancel();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts a recording immediately for the given timer.
    /// </summary>
    /// <param name="timer">The timer info.</param>
    public void StartRecording(TimerInfo timer)
    {
        if (_activeRecordings.ContainsKey(timer.Id))
        {
            _logger.LogDebug("Recording already active for timer {TimerId}", timer.Id);
            return;
        }

        timer.Status = RecordingStatus.InProgress;
        _liveTvService.UpdateTimerStatus(timer);

        var recording = new ActiveRecording(timer);
        if (!_activeRecordings.TryAdd(timer.Id, recording))
        {
            return;
        }

        _ = Task.Run(() => RecordStreamAsync(recording));
    }

    /// <summary>
    /// Cancels an active recording.
    /// </summary>
    /// <param name="timerId">The timer ID to cancel.</param>
    public void CancelRecording(string timerId)
    {
        if (_activeRecordings.TryRemove(timerId, out var recording))
        {
            recording.Cancel();

            // Mark as completed so CheckTimers won't re-start it
            _completedRecordings[timerId] = new CompletedRecording
            {
                TimerId = timerId,
                Timer = recording.Timer,
                FilePath = recording.FilePath ?? string.Empty,
                CompletedAt = DateTime.UtcNow,
            };

            _logger.LogInformation("Recording cancelled for timer {TimerId}", timerId);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged and managed resources.
    /// </summary>
    /// <param name="disposing">Whether managed resources should be disposed.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _pollTimer?.Dispose();
            foreach (var recording in _activeRecordings.Values)
            {
                recording.Dispose();
            }
        }

        _disposed = true;
    }

    private void CheckTimers(object? state)
    {
        try
        {
            var timers = _liveTvService.GetTimersSnapshot();
            var now = DateTime.UtcNow;

            _logger.LogDebug("CheckTimers: {Count} timer(s) found, now={Now}", timers.Count, now);

            foreach (var timer in timers)
            {
                // Skip already active or completed recordings
                if (_activeRecordings.ContainsKey(timer.Id) || _completedRecordings.ContainsKey(timer.Id))
                {
                    continue;
                }

                var startWithPadding = timer.StartDate.AddSeconds(-timer.PrePaddingSeconds);
                var endWithPadding = timer.EndDate.AddSeconds(timer.PostPaddingSeconds);

                _logger.LogDebug(
                    "Timer {TimerId} '{Name}': Start={Start}, End={End}, Now={Now}, InWindow={InWindow}",
                    timer.Id,
                    timer.Name,
                    startWithPadding,
                    endWithPadding,
                    now,
                    now >= startWithPadding && now < endWithPadding);

                // Start recording if we're within the recording window
                if (now >= startWithPadding && now < endWithPadding)
                {
                    _logger.LogInformation("Timer {TimerId} is due, starting recording for {Name}", timer.Id, timer.Name);
                    StartRecording(timer);
                }
            }

            // Stop recordings that have passed their end time
            foreach (var kvp in _activeRecordings)
            {
                var timer = kvp.Value.Timer;
                var endWithPadding = timer.EndDate.AddSeconds(timer.PostPaddingSeconds);
                if (now >= endWithPadding)
                {
                    _logger.LogInformation("Recording for timer {TimerId} has reached end time, stopping", timer.Id);
                    if (_activeRecordings.TryRemove(kvp.Key, out var recording))
                    {
                        recording.Cancel();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking timers");
        }
    }

    private async Task RecordStreamAsync(ActiveRecording recording)
    {
        var timer = recording.Timer;
        string baseName = $"{timer.Name}_{timer.StartDate:yyyyMMdd}_{timer.StartDate:HH.mm}-{timer.EndDate:HH.mm}_rec{recording.StartedUtc.ToLocalTime():HH.mm}"
            .Replace(' ', '_')
            .Replace('/', '-')
            .Replace('\\', '-');

        // TS file is hidden (dot-prefix) to prevent Jellyfin's library manager
        // from detecting and deleting it during scans.
        string tsPath = Path.Combine(RecordingsPath, "." + baseName + ".ts");

        // Each recording gets a hidden subdirectory for HLS segments (seeking)
        string segmentDir = Path.Combine(RecordingsPath, $".rec_{timer.Id}");
        Directory.CreateDirectory(segmentDir);
        string playlistPath = Path.Combine(segmentDir, HlsPlaylistName);

        string tsApiUrl = $"{_appHost.GetSmartApiUrl(IPAddress.Any)}/Xtream/Recordings/{timer.Id}/stream.ts";
        string strmPath = Path.Combine(RecordingsPath, baseName + ".strm");

        _logger.LogInformation("Recording started: {Name} -> {TsPath}", timer.Name, tsPath);
        recording.FilePath = segmentDir;
        recording.TsFilePath = tsPath;
        recording.StrmFilePath = strmPath;

        // Write .strm file pointing to our TS endpoint. The endpoint serves the
        // current file snapshot so ffprobe completes quickly.
        await File.WriteAllTextAsync(strmPath, tsApiUrl).ConfigureAwait(false);

        try
        {
            // Subscribe to the multiplexer so it captures this channel
            Guid guid = Guid.Parse(timer.ChannelId);
            StreamService.FromGuid(guid, out int _, out int streamId, out int _, out int _);

            var muxBuffer = _multiplexer.Subscribe(streamId);
            recording.MuxStreamId = streamId;

            // Wait for the multiplexer to have segments
            _logger.LogInformation("Recording waiting for multiplexer segments on stream {StreamId}", streamId);
            for (int i = 0; i < 60; i++)
            {
                if (muxBuffer.State == ChannelBufferState.Ready)
                {
                    break;
                }

                await Task.Delay(1000, recording.CancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Recording from multiplexer segments for stream {StreamId}", streamId);

            // Directly copy multiplexer segments into the TS file and our own HLS directory.
            // This avoids running ffmpeg as an HLS reader (which fails during round-robin gaps).
            int lastSeenGlobal = -1;
            int recSegCount = 0;
            var hlsLines = new List<string>
            {
                "#EXTM3U",
                "#EXT-X-VERSION:3",
                "#EXT-X-TARGETDURATION:6",
                "#EXT-X-MEDIA-SEQUENCE:0",
                "#EXT-X-PLAYLIST-TYPE:EVENT",
            };
            bool libraryNotified = false;

            using var tsStream = new FileStream(tsPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 81920);
            var ct = recording.CancellationToken;

            while (!ct.IsCancellationRequested)
            {
                var segments = muxBuffer.GetSegments();
                bool foundNew = false;

                for (int si = 0; si < segments.Count; si++)
                {
                    var seg = segments[si];
                    int globalIdx = muxBuffer.GetGlobalIndex(si);
                    if (globalIdx <= lastSeenGlobal)
                    {
                        continue;
                    }

                    lastSeenGlobal = globalIdx;
                    foundNew = true;

                    string srcPath = Path.Combine(muxBuffer.SegmentDir, seg.Filename);
                    if (!File.Exists(srcPath))
                    {
                        continue;
                    }

                    // Read segment data
                    byte[] data = await File.ReadAllBytesAsync(srcPath, ct).ConfigureAwait(false);

                    // Append to the growing TS file
                    await tsStream.WriteAsync(data, ct).ConfigureAwait(false);
                    await tsStream.FlushAsync(ct).ConfigureAwait(false);

                    // Copy segment to our own HLS directory
                    string recSegName = $"seg_{recSegCount:D5}.ts";
                    string recSegPath = Path.Combine(segmentDir, recSegName);
                    await File.WriteAllBytesAsync(recSegPath, data, ct).ConfigureAwait(false);
                    recSegCount++;

                    // Update HLS playlist
                    hlsLines.Add($"#EXTINF:{seg.DurationSeconds:F3},");
                    hlsLines.Add(recSegName);
                    await File.WriteAllLinesAsync(playlistPath, hlsLines, ct).ConfigureAwait(false);

                    if (!libraryNotified && tsStream.Length > 0)
                    {
                        libraryNotified = true;
                        _libraryMonitor.ReportFileSystemChanged(strmPath);
                        TriggerMediaScan();
                        TriggerGuideRefresh();
                        _logger.LogInformation("Library notified of new recording: {Path}", strmPath);
                    }
                }

                if (!foundNew)
                {
                    // No new segments yet — wait briefly before checking again.
                    // The multiplexer round-robins between channels, so gaps are normal.
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Recording stopped (cancelled): {Name}", timer.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording stream for timer {TimerId}", timer.Id);
        }
        finally
        {
            // Unsubscribe from the multiplexer
            if (recording.MuxStreamId.HasValue)
            {
                _multiplexer.Unsubscribe(recording.MuxStreamId.Value);
            }

            // Keep HLS directory servable during merge so active viewers aren't interrupted
            _servableHlsDirs[timer.Id] = segmentDir;
            _servableTsPaths[timer.Id] = tsPath;
            _activeRecordings.TryRemove(timer.Id, out _);

            if (File.Exists(playlistPath))
            {
                // Merge HLS segments into a single MKV
                string mkvPath = Path.Combine(RecordingsPath, baseName + FinalExtension);
                string completedPath = await MergeHlsToMkvAsync(playlistPath, mkvPath).ConfigureAwait(false);
                bool mergeSucceeded = string.Equals(completedPath, mkvPath, StringComparison.Ordinal);

                // Remove from servable dirs and clean up
                _servableHlsDirs.TryRemove(timer.Id, out _);
                _servableTsPaths.TryRemove(timer.Id, out _);

                if (mergeSucceeded)
                {
                    try
                    {
                        Directory.Delete(segmentDir, true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to clean up segment directory: {Dir}", segmentDir);
                    }
                }

                if (File.Exists(tsPath))
                {
                    try
                    {
                        File.Delete(tsPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to clean up TS file: {Path}", tsPath);
                    }
                }

                // Remove the .strm file — the .mkv is the final library item
                if (File.Exists(strmPath))
                {
                    try
                    {
                        File.Delete(strmPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to clean up .strm file: {Path}", strmPath);
                    }
                }

                timer.Status = RecordingStatus.Completed;
                _liveTvService.UpdateTimerStatus(timer);
                _completedRecordings[timer.Id] = new CompletedRecording
                {
                    TimerId = timer.Id,
                    Timer = timer,
                    FilePath = completedPath,
                    CompletedAt = DateTime.UtcNow,
                };
                _logger.LogInformation("Recording completed: {Name} ({Size} bytes)", timer.Name, new FileInfo(completedPath).Length);
                _libraryMonitor.ReportFileSystemChanged(completedPath);
                TriggerMediaScan();
                TriggerGuideRefresh();
            }
            else
            {
                _servableHlsDirs.TryRemove(timer.Id, out _);
                _servableTsPaths.TryRemove(timer.Id, out _);
                timer.Status = RecordingStatus.Error;
                _liveTvService.UpdateTimerStatus(timer);
                _logger.LogWarning("Recording produced no output: {Name}", timer.Name);
            }

            recording.Dispose();
        }
    }

    /// <summary>
    /// Gets the HLS segment directory path for an active recording.
    /// Returns null if the recording is not active.
    /// </summary>
    /// <param name="timerId">The timer ID.</param>
    /// <returns>The path to the HLS segment directory, or null.</returns>
    public string? GetHlsDirectory(string timerId)
    {
        if (_activeRecordings.TryGetValue(timerId, out var recording) && recording.FilePath != null)
        {
            return recording.FilePath;
        }

        // Also serve directories that are in the merge phase (recording just ended)
        if (_servableHlsDirs.TryGetValue(timerId, out var dir))
        {
            return dir;
        }

        return null;
    }

    /// <summary>
    /// Gets the growing TS file path for an active recording.
    /// Returns null if the recording is not active.
    /// </summary>
    /// <param name="timerId">The timer ID.</param>
    /// <returns>The TS file path, or null.</returns>
    public string? GetTsFilePath(string timerId)
    {
        if (_activeRecordings.TryGetValue(timerId, out var recording) && recording.TsFilePath != null)
        {
            return recording.TsFilePath;
        }

        if (_servableTsPaths.TryGetValue(timerId, out var path))
        {
            return path;
        }

        return null;
    }

    /// <summary>
    /// Checks whether a recording is still actively being written.
    /// </summary>
    /// <param name="timerId">The timer ID.</param>
    /// <returns>True if the recording is still in progress.</returns>
    public bool IsRecordingActive(string timerId) => _activeRecordings.ContainsKey(timerId);

    /// <summary>
    /// Gets the active recording for a timer, or null if not active.
    /// </summary>
    /// <param name="timerId">The timer ID.</param>
    /// <returns>The active recording, or null.</returns>
    public ActiveRecording? GetActiveRecording(string timerId)
    {
        _activeRecordings.TryGetValue(timerId, out var recording);
        return recording;
    }

    /// <summary>
    /// Gets a snapshot of active recordings that have a ready (non-empty) TS file.
    /// Used by LiveTvService to expose virtual recording channels.
    /// </summary>
    /// <returns>A list of active recordings with ready TS files.</returns>
    public IReadOnlyList<ActiveRecording> GetReadyRecordingsSnapshot()
    {
        var result = new List<ActiveRecording>();
        foreach (var kvp in _activeRecordings)
        {
            var rec = kvp.Value;
            if (!string.IsNullOrEmpty(rec.TsFilePath) && File.Exists(rec.TsFilePath) && new FileInfo(rec.TsFilePath).Length > 0)
            {
                result.Add(rec);
            }
        }

        return result;
    }

    /// <summary>
    /// Merges HLS segments into a single MKV file.
    /// Returns the final file path (MKV on success, concatenated TS on failure).
    /// </summary>
    private async Task<string> MergeHlsToMkvAsync(string playlistPath, string mkvPath)
    {
        var ffmpegPath = GetFfmpegPath();
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-allowed_extensions ALL -i \"{playlistPath}\" -map 0 -c copy -f matroska -y \"{mkvPath}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _logger.LogInformation("Merging HLS to MKV: {Source} -> {Dest}", playlistPath, mkvPath);

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ffmpeg for merge");

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            if (process.ExitCode == 0 && File.Exists(mkvPath) && new FileInfo(mkvPath).Length > 0)
            {
                _logger.LogInformation("Merge completed successfully: {Path}", mkvPath);
                return mkvPath;
            }

            _logger.LogWarning("Merge failed (exit code {Code}), keeping HLS segments", process.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Merge failed, keeping HLS segments");
        }

        return playlistPath;
    }

    private void TriggerMediaScan()
    {
        try
        {
            Plugin.Instance.TaskService.CancelIfRunningAndQueue(
                "Emby.Server.Implementations",
                "Emby.Server.Implementations.ScheduledTasks.Tasks.RefreshMediaLibraryTask");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to trigger media library scan");
        }
    }

    private void TriggerGuideRefresh()
    {
        try
        {
            Plugin.Instance.TaskService.CancelIfRunningAndQueue(
                "Jellyfin.LiveTv",
                "Jellyfin.LiveTv.Channels.RefreshChannelsScheduledTask");
            Plugin.Instance.TaskService.CancelIfRunningAndQueue(
                "Jellyfin.LiveTv",
                "Jellyfin.LiveTv.Guide.RefreshGuideScheduledTask");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to trigger guide refresh");
        }
    }

    private void CleanupOrphanedRecordingArtifacts()
    {
        try
        {
            string recPath = RecordingsPath;

            // Build a set of paths that belong to active recordings — don't touch these
            var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _activeRecordings)
            {
                var rec = kvp.Value;
                if (!string.IsNullOrEmpty(rec.TsFilePath))
                {
                    activePaths.Add(rec.TsFilePath);
                }

                if (!string.IsNullOrEmpty(rec.StrmFilePath))
                {
                    activePaths.Add(rec.StrmFilePath);
                }

                if (!string.IsNullOrEmpty(rec.FilePath))
                {
                    activePaths.Add(rec.FilePath);
                }
            }

            // Remove orphaned .strm files left from previous plugin versions
            foreach (string strmFile in Directory.GetFiles(recPath, "*.strm"))
            {
                if (activePaths.Contains(strmFile))
                {
                    continue;
                }

                _logger.LogInformation("Cleaning up orphaned .strm file: {Path}", strmFile);
                File.Delete(strmFile);
            }

            // Remove orphaned hidden .ts files (dot-prefix) from previous plugin versions
            foreach (string tsFile in Directory.GetFiles(recPath, ".*.ts"))
            {
                if (activePaths.Contains(tsFile))
                {
                    continue;
                }

                _logger.LogInformation("Cleaning up orphaned hidden .ts file: {Path}", tsFile);
                File.Delete(tsFile);
            }

            // Remove orphaned non-hidden .ts files (skip active recordings)
            foreach (string tsFile in Directory.GetFiles(recPath, "*.ts"))
            {
                string name = Path.GetFileName(tsFile);
                if (name.StartsWith('.') || activePaths.Contains(tsFile))
                {
                    continue;
                }

                _logger.LogInformation("Cleaning up orphaned .ts file: {Path}", tsFile);
                File.Delete(tsFile);
            }

            // Clean up orphaned .rec_* segment directories from previous recordings.
            // On restart, these are leftover from interrupted recordings whose normal
            // merge was killed. Just delete them — the recording was interrupted anyway.
            var activeTimerIds = new HashSet<string>(_activeRecordings.Keys, StringComparer.OrdinalIgnoreCase);

            foreach (string dir in Directory.GetDirectories(recPath, ".rec_*"))
            {
                string dirName = Path.GetFileName(dir);
                string timerId = dirName.StartsWith(".rec_", StringComparison.Ordinal)
                    ? dirName.Substring(5)
                    : dirName;

                // Skip directories belonging to active recordings
                if (activeTimerIds.Contains(timerId))
                {
                    continue;
                }

                _logger.LogInformation("Cleaning up orphaned segment directory: {Path}", dir);
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up segment directory: {Dir}", dir);
                }
            }

            // Clean up any recovered_*.mkv files from previous merges
            foreach (string mkvFile in Directory.GetFiles(recPath, "recovered_*" + FinalExtension))
            {
                _logger.LogInformation("Cleaning up recovered file: {Path}", mkvFile);
                try
                {
                    File.Delete(mkvFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up recovered file: {Path}", mkvFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cleaning up orphaned recording artifacts");
        }
    }

    private static string GetFfmpegPath()
    {
        // Jellyfin Docker images bundle ffmpeg here
        string jellyfinFfmpeg = "/usr/lib/jellyfin-ffmpeg/ffmpeg";
        if (File.Exists(jellyfinFfmpeg))
        {
            return jellyfinFfmpeg;
        }

        // Fall back to system ffmpeg
        return "ffmpeg";
    }
}
