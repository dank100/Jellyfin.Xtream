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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Configuration;
using MediaBrowser.Common.Net;
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
    private const string RecordingExtension = ".ts";
    private const string FinalExtension = ".mkv";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RecordingEngine> _logger;
    private readonly LiveTvService _liveTvService;
    private readonly IServerConfigurationManager _config;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ConcurrentDictionary<string, ActiveRecording> _activeRecordings = new();
    private readonly ConcurrentDictionary<string, CompletedRecording> _completedRecordings = new();
    private Timer? _pollTimer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingEngine"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{RecordingEngine}"/> interface.</param>
    /// <param name="liveTvService">Instance of the <see cref="LiveTvService"/> class.</param>
    /// <param name="config">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    public RecordingEngine(IHttpClientFactory httpClientFactory, ILogger<RecordingEngine> logger, LiveTvService liveTvService, IServerConfigurationManager config, ILibraryMonitor libraryMonitor)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _liveTvService = liveTvService;
        _config = config;
        _libraryMonitor = libraryMonitor;
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
        string fileName = $"{timer.Name}_{timer.StartDate:yyyyMMdd_HHmmss}{RecordingExtension}"
            .Replace(' ', '_')
            .Replace('/', '-')
            .Replace('\\', '-');
        string filePath = Path.Combine(RecordingsPath, fileName);

        _logger.LogInformation("Recording started: {Name} -> {Path}", timer.Name, filePath);
        recording.FilePath = filePath;

        try
        {
            // Build the stream URL (same logic as Restream)
            PluginConfiguration config = Plugin.Instance.Configuration;
            Guid guid = Guid.Parse(timer.ChannelId);
            StreamService.FromGuid(guid, out int _, out int streamId, out int _, out int _);
            string url = $"{config.BaseUrl}/{config.Username}/{config.Password}/{streamId}";

            var ffmpegPath = GetFfmpegPath();
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-i \"{url}\" -map 0 -dn -sn -ignore_unknown -fflags +genpts+igndts -c:v copy -c:a aac -b:a 192k -f mpegts -y \"{filePath}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };

            if (!string.IsNullOrEmpty(config.UserAgent))
            {
                psi.Arguments = $"-user_agent \"{config.UserAgent}\" " + psi.Arguments;
            }

            _logger.LogInformation("Starting ffmpeg: {Args}", psi.Arguments);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ffmpeg process");

            // Ask ffmpeg to quit cleanly so the transport stream is flushed before remuxing.
            recording.CancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.StandardInput.WriteLine("q");
                        process.StandardInput.Flush();

                        if (!process.WaitForExit(5000))
                        {
                            process.Kill(true);
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            });

            // Read stderr for logging (ffmpeg writes progress to stderr)
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardError.ReadLineAsync(recording.CancellationToken).ConfigureAwait(false)) != null)
                    {
                        _logger.LogDebug("ffmpeg: {Line}", line);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < 10; i++)
                    {
                        await Task.Delay(1000, recording.CancellationToken).ConfigureAwait(false);
                        if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                        {
                            _libraryMonitor.ReportFileSystemChanged(filePath);
                            TriggerMediaScan();
                            _logger.LogInformation("Library notified of new recording: {Path}", filePath);
                            return;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            if (process.ExitCode != 0 && !recording.CancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("ffmpeg exited with code {Code}", process.ExitCode);
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
            _activeRecordings.TryRemove(timer.Id, out _);

            if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
            {
                // Remux from MPEG-TS to MKV for better seeking and library support
                string finalPath = Path.ChangeExtension(filePath, FinalExtension);
                string completedPath = await RemuxToMkvAsync(filePath, finalPath).ConfigureAwait(false);

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
            }
            else
            {
                timer.Status = RecordingStatus.Error;
                _liveTvService.UpdateTimerStatus(timer);
                _logger.LogWarning("Recording produced no output: {Name}", timer.Name);
            }

            recording.Dispose();
        }
    }

    /// <summary>
    /// Remuxes a MPEG-TS recording to MKV for better seeking and library support.
    /// Returns the final file path (MKV on success, original TS on failure).
    /// </summary>
    private async Task<string> RemuxToMkvAsync(string tsPath, string mkvPath)
    {
        var ffmpegPath = GetFfmpegPath();
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-i \"{tsPath}\" -map 0 -c copy -f matroska -y \"{mkvPath}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _logger.LogInformation("Remuxing recording to MKV: {Source} -> {Dest}", tsPath, mkvPath);

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ffmpeg for remux");

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            if (process.ExitCode == 0 && File.Exists(mkvPath) && new FileInfo(mkvPath).Length > 0)
            {
                File.Delete(tsPath);
                _logger.LogInformation("Remux completed successfully: {Path}", mkvPath);
                return mkvPath;
            }

            _logger.LogWarning("Remux failed (exit code {Code}), keeping TS file", process.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remux failed, keeping TS file");
        }

        return tsPath;
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
