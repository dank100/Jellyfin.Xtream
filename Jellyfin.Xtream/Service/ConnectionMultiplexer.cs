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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Background service that round-robins a single IPTV connection across
/// multiple subscribed channels, capturing short bursts of TS data per channel
/// and storing them as HLS segments.
/// </summary>
public sealed class ConnectionMultiplexer : IHostedService, IDisposable
{
    private readonly ILogger<ConnectionMultiplexer> _logger;
    private readonly IServerConfigurationManager _config;
    private readonly IXtreamClient _xtreamClient;
    private readonly ConcurrentDictionary<int, ChannelBuffer> _channels = new();
    private readonly ConcurrentDictionary<int, (Task Task, CancellationTokenSource Cts)> _parallelCaptures = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _disposed;
    private int _maxConnections = 1;
    private bool _maxConnectionsFetched;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionMultiplexer"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{ConnectionMultiplexer}"/> interface.</param>
    /// <param name="config">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="xtreamClient">Instance of the <see cref="IXtreamClient"/> interface.</param>
    public ConnectionMultiplexer(ILogger<ConnectionMultiplexer> logger, IServerConfigurationManager config, IXtreamClient xtreamClient)
    {
        _logger = logger;
        _config = config;
        _xtreamClient = xtreamClient;
    }

