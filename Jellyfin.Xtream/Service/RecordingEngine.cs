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
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RecordingEngine> _logger;
    private readonly LiveTvService _liveTvService;
    private readonly IServerConfigurationManager _config;
    private readonly IRecordingsManager _recordingsManager;
    private readonly ConcurrentDictionary<string, ActiveRecording> _activeRecordings = new();
    private readonly ConcurrentDictionary<string, CompletedRecording> _completedRecordings = new();
    private ConcurrentDictionary<string, ActiveRecordingInfo>? _jellyfinActiveRecordings;
    private Timer? _pollTimer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingEngine"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{RecordingEngine}"/> interface.</param>
    /// <param name="liveTvService">Instance of the <see cref="LiveTvService"/> class.</param>
    /// <param name="config">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="recordingsManager">Instance of the <see cref="IRecordingsManager"/> interface.</param>
    public RecordingEngine(IHttpClientFactory httpClientFactory, ILogger<RecordingEngine> logger, LiveTvService liveTvService, IServerConfigurationManager config, IRecordingsManager recordingsManager)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _liveTvService = liveTvService;
        _config = config;
        _recordingsManager = recordingsManager;

        // Access RecordingsManager's internal _activeRecordings via reflection
        // so that Video.IsActiveRecording() recognizes our recordings.
        var field = _recordingsManager.GetType().GetField("_activeRecordings", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null)
        {
            _jellyfinActiveRecordings = field.GetValue(_recordingsManager) as ConcurrentDictionary<string, ActiveRecordingInfo>;
        }

        if (_jellyfinActiveRecordings == null)
        {
            _logger.LogWarning("Could not access RecordingsManager._activeRecordings via reflection. Active recordings may not show in UI.");
        }
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
        string fileName = $"{timer.Name}_{timer.StartDate:yyyyMMdd_HHmmss}.ts"
            .Replace(' ', '_')
            .Replace('/', '-')
            .Replace('\\', '-');
        string filePath = Path.Combine(RecordingsPath, fileName);

        _logger.LogInformation("Recording started: {Name} -> {Path}", timer.Name, filePath);
        recording.FilePath = filePath;

        // Register with Jellyfin's RecordingsManager so Video.IsActiveRecording() returns true
        RegisterActiveRecording(timer.Id, filePath, timer, recording.CancellationTokenSource);

        try
        {
            // Build the stream URL (same logic as Restream)
            PluginConfiguration config = Plugin.Instance.Configuration;
            Guid guid = Guid.Parse(timer.ChannelId);
            StreamService.FromGuid(guid, out int _, out int streamId, out int _, out int _);
            string url = $"{config.BaseUrl}/{config.Username}/{config.Password}/{streamId}";

            using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, recording.CancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync(recording.CancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read, 81920);
                await using (fileStream.ConfigureAwait(false))
                {
                    var buffer = new byte[65536];
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer, recording.CancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), recording.CancellationToken).ConfigureAwait(false);
                    }
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
            // Unregister from Jellyfin's RecordingsManager
            UnregisterActiveRecording(timer.Id);

            _activeRecordings.TryRemove(timer.Id, out _);

            if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
            {
                timer.Status = RecordingStatus.Completed;
                _liveTvService.UpdateTimerStatus(timer);
                _completedRecordings[timer.Id] = new CompletedRecording
                {
                    TimerId = timer.Id,
                    Timer = timer,
                    FilePath = filePath,
                    CompletedAt = DateTime.UtcNow,
                };
                _logger.LogInformation("Recording completed: {Name} ({Size} bytes)", timer.Name, new FileInfo(filePath).Length);
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

    private void RegisterActiveRecording(string id, string path, TimerInfo timer, CancellationTokenSource cts)
    {
        if (_jellyfinActiveRecordings == null)
        {
            return;
        }

        var info = new ActiveRecordingInfo
        {
            Id = id,
            Path = path,
            Timer = timer,
            CancellationTokenSource = cts,
        };

        _jellyfinActiveRecordings[id] = info;
        _logger.LogDebug("Registered active recording with RecordingsManager: {Path}", path);
    }

    private void UnregisterActiveRecording(string id)
    {
        _jellyfinActiveRecordings?.TryRemove(id, out _);
    }
}
