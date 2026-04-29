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
using System.Net.Http.Headers;
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
    /// <summary>
    /// Grace period in seconds before cleaning up after the last subscriber leaves.
    /// Allows Jellyfin's probe-then-play cycle to reuse the existing buffer.
    /// </summary>
    private const int GracePeriodSeconds = 30;

    private readonly ILogger<ConnectionMultiplexer> _logger;
    private readonly IServerConfigurationManager _config;
    private readonly IXtreamClient _xtreamClient;
    private readonly ConcurrentDictionary<int, ChannelBuffer> _channels = new();
    private readonly ConcurrentDictionary<int, (Task Task, CancellationTokenSource Cts)> _parallelCaptures = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _gracePeriodTimers = new();
    private readonly ConcurrentDictionary<int, PersistentCapture> _persistentCaptures = new();
    private readonly Lazy<HttpClient> _httpClient = new(() =>
    {
        var client = new HttpClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    });

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
        // Cancel any pending grace-period cleanup for this channel.
        if (_gracePeriodTimers.TryRemove(streamId, out var graceCts))
        {
            graceCts.Cancel();
            graceCts.Dispose();
            _logger.LogInformation("Cancelled grace period cleanup for stream {StreamId} (re-subscribed)", streamId);
        }

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

            // Start a grace period instead of immediately cleaning up.
            // Jellyfin's live TV pipeline probes then reopens streams, so
            // keeping the capture alive avoids a 3+ second delay on the retry.
            var graceCts = new CancellationTokenSource();
            if (_gracePeriodTimers.TryAdd(streamId, graceCts))
            {
                _logger.LogInformation(
                    "Starting {Seconds}s grace period for stream {StreamId}",
                    GracePeriodSeconds,
                    streamId);
                _ = RunGracePeriodCleanupAsync(streamId, graceCts.Token);
            }
        }
    }

    private async Task RunGracePeriodCleanupAsync(int streamId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(GracePeriodSeconds), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A new subscriber arrived during the grace period — keep the buffer.
            return;
        }

        // Grace period expired with no new subscribers — clean up.
        if (!_channels.TryGetValue(streamId, out var buffer) || buffer.SubscriberCount > 0)
        {
            return;
        }

        _logger.LogInformation("Grace period expired for stream {StreamId}, cleaning up", streamId);
        buffer.State = ChannelBufferState.Idle;
        StopParallelCapture(streamId);
        await StopPersistentCaptureAsync(streamId).ConfigureAwait(false);

        if (_channels.TryRemove(streamId, out var removed))
        {
            removed.Dispose();
            _logger.LogInformation("Removed idle buffer for stream {StreamId}", streamId);
        }

        _gracePeriodTimers.TryRemove(streamId, out _);
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

        // Cancel all pending grace-period timers.
        foreach (var kvp in _gracePeriodTimers)
        {
            await kvp.Value.CancelAsync().ConfigureAwait(false);
            kvp.Value.Dispose();
        }

        _gracePeriodTimers.Clear();

        // Stop all parallel captures
        StopAllParallelCaptures();

        // Stop all persistent captures (await ffmpeg exit before deleting dirs)
        foreach (var kvp in _persistentCaptures)
        {
            await kvp.Value.StopAsync().ConfigureAwait(false);
            kvp.Value.Dispose();
        }

        _persistentCaptures.Clear();

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

        // Cancel all pending grace-period timers.
        foreach (var kvp in _gracePeriodTimers)
        {
#pragma warning disable CA1849 // Dispose is synchronous; CancelAsync not available here
            kvp.Value.Cancel();
#pragma warning restore CA1849
            kvp.Value.Dispose();
        }

        _gracePeriodTimers.Clear();

        StopAllParallelCaptures();

        foreach (var kvp in _persistentCaptures)
        {
            kvp.Value.Dispose();
        }

        _persistentCaptures.Clear();

        _cts?.Dispose();

        if (_httpClient.IsValueCreated)
        {
            _httpClient.Value.Dispose();
        }

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

                // Stop captures for channels that are no longer active
                // (but not those in a grace period — they'll be cleaned up later).
                foreach (var sid in _parallelCaptures.Keys.ToList())
                {
                    if (!activeIds.Contains(sid) && !_gracePeriodTimers.ContainsKey(sid))
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

                    // Skip channels that lost all subscribers during this iteration
                    // (e.g., unsubscribed while another channel was being captured).
                    if (buffer.SubscriberCount <= 0 || !_channels.ContainsKey(buffer.StreamId))
                    {
                        _logger.LogDebug(
                            "Skipping capture for stream {StreamId} — no subscribers or buffer removed",
                            buffer.StreamId);
                        continue;
                    }

                    try
                    {
                        int produced = await CaptureStreamAsync(buffer, sliceSeconds, retentionSeconds, ct, stopOnNoSubscribers: true).ConfigureAwait(false);
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
    /// Stops and disposes the persistent capture for a stream, waiting for
    /// ffmpeg to exit before returning so files can be safely deleted.
    /// </summary>
    private async Task StopPersistentCaptureAsync(int streamId)
    {
        if (_persistentCaptures.TryRemove(streamId, out var capture))
        {
            _logger.LogDebug("Stopping persistent capture for stream {StreamId}", streamId);
            await capture.StopAsync().ConfigureAwait(false);
            capture.Dispose();
        }
    }

    private PersistentCapture GetOrCreatePersistentCapture(ChannelBuffer buffer, int sliceSeconds)
    {
        return _persistentCaptures.GetOrAdd(buffer.StreamId, _ => CreatePersistentCapture(buffer, sliceSeconds));
    }

    private PersistentCapture CreatePersistentCapture(ChannelBuffer buffer, int sliceSeconds)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        var ffmpegPath = GetFfmpegPath();

        string captureId = Guid.NewGuid().ToString("N")[..9];
        string segPattern = Path.Combine(buffer.SegmentDir, $"c{captureId}_%05d.ts");
        string segListPath = Path.Combine(buffer.SegmentDir, $"c{captureId}_list.txt");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = "-f mpegts -fflags +nobuffer+discardcorrupt"
                + " -i pipe:0"
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
            "Starting persistent ffmpeg for stream {StreamId}: {Args}",
            buffer.StreamId,
            psi.Arguments);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg for persistent capture");

        var capture = new PersistentCapture
        {
            Process = process,
            CaptureId = captureId,
            SegListPath = segListPath,
            CompletedCount = 0,
            FirstSegment = true,
        };

        // Drain stderr to prevent buffer deadlock.
        capture.StderrTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    _logger.LogDebug("Capture stderr [{StreamId}]: {Line}", buffer.StreamId, line);
                }
            }
            catch
            {
                // Process exited or stream closed.
            }
        });

        return capture;
    }

    /// <summary>
    /// Captures TS data for a channel using a persistent ffmpeg process.
    /// Opens an HTTP connection to the IPTV source and forwards data to
    /// ffmpeg's stdin pipe. The ffmpeg process is reused across calls,
    /// eliminating the ~4s startup overhead per burst.
    /// </summary>
    private async Task<int> CaptureStreamAsync(ChannelBuffer buffer, int sliceSeconds, int retentionSeconds, CancellationToken ct, bool stopOnNoSubscribers = false)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        string url = $"{config.BaseUrl}/{config.Username}/{config.Password}/{buffer.StreamId}";

        // Get or create the persistent ffmpeg process for this channel.
        var capture = GetOrCreatePersistentCapture(buffer, sliceSeconds);

        // If the persistent process has exited (crash, pipe error), recreate it.
        if (capture.Process == null || capture.Process.HasExited)
        {
            _logger.LogWarning(
                "Persistent ffmpeg for stream {StreamId} exited unexpectedly, recreating",
                buffer.StreamId);
            _persistentCaptures.TryRemove(buffer.StreamId, out _);
            capture.Dispose();
            capture = GetOrCreatePersistentCapture(buffer, sliceSeconds);
        }

        // Open HTTP connection to the IPTV source.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(config.UserAgent))
        {
            request.Headers.UserAgent.TryParseAdd(config.UserAgent);
        }

        HttpResponseMessage? response = null;
        Stream? upstream = null;
        try
        {
            response = await _httpClient.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            upstream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open upstream for stream {StreamId}", buffer.StreamId);
            response?.Dispose();
            return 0;
        }

        _logger.LogInformation(
            "Opened upstream for stream {StreamId}, forwarding to persistent ffmpeg (captureId={CaptureId})",
            buffer.StreamId,
            capture.CaptureId);

        // Background task: forward upstream TS data → ffmpeg stdin.
        using var forwardCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stdinStream = capture.Process!.StandardInput.BaseStream;
        var forwardTask = Task.Run(
            async () =>
            {
                byte[] buf = new byte[65536];
                try
                {
                    while (!forwardCts.Token.IsCancellationRequested)
                    {
                        int read = await upstream.ReadAsync(buf, forwardCts.Token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break; // Upstream closed.
                        }

                        await stdinStream.WriteAsync(buf.AsMemory(0, read), forwardCts.Token).ConfigureAwait(false);
                        await stdinStream.FlushAsync(forwardCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal yield.
                }
                catch (IOException)
                {
                    // Upstream or pipe closed.
                }
            },
            forwardCts.Token);

        // Monitor the segment list file for completed segments.
        int startCount = capture.CompletedCount;
        int segmentsThisBurst = 0;
        try
        {
            while (!ct.IsCancellationRequested && !capture.Process.HasExited)
            {
                // Read the CSV segment list written by ffmpeg.
                int newCount = 0;
                double[] segDurations = Array.Empty<double>();
                if (File.Exists(capture.SegListPath))
                {
                    try
                    {
                        var lines = await File.ReadAllLinesAsync(capture.SegListPath, ct).ConfigureAwait(false);
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
                        // File may be locked by ffmpeg writing it.
                    }
                }

                // Process newly completed segments.
                int safeCount = newCount;
                bool shouldYield = false;

                int activeCount = _channels.Count(kvp => kvp.Value.SubscriberCount > 0);
                bool needsYield = activeCount > _maxConnections;

                // With persistent ffmpeg (near-zero per-burst overhead),
                // 3 segments (6s data) per burst is sufficient: each capture
                // takes ~2s, so a 2-channel round is ~4s → rate ≈ 1.5x.
                const int minSegments = 3;

                for (int i = capture.CompletedCount; i < safeCount; i++)
                {
                    string segFilename = $"c{capture.CaptureId}_{i:D5}.ts";
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
                        IsDiscontinuity = capture.FirstSegment && buffer.GetSegments().Count > 0,
                    };

                    buffer.AddSegment(segment, segment.IsDiscontinuity);
                    capture.FirstSegment = false;
                    capture.CompletedCount = i + 1;
                    segmentsThisBurst++;

                    _logger.LogInformation(
                        "Continuous capture: segment {Filename} ready for stream {StreamId}",
                        segFilename,
                        buffer.StreamId);

                    // Yield after minSegments to let other channels capture.
                    if (needsYield && segmentsThisBurst >= minSegments)
                    {
                        _logger.LogInformation(
                            "Yielding capture for stream {StreamId} after {Count} segments (live={IsLive})",
                            buffer.StreamId,
                            segmentsThisBurst,
                            buffer.LiveViewerCount > 0);
                        shouldYield = true;
                        break;
                    }
                }

                if (shouldYield)
                {
                    break;
                }

                // In sequential mode, stop capturing if subscribers left.
                if (stopOnNoSubscribers && buffer.SubscriberCount <= 0)
                {
                    _logger.LogInformation(
                        "Stopping capture for stream {StreamId} — no subscribers remain",
                        buffer.StreamId);
                    break;
                }

                buffer.PruneSegments(retentionSeconds);

                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation.
        }
        finally
        {
            // Stop forwarding but keep ffmpeg alive for next burst.
            await forwardCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await forwardTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore forward task exceptions.
            }

            await upstream.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
        }

        _logger.LogInformation(
            "Capture burst for stream {StreamId} finished: {Count} segments this burst, {Total} total",
            buffer.StreamId,
            segmentsThisBurst,
            capture.CompletedCount);

        return segmentsThisBurst;
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

    /// <summary>
    /// Holds a persistent ffmpeg process that reads MPEG-TS from stdin.
    /// The process is reused across multiple capture bursts for the same channel,
    /// eliminating the ~4s startup overhead (process start, analyzeduration,
    /// keyframe wait) per burst.
    /// </summary>
    private sealed class PersistentCapture : IDisposable
    {
        private bool _disposed;

        public Process? Process { get; set; }

        public string CaptureId { get; set; } = string.Empty;

        public string SegListPath { get; set; } = string.Empty;

        public int CompletedCount { get; set; }

        public bool FirstSegment { get; set; } = true;

        public Task? StderrTask { get; set; }

        public async Task StopAsync()
        {
            if (Process == null || Process.HasExited)
            {
                return;
            }

            try
            {
                // Close stdin → sends EOF → ffmpeg flushes and exits.
                Process.StandardInput.BaseStream.Close();
            }
            catch
            {
                // Process may have already exited.
            }

            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await Process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    Process.Kill();
                }
                catch
                {
                    // Best effort.
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (Process != null && !Process.HasExited)
            {
                try
                {
                    Process.Kill();
                }
                catch
                {
                    // Best effort.
                }
            }

            Process?.Dispose();
        }
    }
}