    /// <summary>
    /// Gets the base directory for multiplexer segment storage.
    /// </summary>
    private string BaseDir
    {
        get
        {
            string path = Path.Combine(_config.CommonApplicationPaths.DataPath, "livetv", "multiplex");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>
    /// Subscribes to a channel. Creates the buffer if needed and increments the
    /// subscriber count. The channel enters Buffering state until the first segment
    /// is captured.
    /// </summary>
    /// <param name="streamId">The Xtream stream ID.</param>
    /// <param name="isLive">True for live TV viewers that need real-time delivery; false for recordings.</param>
    /// <returns>The channel buffer.</returns>
    public ChannelBuffer Subscribe(int streamId, bool isLive = false)
    {
        var buffer = _channels.GetOrAdd(streamId, id =>
        {
            _logger.LogInformation("Creating multiplexer buffer for stream {StreamId}", id);
            return new ChannelBuffer(id, BaseDir);
        });

        buffer.SubscriberCount++;
        if (isLive)
        {
            buffer.LiveViewerCount++;
        }

        if (buffer.State == ChannelBufferState.Idle)
        {
            buffer.State = ChannelBufferState.Buffering;
        }

        _logger.LogInformation(
            "Subscribed to stream {StreamId}, subscribers: {Count}, live: {Live}",
            streamId,
            buffer.SubscriberCount,
            buffer.LiveViewerCount);
        return buffer;
    }

    /// <summary>
    /// Unsubscribes from a channel. When the subscriber count reaches zero, the
    /// channel moves to Idle and its segments are cleaned up.
    /// </summary>
    /// <param name="streamId">The Xtream stream ID.</param>
    /// <param name="isLive">Must match the value used in Subscribe.</param>
    public void Unsubscribe(int streamId, bool isLive = false)
    {
        if (!_channels.TryGetValue(streamId, out var buffer))
        {
            return;
        }

        buffer.SubscriberCount--;
        if (isLive)
        {
            buffer.LiveViewerCount = Math.Max(0, buffer.LiveViewerCount - 1);
        }

        _logger.LogInformation(
            "Unsubscribed from stream {StreamId}, subscribers: {Count}, live: {Live}",
            streamId,
            buffer.SubscriberCount,
            buffer.LiveViewerCount);

        if (buffer.SubscriberCount <= 0)
        {
            buffer.SubscriberCount = 0;
            buffer.State = ChannelBufferState.Idle;

            // Stop any parallel capture running for this channel.
            StopParallelCapture(streamId);

            if (_channels.TryRemove(streamId, out var removed))
            {
                removed.Dispose();
                _logger.LogInformation("Removed idle buffer for stream {StreamId}", streamId);
            }
        }
    }

    /// <summary>
    /// Gets the channel buffer for a stream, or null if not subscribed.
    /// </summary>
    /// <param name="streamId">The Xtream stream ID.</param>
    /// <returns>The buffer, or null.</returns>
    public ChannelBuffer? GetBuffer(int streamId)
    {
        _channels.TryGetValue(streamId, out var buffer);
        return buffer;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting stream multiplexer background loop");
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RoundRobinLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_loopTask != null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
        }

        // Stop all parallel captures
        StopAllParallelCaptures();

        // Clean up all buffers
        foreach (var kvp in _channels)
        {
            kvp.Value.Dispose();
        }

        _channels.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAllParallelCaptures();
        _cts?.Dispose();

        foreach (var kvp in _channels)
        {
            kvp.Value.Dispose();
        }

        _channels.Clear();
    }

    /// <summary>
    /// The main round-robin loop. Iterates over subscribed channels, capturing a
    /// short burst from each one.
    /// </summary>
    private async Task RoundRobinLoopAsync(CancellationToken ct)
    {
        try
        {
        while (!ct.IsCancellationRequested)
        {
            try
            {
            var activeChannels = _channels
                .Where(kvp => kvp.Value.SubscriberCount > 0)
                .Select(kvp => kvp.Value)
                .ToList();

            if (activeChannels.Count == 0)
            {
                // No subscribers — wait a bit before checking again
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }

            // Lazily fetch max connections on first active subscription
            if (!_maxConnectionsFetched)
            {
                _maxConnectionsFetched = true;
                try
                {
                    var connectionInfo = Plugin.Instance.Creds;
                    var info = await _xtreamClient.GetUserAndServerInfoAsync(connectionInfo, ct).ConfigureAwait(false);
                    _maxConnections = Math.Max(1, info.UserInfo.MaxConnections);
                    _logger.LogInformation("Provider max connections: {MaxConn}", _maxConnections);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not fetch provider info for max connections, defaulting to {Max}", _maxConnections);
                }
            }

            PluginConfiguration pluginConfig = Plugin.Instance.Configuration;
            int sliceSeconds = Math.Max(1, pluginConfig.MultiplexSliceSeconds);
            int retentionSeconds = Math.Max(10, pluginConfig.MultiplexRetentionSeconds);

            if (activeChannels.Count <= _maxConnections)
            {
                // Enough connections for all — use persistent parallel captures.
                // Start a capture task for any channel that doesn't already have one.
                var activeIds = new HashSet<int>(activeChannels.Select(b => b.StreamId));

                // Clean up completed/stale capture tasks first.
                foreach (var sid in _parallelCaptures.Keys.ToList())
                {
                    if (_parallelCaptures.TryGetValue(sid, out var existing) && existing.Task.IsCompleted)
                    {
                        _parallelCaptures.TryRemove(sid, out _);
                        existing.Cts.Dispose();
                    }
                }

                foreach (var buffer in activeChannels)
                {
                    int sid = buffer.StreamId;
                    if (!_parallelCaptures.ContainsKey(sid))
                    {
                        var captureCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var task = Task.Run(
                            () => ParallelCaptureLoopAsync(buffer, sliceSeconds, retentionSeconds, captureCts.Token),
                            captureCts.Token);
                        _parallelCaptures[sid] = (task, captureCts);
                        _logger.LogInformation("Started parallel capture task for stream {StreamId}", sid);
                    }
                }

                // Stop captures for channels that are no longer active.
                foreach (var sid in _parallelCaptures.Keys.ToList())
                {
                    if (!activeIds.Contains(sid))
                    {
                        StopParallelCapture(sid);
                    }
                }

                // Sleep briefly then re-check for channel changes.
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            else
            {
                // More channels than connections — transition to sequential round-robin.
                // Stop parallel captures gracefully (they'll finish current work).
                StopAllParallelCaptures();

                // Wait briefly for parallel captures to wind down and produce
                // their last segments, minimizing the transition gap.
                await Task.Delay(500, ct).ConfigureAwait(false);

                // Prioritize channels with the oldest data (fewest recent segments).
                // This ensures newly-joined channels get data first during the
                // parallel→sequential transition, minimizing their startup gap.
                var orderedChannels = activeChannels
                    .OrderBy(b => b.GetSegments().Count)
                    .ThenByDescending(b => b.StreamId)
                    .ToList();

                foreach (var buffer in orderedChannels)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        int produced = await CaptureStreamAsync(buffer, sliceSeconds, retentionSeconds, ct).ConfigureAwait(false);
                        if (produced == 0)
                        {
                            _logger.LogWarning("Capture for stream {StreamId} produced 0 segments, skipping turn", buffer.StreamId);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error in continuous capture for stream {StreamId}", buffer.StreamId);
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                    }
                }
            }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Round-robin loop iteration error, resuming in 2s");
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Round-robin loop CRASHED — captures will not restart until plugin reload");
        }

        _logger.LogInformation("Round-robin loop exited");
    }

    /// <summary>
    /// Long-running capture loop for a single channel in parallel mode.
    /// Restarts ffmpeg if it exits unexpectedly.
    /// </summary>
    private async Task ParallelCaptureLoopAsync(ChannelBuffer buffer, int sliceSeconds, int retentionSeconds, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && buffer.SubscriberCount > 0)
        {
            try
            {
                await CaptureStreamAsync(buffer, sliceSeconds, retentionSeconds, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Parallel capture error for stream {StreamId}, restarting in 2s", buffer.StreamId);
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Parallel capture loop ended for stream {StreamId}", buffer.StreamId);
    }

    private void StopParallelCapture(int streamId)
    {
        if (_parallelCaptures.TryRemove(streamId, out var entry))
        {
            _logger.LogInformation("Stopping parallel capture for stream {StreamId}", streamId);
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }
    }

    private void StopAllParallelCaptures()
    {
        foreach (var sid in _parallelCaptures.Keys.ToList())
        {
            StopParallelCapture(sid);
        }
    }

    /// <summary>
    /// Runs a continuous ffmpeg capture for a channel using the segment muxer.
    /// One persistent connection produces segment files automatically.
    /// Runs until cancelled, the stream ends, or we need to yield to other channels.
    /// </summary>
    private async Task<int> CaptureStreamAsync(ChannelBuffer buffer, int sliceSeconds, int retentionSeconds, CancellationToken ct)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        string url = $"{config.BaseUrl}/{config.Username}/{config.Password}/{buffer.StreamId}";

        string captureId = Guid.NewGuid().ToString("N")[..8];
        string segPattern = Path.Combine(buffer.SegmentDir, $"c{captureId}_%05d.ts");
        string segListPath = Path.Combine(buffer.SegmentDir, $"c{captureId}_list.txt");

        var ffmpegPath = GetFfmpegPath();
        string userAgentArg = string.IsNullOrEmpty(config.UserAgent)
            ? string.Empty
            : $"-user_agent \"{config.UserAgent}\" ";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-fflags +nobuffer -analyzeduration 500000 -probesize 500000 "
                + $"{userAgentArg}-i \"{url}\""
                + " -map 0 -dn -sn -c copy"
                + $" -f segment -segment_time {sliceSeconds} -segment_format mpegts"
                + $" -segment_list \"{segListPath}\" -segment_list_type csv"
                + $" -y \"{segPattern}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        _logger.LogInformation(
            "Starting continuous capture for stream {StreamId}: {Args}",
            buffer.StreamId,
            psi.Arguments);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg for continuous capture");

        ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.Write('q');
                }
            }
            catch
            {
                // Process may have already exited
            }
        });

