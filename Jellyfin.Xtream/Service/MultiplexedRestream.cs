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
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A live stream backed by the <see cref="ConnectionMultiplexer"/>.
/// Serves a continuous MPEG-TS byte stream via Jellyfin's standard
/// LiveStreamFiles endpoint so that ffmpeg remux/transcode paths
/// automatically receive the <c>aac_adtstoasc</c> bitstream filter.
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
    private readonly CancellationTokenSource _cts = new();
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

        UniqueId = Guid.NewGuid().ToString();

        // Use the standard LiveTV stream path. Jellyfin's EncodingHelper
        // adds -bsf:a aac_adtstoasc when reading from this path, fixing
        // the AAC ADTS→MP4/fMP4 issue for all clients.
        string path = $"/LiveTv/LiveStreamFiles/{UniqueId}/stream.ts";

        MediaSource = new MediaSourceInfo
        {
            Id = $"multiplex_{streamId}",
            Path = appHost.GetSmartApiUrl(IPAddress.Any) + path,
            EncoderPath = appHost.GetApiUrlForLocalAccess() + path,
            Protocol = MediaProtocol.Http,
            Container = "ts",
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            IsInfiniteStream = true,
            SupportsProbing = true,
            IsRemote = false,
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

    /// <summary>
    /// Returns a continuous MPEG-TS stream that concatenates segment files
    /// from the multiplexer's channel buffer.
    /// </summary>
    /// <returns>A readable stream of concatenated TS segments.</returns>
    public Stream GetStream()
    {
        var buffer = _multiplexer?.GetBuffer(_streamId);
        if (buffer is null)
        {
            _logger.LogWarning(
                "GetStream() called for channel {StreamId} but no buffer exists",
                _streamId);
            return Stream.Null;
        }

        // Start from the most recent segment to minimize latency
        var segments = buffer.GetSegments();
        int startIndex = Math.Max(0, segments.Count - 2);

        _logger.LogInformation(
            "Opening TS stream reader for channel {StreamId}, starting at segment {Index}/{Total}",
            _streamId,
            startIndex,
            segments.Count);

        return new MultiplexedSegmentStream(buffer, startIndex, _cts.Token);
    }

    /// <inheritdoc />
    public async Task Open(CancellationToken openCancellationToken)
    {
        _logger.LogInformation("Opening multiplexed stream for channel {StreamId}", _streamId);
        var buffer = _multiplexer.Subscribe(_streamId, isLive: true);

        // Wait for enough segments before allowing probe/playback.
        const int minSegments = 3;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (buffer.GetSegments().Count < minSegments && DateTime.UtcNow < deadline)
        {
            openCancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(500, openCancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Multiplexed stream for channel {StreamId}: {Count} segments ready",
            _streamId,
            buffer.GetSegments().Count);
    }

    /// <inheritdoc />
    public async Task Close()
    {
        _logger.LogInformation("Closing multiplexed stream for channel {StreamId}", _streamId);
        await _cts.CancelAsync().ConfigureAwait(false);
        _multiplexer.Unsubscribe(_streamId, isLive: true);
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

        if (disposing)
        {
            _cts.Dispose();
        }

        _disposed = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
