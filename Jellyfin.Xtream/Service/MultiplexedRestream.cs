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
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A live stream backed by the <see cref="ConnectionMultiplexer"/>.
/// Pipes multiplexed TS segments through a buffer stream so Jellyfin's
/// transcoder reads a continuous MPEG-TS feed (just like <see cref="Restream"/>).
/// </summary>
public class MultiplexedRestream : ILiveStream, IDirectStreamProvider, IDisposable
{
    /// <summary>
    /// The global constant for the multiplexed tuner host.
    /// </summary>
    public const string TunerHost = "Xtream-Multiplex";

    private readonly ILogger _logger;
    private readonly ConnectionMultiplexer _multiplexer;
    private readonly int _streamId;
    private readonly WrappedBufferStream _buffer;
    private readonly CancellationTokenSource _cts;
    private Task? _pumpTask;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiplexedRestream"/> class.
    /// </summary>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="multiplexer">The connection multiplexer singleton.</param>
    /// <param name="streamId">The Xtream stream ID for this channel.</param>
    public MultiplexedRestream(IServerApplicationHost appHost, ILogger logger, ConnectionMultiplexer multiplexer, int streamId)
    {
        _logger = logger;
        _multiplexer = multiplexer;
        _streamId = streamId;
        _buffer = new WrappedBufferStream(16 * 1024 * 1024);
        _cts = new CancellationTokenSource();

        UniqueId = Guid.NewGuid().ToString();

        string path = $"/LiveTv/LiveStreamFiles/{UniqueId}/stream.ts";
        string baseUrl = appHost.GetSmartApiUrl(IPAddress.Any);

        MediaSource = new MediaSourceInfo
        {
            Id = $"multiplex_{streamId}",
            Path = baseUrl + path,
            EncoderPath = appHost.GetApiUrlForLocalAccess() + path,
            Protocol = MediaProtocol.Http,
            Container = "ts",
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = true,
            IsInfiniteStream = true,
        };

        OriginalStreamId = MediaSource.Id;
    }

    /// <inheritdoc />
    public int ConsumerCount { get; set; }

    /// <inheritdoc />
    public string OriginalStreamId { get; set; }

    /// <inheritdoc />
    public string TunerHostId => TunerHost;

    /// <inheritdoc />
    public bool EnableStreamSharing => true;

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public string UniqueId { get; init; }

