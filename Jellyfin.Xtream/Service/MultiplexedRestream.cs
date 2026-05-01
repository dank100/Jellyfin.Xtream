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
/// Points Jellyfin at the per-channel HLS playlist so the web player
/// (hls.js) or native apps can direct-play without an ffmpeg remux step.
/// </summary>
public class MultiplexedRestream : ILiveStream, IDisposable
{
    /// <summary>
    /// The global constant for the multiplexed tuner host.
    /// </summary>
    public const string TunerHost = "Xtream-Multiplex";

    private readonly ILogger _logger;
    private readonly ConnectionMultiplexer _multiplexer;
    private readonly int _streamId;
    private MediaSourceInfo _mediaSource;
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

        // Point directly at the HLS playlist so clients with hls.js (web player)
        // can direct-play without a Jellyfin remuxer in between. Discontinuity
        // tags in the m3u8 handle PTS jumps natively.
        string hlsPath = $"/Xtream/Multiplex/{streamId}/playlist.m3u8";
        string baseUrl = appHost.GetSmartApiUrl(IPAddress.Any);

        // Disable probing so Jellyfin does NOT open→probe→close→reopen the
        // stream.  Without this, Jellyfin's LiveStreamHelper hard-codes
        // AnalyzeDurationMs=3000 (overriding our 500) and the probe cycle
        // adds ~3-4s to startup — enough to trigger an Android TV error toast.
        // With SupportsProbing=false, Jellyfin uses AddMediaInfo() which
        // preserves our AnalyzeDurationMs and skips the extra round-trip.
        //
        // We must provide MediaStreams with valid codecs and Index >= 0 so
        // Jellyfin's StreamBuilder can determine that direct play / stream
        // copy is possible.  Without codec info it falls back to full
        // software transcode (H264→HEVC at ~0.4x realtime).
        _mediaSource = new MediaSourceInfo
        {
            Id = $"multiplex_{streamId}",
            Path = baseUrl + hlsPath,
            EncoderPath = baseUrl + hlsPath,
            Protocol = MediaProtocol.Http,
            Container = "ts",
            AnalyzeDurationMs = 10000,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            IsInfiniteStream = true,
            SupportsProbing = false,
            IsRemote = false,
            MediaStreams = new List<MediaStream>
            {
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "h264",
                    Profile = "High",
                    Width = 1920,
                    Height = 1080,
                    IsDefault = true,
                    PixelFormat = "yuv420p",
                    BitRate = 20_000_000,
                    AspectRatio = "16:9",
                    IsInterlaced = false,
                    BitDepth = 8,
                    Level = 40,
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = "eac3",
                    Profile = "LC",
                    Channels = 2,
                    ChannelLayout = "stereo",
                    SampleRate = 48000,
                    IsDefault = true,
                    BitRate = 128000,
                },
            },
        };

        OriginalStreamId = _mediaSource.Id;
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
    /// <remarks>
    /// Jellyfin's LiveTvMediaSourceProvider.Normalize() unconditionally sets
    /// IsInterlaced=true for every video stream from non-default LiveTV services.
    /// This forces clients (especially iOS/Swiftfin) to add a yadif deinterlacer,
    /// which in turn forces a full video transcode instead of stream copy.
    /// The getter resets IsInterlaced=false each time Jellyfin reads it so that
    /// OpenLiveStreamInternal sees the correct (progressive) value.
    /// </remarks>
    public MediaSourceInfo MediaSource
    {
        get
        {
            if (_mediaSource is not null)
            {
                // Jellyfin's Normalize() overrides these for live TV sources.
                // Reset them to ensure the HLS remux path is available.
                _mediaSource.SupportsDirectStream = true;
                _mediaSource.SupportsTranscoding = true;

                if (_mediaSource.MediaStreams is not null)
                {
                    foreach (var s in _mediaSource.MediaStreams)
                    {
                        if (s.Type == MediaStreamType.Video)
                        {
                            s.IsInterlaced = false;
                        }
                    }
                }
            }

            return _mediaSource!;
        }

        set => _mediaSource = value;
    }

    /// <inheritdoc />
    public string UniqueId { get; init; }

    /// <summary>
    /// Returns a stream for the transcoder fallback path.
    /// With HLS direct play, this should not be called; returns an empty stream.
    /// </summary>
    /// <returns>An empty stream.</returns>
    public System.IO.Stream GetStream()
    {
        _logger.LogWarning(
            "GetStream() called for multiplexed channel {StreamId} — expected HLS direct play, falling back to empty stream",
            _streamId);
        return System.IO.Stream.Null;
    }

    /// <inheritdoc />
    public async Task Open(CancellationToken openCancellationToken)
    {
        _logger.LogInformation("Opening multiplexed stream for channel {StreamId}", _streamId);
        var buffer = _multiplexer.Subscribe(_streamId, isLive: true);

        // Wait for enough segments to fill buffer sufficiently before playback.
        // With SupportsProbing=false there is no second open, so 2 segments
        // (typically 4s at default MultiplexSliceSeconds=2) is sufficient.
        const int minSegments = 2;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (buffer.GetSegments().Count < minSegments && DateTime.UtcNow < deadline)
        {
            openCancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(200, openCancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Multiplexed stream for channel {StreamId}: {Count} segments ready",
            _streamId,
            buffer.GetSegments().Count);
    }

    /// <inheritdoc />
    public Task Close()
    {
        _logger.LogInformation("Closing multiplexed stream for channel {StreamId}", _streamId);
        _multiplexer.Unsubscribe(_streamId, isLive: true);
        return Task.CompletedTask;
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
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