        // Drain stderr to prevent buffer deadlock.
        var stderrTask = Task.Run(
            async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                    {
                        _logger.LogDebug("Capture stderr [{StreamId}]: {Line}", buffer.StreamId, line);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            },
            ct);

        // Monitor the segment list file for completed segments.
        int completedCount = 0;
        bool firstSegment = true;
        try
        {
            while (!ct.IsCancellationRequested && !process.HasExited)
            {
                // Read the CSV segment list written by ffmpeg.
                // CSV format: filename,start_time,end_time
                int newCount = 0;
                double[] segDurations = Array.Empty<double>();
                if (File.Exists(segListPath))
                {
                    try
                    {
                        var lines = await File.ReadAllLinesAsync(segListPath, ct).ConfigureAwait(false);
                        var validLines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                        newCount = validLines.Length;
                        segDurations = new double[newCount];
                        for (int li = 0; li < newCount; li++)
                        {
                            var parts = validLines[li].Split(',');
                            if (parts.Length >= 3
                                && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double start)
                                && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double end))
                            {
                                segDurations[li] = Math.Max(0.5, end - start);
                            }
                            else
                            {
                                segDurations[li] = sliceSeconds;
                            }
                        }
                    }
                    catch (IOException)
                    {
                        // File may be locked by ffmpeg writing it
                    }
                }

                // Process newly completed segments.
                // ffmpeg's segment list CSV writes each line when the segment is
                // finalized, so all listed segments are safe to process.
                // The file existence + size check below guards against edge cases.
                int safeCount = newCount;
                bool shouldYield = false;

                // Pre-compute yield parameters once per poll cycle.
                int activeCount = _channels.Count(kvp => kvp.Value.SubscriberCount > 0);
                bool needsYield = activeCount > _maxConnections;
                const int minSegments = 3;

