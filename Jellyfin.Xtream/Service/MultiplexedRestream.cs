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
    private long _gapFillCount;
    private long _gapFillBytes;
    private long _freshBytes;

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
        _buffer = new WrappedBufferStream(32 * 1024 * 1024);
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

    /// <summary>Gets the number of gap-fill replay events.</summary>
    public long GapFillCount => Interlocked.Read(ref _gapFillCount);

    /// <summary>Gets the total bytes written by gap-fill replays.</summary>
    public long GapFillBytes => Interlocked.Read(ref _gapFillBytes);

    /// <summary>Gets the total bytes written from fresh (non-replay) segments.</summary>
    public long FreshBytes => Interlocked.Read(ref _freshBytes);

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public string UniqueId { get; init; }

    /// <inheritdoc />
    public async Task Open(CancellationToken openCancellationToken)
    {
        _logger.LogInformation("Opening multiplexed stream for channel {StreamId}", _streamId);
        var buffer = _multiplexer.Subscribe(_streamId, isLive: true);

        // Wait for enough segments to fill buffer sufficiently before playback.
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
        int lastSegmentIndex = -1;

        // In-process timestamp + continuity counter normalization — zero latency.
        var tsRewriter = new TsTimestampRewriter();

        long totalWritten = 0;
        int segmentsWritten = 0;
        var lastWriteTime = DateTime.UtcNow;

        // Gap-fill: write null TS packets (PID 0x1FFF) to keep the transcoder's
        // TCP connection alive during capture gaps. Null packets carry no PTS/PES,
        // so they cannot cause backward timestamps or PTS drift.
        const int gapFillThresholdMs = 2000;
        const int nullPacketCount = 100; // 100 × 188 = 18.8 KB per gap-fill write
        byte[] nullTsPackets = BuildNullTsPackets(nullPacketCount);

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
                        tsRewriter.Rewrite(data);
                        await _buffer.WriteAsync(data, ct).ConfigureAwait(false);
                        totalWritten += data.Length;
                        segmentsWritten++;
                        lastSegmentIndex = globalIndex;
                        wroteAny = true;
                        lastWriteTime = DateTime.UtcNow;
                        Interlocked.Add(ref _freshBytes, data.Length);

                        _logger.LogInformation(
                            "Pump for channel {StreamId}: wrote segment {Filename} ({Bytes} bytes, adjusted={Adjusted}, lastPts={LastPts}, adj={Adj}), total: {Segs} segments, {Total} bytes",
                            _streamId,
                            seg.Filename,
                            data.Length,
                            tsRewriter.Adjustment != 0,
                            tsRewriter.LastOutputPts,
                            tsRewriter.Adjustment,
                            segmentsWritten,
                            totalWritten);
                    }
                    catch (FileNotFoundException)
                    {
                        _logger.LogDebug("Segment {Filename} was pruned before pump could read it, skipping", seg.Filename);
                        lastSegmentIndex = globalIndex;
                    }
                    catch (IOException ex) when (ex is not FileNotFoundException)
                    {
                        _logger.LogWarning(ex, "Pump write failed for segment {Filename}", seg.Filename);
                        return;
                    }
                }

                if (!wroteAny)
                {
                    double msSinceWrite = (DateTime.UtcNow - lastWriteTime).TotalMilliseconds;
                    if (segmentsWritten > 0 && msSinceWrite >= gapFillThresholdMs)
                    {
                        // Check for fresh data before doing gap-fill work.
                        var freshSegs = channelBuffer.GetSegments();
                        bool hasFresh = false;
                        for (int fi = 0; fi < freshSegs.Count; fi++)
                        {
                            if (channelBuffer.GetGlobalIndex(fi) > lastSegmentIndex)
                            {
                                hasFresh = true;
                                break;
                            }
                        }

                        if (!hasFresh)
                        {
                            // Gap-fill: write null TS packets to keep the TCP connection
                            // alive. Null packets (PID 0x1FFF) contain no PTS/PES data,
                            // so they cannot cause backward timestamps or PTS drift.
                            await _buffer.WriteAsync(nullTsPackets, ct).ConfigureAwait(false);

                            totalWritten += nullTsPackets.Length;
                            lastWriteTime = DateTime.UtcNow;
                            Interlocked.Increment(ref _gapFillCount);
                            Interlocked.Add(ref _gapFillBytes, nullTsPackets.Length);
                        }
                    }
                    else
                    {
                        await Task.Delay(50, ct).ConfigureAwait(false);
                    }
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
        }
    }

    /// <summary>
    /// Builds a buffer of null TS packets (PID 0x1FFF). These are valid MPEG-TS
    /// packets that carry no content (no PES/PTS). Writing them during capture gaps
    /// keeps the transcoder's TCP connection alive without introducing backward
    /// timestamps or PTS drift.
    /// </summary>
    private static byte[] BuildNullTsPackets(int count)
    {
        byte[] data = new byte[count * 188];
        for (int i = 0; i < count; i++)
        {
            int offset = i * 188;
            data[offset] = 0x47;     // sync byte
            data[offset + 1] = 0x1F; // PID high bits (0x1FFF = null)
            data[offset + 2] = 0xFF; // PID low bits
            data[offset + 3] = 0x10; // adaptation = payload only, CC = 0
            // remaining 184 bytes are 0x00 (padding)
        }

        return data;
    }
}