    /// <inheritdoc />
    public async Task Open(CancellationToken openCancellationToken)
    {
        _logger.LogInformation("Opening multiplexed stream for channel {StreamId}", _streamId);
        var buffer = _multiplexer.Subscribe(_streamId, isLive: true);

        // Wait for enough segments so there is data to pump.
        const int minSegments = 3;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (buffer.GetSegments().Count < minSegments && DateTime.UtcNow < deadline)
        {
            openCancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(500, openCancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Multiplexed stream for channel {StreamId}: {Count} segments ready, starting pump",
            _streamId,
            buffer.GetSegments().Count);

        // Start background task that reads new segment files and writes them into the buffer.
        _pumpTask = Task.Run(() => PumpSegmentsAsync(buffer, _cts.Token), CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task Close()
    {
        _logger.LogInformation("Closing multiplexed stream for channel {StreamId}", _streamId);
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_pumpTask != null)
        {
            await _pumpTask.ConfigureAwait(false);
        }

        _multiplexer.Unsubscribe(_streamId, isLive: true);
    }

    /// <inheritdoc />
    public Stream GetStream()
    {
        _logger.LogInformation("Opening restream {Count} for multiplexed channel {StreamId}.", ConsumerCount, _streamId);
        return new WrappedBufferReadStream(_buffer);
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    /// <param name="disposing">Whether to dispose managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            _buffer.Dispose();
            _cts.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Continuously reads new TS segment files from the channel buffer, pipes them
    /// through an ffmpeg remuxer that normalizes timestamps, and writes clean
    /// output into the shared buffer stream.
    /// </summary>
    private async Task PumpSegmentsAsync(ChannelBuffer channelBuffer, CancellationToken ct)
    {
        var initialSegments = channelBuffer.GetSegments();
        int skipCount = Math.Max(0, initialSegments.Count - 3);
        int lastSegmentIndex = skipCount > 0
            ? channelBuffer.GetGlobalIndex(skipCount - 1)
            : -1;

        // Remuxer: normalizes timestamps across concatenated capture segments.
        // +igndts+genpts: ignores source DTS (which jump at segment boundaries)
        //   and regenerates PTS from frame ordering.
        // -map 0: copies all streams (video + audio).
        var ffmpegPath = "/usr/lib/jellyfin-ffmpeg/ffmpeg";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = "-fflags +genpts+igndts"
                + " -f mpegts -i pipe:0"
                + " -map 0 -c copy"
                + " -f mpegts -mpegts_flags +resend_headers pipe:1",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var ffmpeg = System.Diagnostics.Process.Start(psi);
        if (ffmpeg == null)
        {
            _logger.LogError("Failed to start remuxer for channel {StreamId}", _streamId);
            return;
        }

        _logger.LogInformation("Started remuxer for channel {StreamId}, PID {Pid}", _streamId, ffmpeg.Id);

        // Read ffmpeg stdout into the buffer.
        var readTask = Task.Run(
            async () =>
            {
                long totalRead = 0;
                try
                {
                    byte[] readBuf = new byte[65536];
                    int bytesRead;
                    while ((bytesRead = await ffmpeg.StandardOutput.BaseStream.ReadAsync(readBuf, ct).ConfigureAwait(false)) > 0)
                    {
                        await _buffer.WriteAsync(readBuf.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                        totalRead += bytesRead;
                    }
                }
                catch (OperationCanceledException)
                {
                }

                _logger.LogInformation("Remuxer stdout reader finished for channel {StreamId}, total bytes: {Bytes}", _streamId, totalRead);
            },
            ct);

        // Drain stderr to prevent buffer deadlock.
        var stderrTask = Task.Run(
            async () =>
            {
                try
                {
                    string? line;
                    while ((line = await ffmpeg.StandardError.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                    {
                        _logger.LogInformation("Remuxer stderr: {Line}", line);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            },
            ct);

        long totalWritten = 0;
        int segmentsWritten = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var segments = channelBuffer.GetSegments();
                bool wroteAny = false;

                for (int i = 0; i < segments.Count; i++)
                {
                    int globalIndex = channelBuffer.GetGlobalIndex(i);
                    if (globalIndex <= lastSegmentIndex)
                    {
                        continue;
                    }

                    var seg = segments[i];
                    string segPath = Path.Combine(channelBuffer.SegmentDir, seg.Filename);

                    if (!File.Exists(segPath))
                    {
                        continue;
                    }

                    try
                    {
                        byte[] data = await File.ReadAllBytesAsync(segPath, ct).ConfigureAwait(false);
                        await ffmpeg.StandardInput.BaseStream.WriteAsync(data, ct).ConfigureAwait(false);
                        await ffmpeg.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
                        totalWritten += data.Length;
                        segmentsWritten++;
                        lastSegmentIndex = globalIndex;
                        wroteAny = true;

                        _logger.LogInformation(
                            "Pump for channel {StreamId}: wrote segment {Filename} ({Bytes} bytes), total: {Segs} segments, {Total} bytes",
                            _streamId,
                            seg.Filename,
                            data.Length,
                            segmentsWritten,
                            totalWritten);
                    }
                    catch (FileNotFoundException)
                    {
                        // Segment was pruned before we could read it — skip it
                        _logger.LogDebug("Segment {Filename} was pruned before pump could read it, skipping", seg.Filename);
                        lastSegmentIndex = globalIndex;
                    }
                    catch (IOException ex) when (ex is not FileNotFoundException)
                    {
                        _logger.LogWarning(ex, "Pump write failed for segment {Filename}, remuxer may have exited", seg.Filename);
                        return;
                    }
                }

                if (!wroteAny)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Segment pump for channel {StreamId} failed", _streamId);
        }
        finally
        {
            _logger.LogInformation(
                "Pump for channel {StreamId} stopping: {Segs} segments, {Bytes} bytes written",
                _streamId,
                segmentsWritten,
                totalWritten);
            try
            {
                ffmpeg.StandardInput.Close();
            }
            catch (IOException)
            {
            }

            await readTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            if (!ffmpeg.HasExited)
            {
                ffmpeg.Kill();
            }
        }
    }
}