                for (int i = completedCount; i < safeCount; i++)
                {
                    string segFilename = $"c{captureId}_{i:D5}.ts";
                    string segPath = Path.Combine(buffer.SegmentDir, segFilename);

                    if (!File.Exists(segPath) || new FileInfo(segPath).Length == 0)
                    {
                        continue;
                    }

                    var segment = new SegmentInfo
                    {
                        Filename = segFilename,
                        DurationSeconds = i < segDurations.Length ? segDurations[i] : sliceSeconds,
                        CapturedUtc = DateTime.UtcNow,
                        IsDiscontinuity = firstSegment && buffer.GetSegments().Count > 0,
                    };

                    buffer.AddSegment(segment, segment.IsDiscontinuity);
                    firstSegment = false;
                    completedCount = i + 1;

                    _logger.LogInformation(
                        "Continuous capture: segment {Filename} ready for stream {StreamId}",
                        segFilename,
                        buffer.StreamId);

                    // Yield after minSegments to let other channels capture.
                    if (needsYield && completedCount >= minSegments)
                    {
                        _logger.LogInformation(
                            "Yielding capture for stream {StreamId} after {Count} segments (live={IsLive})",
                            buffer.StreamId,
                            completedCount,
                            buffer.LiveViewerCount > 0);
                        shouldYield = true;
                        break;
                    }
                }

                if (shouldYield)
                {
                    break;
                }

                // Do NOT advance completedCount past unprocessed segments.
                // If a segment file was temporarily unreadable, the next poll
                // will retry it. Only the for-loop above advances completedCount
                // on successful processing (line: completedCount = i + 1).

                // Periodic pruning
                buffer.PruneSegments(retentionSeconds);

                await Task.Delay(100, ct).ConfigureAwait(false);
            }

            // When ffmpeg exits via yield/cancel, the loop may exit before
            // the last segment is processed. Final segment processing is
            // deferred to after WaitForExitAsync to ensure ffmpeg has
            // finished writing.
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    // Use sync Write here since we're in finally block cleanup
#pragma warning disable CA1849
                    process.StandardInput.Write('q');
#pragma warning restore CA1849
                }
                catch
                {
                    // Ignore
                }

                using var exitCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                try
                {
                    await process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    process.Kill();
                }
            }

            // Now that ffmpeg has fully exited, process any remaining segments.
            // The last listed segment is safe to consume because ffmpeg has
            // closed the file and flushed the segment list.
            bool gracefulExit = process.HasExited && process.ExitCode == 0;
            if (File.Exists(segListPath))
            {
                try
                {
                    var finalLines = (await File.ReadAllLinesAsync(segListPath, CancellationToken.None).ConfigureAwait(false))
                        .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                    int finalCount = gracefulExit ? finalLines.Length : Math.Max(0, finalLines.Length - 1);

                    // Parse CSV durations from final segment list
                    var finalDurations = new double[finalLines.Length];
                    for (int li = 0; li < finalLines.Length; li++)
                    {
                        var parts = finalLines[li].Split(',');
                        if (parts.Length >= 3
                            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double start)
                            && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double end))
                        {
                            finalDurations[li] = Math.Max(0.5, end - start);
                        }
                        else
                        {
                            finalDurations[li] = sliceSeconds;
                        }
                    }

                    for (int i = completedCount; i < finalCount; i++)
                    {
                        string segFilename = $"c{captureId}_{i:D5}.ts";
                        string segPath = Path.Combine(buffer.SegmentDir, segFilename);

                        if (File.Exists(segPath) && new FileInfo(segPath).Length > 0)
                        {
                            var segment = new SegmentInfo
                            {
                                Filename = segFilename,
                                DurationSeconds = i < finalDurations.Length ? finalDurations[i] : sliceSeconds,
                                CapturedUtc = DateTime.UtcNow,
                                IsDiscontinuity = firstSegment && buffer.GetSegments().Count > 0,
                            };

                            buffer.AddSegment(segment, segment.IsDiscontinuity);
                            firstSegment = false;
                            completedCount = i + 1;
                        }
                    }
                }
                catch (IOException)
                {
                    // Best effort
                }
            }

            try
            {
                await stderrTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore
            }

            // Clean up the segment list file
            if (File.Exists(segListPath))
            {
                try
                {
                    File.Delete(segListPath);
                }
                catch (IOException)
                {
                    // Best effort
                }
            }

            _logger.LogInformation(
                "Continuous capture for stream {StreamId} finished: {Count} segments produced",
                buffer.StreamId,
                completedCount);
        }

        return completedCount;
    }

    private static string GetFfmpegPath()
    {
        string jellyfinFfmpeg = "/usr/lib/jellyfin-ffmpeg/ffmpeg";
        if (File.Exists(jellyfinFfmpeg))
        {
            return jellyfinFfmpeg;
        }

        return "ffmpeg";
    }
}
