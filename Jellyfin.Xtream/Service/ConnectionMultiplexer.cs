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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly ConcurrentDictionary<int, ChannelBuffer> _channels = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionMultiplexer"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{ConnectionMultiplexer}"/> interface.</param>
    /// <param name="config">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    public ConnectionMultiplexer(ILogger<ConnectionMultiplexer> logger, IServerConfigurationManager config)
    {
        _logger = logger;
        _config = config;
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
    /// <returns>The channel buffer.</returns>
    public ChannelBuffer Subscribe(int streamId)
    {
        var buffer = _channels.GetOrAdd(streamId, id =>
        {
            _logger.LogInformation("Creating multiplexer buffer for stream {StreamId}", id);
            return new ChannelBuffer(id, BaseDir);
        });

        buffer.SubscriberCount++;
        if (buffer.State == ChannelBufferState.Idle)
        {
            buffer.State = ChannelBufferState.Buffering;
        }

        _logger.LogInformation("Subscribed to stream {StreamId}, subscribers: {Count}", streamId, buffer.SubscriberCount);
        return buffer;
    }

    /// <summary>
    /// Unsubscribes from a channel. When the subscriber count reaches zero, the
    /// channel moves to Idle and its segments are cleaned up.
    /// </summary>
    /// <param name="streamId">The Xtream stream ID.</param>
    public void Unsubscribe(int streamId)
    {
        if (!_channels.TryGetValue(streamId, out var buffer))
        {
            return;
        }

        buffer.SubscriberCount--;
        _logger.LogInformation("Unsubscribed from stream {StreamId}, subscribers: {Count}", streamId, buffer.SubscriberCount);

        if (buffer.SubscriberCount <= 0)
        {
            buffer.SubscriberCount = 0;
            buffer.State = ChannelBufferState.Idle;

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
        PluginConfiguration pluginConfig = Plugin.Instance.Configuration;
        if (!pluginConfig.EnableMultiplexing)
        {
            _logger.LogInformation("Stream multiplexing is disabled");
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Starting stream multiplexer (slice={Slice}s, retention={Retention}s, maxConn={MaxConn})",
            pluginConfig.MultiplexSliceSeconds,
            pluginConfig.MultiplexRetentionSeconds,
            pluginConfig.MaxActiveConnections);

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
        while (!ct.IsCancellationRequested)
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

            PluginConfiguration pluginConfig = Plugin.Instance.Configuration;
            int sliceSeconds = Math.Max(1, pluginConfig.MultiplexSliceSeconds);
            int retentionSeconds = Math.Max(10, pluginConfig.MultiplexRetentionSeconds);

            foreach (var buffer in activeChannels)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await CaptureSliceAsync(buffer, sliceSeconds, ct).ConfigureAwait(false);
                    buffer.PruneSegments(retentionSeconds);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error capturing slice for stream {StreamId}", buffer.StreamId);
                    // Brief pause before trying the next channel
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Captures <paramref name="sliceSeconds"/> of TS data from a channel using
    /// ffmpeg and stores it as an HLS segment.
    /// </summary>
    private async Task CaptureSliceAsync(ChannelBuffer buffer, int sliceSeconds, CancellationToken ct)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        string url = $"{config.BaseUrl}/{config.Username}/{config.Password}/{buffer.StreamId}";

        string segFilename = buffer.AllocateSegmentFilename();
        string segPath = Path.Combine(buffer.SegmentDir, segFilename);

        var ffmpegPath = GetFfmpegPath();
        string userAgentArg = string.IsNullOrEmpty(config.UserAgent)
            ? string.Empty
            : $"-user_agent \"{config.UserAgent}\" ";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"{userAgentArg}-i \"{url}\" -t {sliceSeconds}"
                + " -map 0 -dn -sn -c:v copy -c:a aac -b:a 192k"
                + $" -f mpegts -y \"{segPath}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        _logger.LogDebug("Capturing {Seconds}s from stream {StreamId}: {Args}", sliceSeconds, buffer.StreamId, psi.Arguments);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg for multiplexer capture");

        // Register cancellation to kill ffmpeg
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

        // Drain stderr to prevent buffer deadlock
        _ = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (!File.Exists(segPath) || new FileInfo(segPath).Length == 0)
        {
            _logger.LogWarning("Capture produced no data for stream {StreamId}", buffer.StreamId);
            if (File.Exists(segPath))
            {
                File.Delete(segPath);
            }

            return;
        }

        var segment = new SegmentInfo
        {
            Filename = segFilename,
            DurationSeconds = sliceSeconds,
            CapturedUtc = DateTime.UtcNow,
        };

        // First segment after a reconnect is a discontinuity
        bool isDiscontinuity = buffer.GetSegments().Count > 0;
        buffer.AddSegment(segment, isDiscontinuity);

        _logger.LogDebug("Captured segment {Filename} for stream {StreamId}", segFilename, buffer.StreamId);
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
