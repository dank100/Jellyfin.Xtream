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

        _mediaSource = new MediaSourceInfo
        {
            Id = $"multiplex_{streamId}",
            Path = baseUrl + hlsPath,
            EncoderPath = baseUrl + hlsPath,
            Protocol = MediaProtocol.Http,
            Container = "ts",
            AnalyzeDurationMs = 500,
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
                    BitRate = 20_000_000,
                    IsDefault = true,
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = "aac",
                    Channels = 2,
                    ChannelLayout = "stereo",
                    SampleRate = 48000,
                    IsDefault = true,
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
    /// SupportsDirectStream=false and IsInterlaced=true for every video stream
    /// from non-default LiveTV services. The getter resets these each time
    /// Jellyfin reads the property so that clients see DirectStream as available
    /// and don't add a yadif deinterlacer (which forces full video transcode).
    /// </remarks>
    public MediaSourceInfo MediaSource
    {
        get
        {
            if (_mediaSource is not null)
            {
                _mediaSource.SupportsDirectStream = true;

                if (_mediaSource.MediaStreams is not null)
                {
                    int videoIdx = 0;
                    int audioIdx = 1;
                    foreach (var s in _mediaSource.MediaStreams)
                    {
                        if (s.Type == MediaStreamType.Video)
                        {
                            s.IsInterlaced = false;
                            // Probe assigns Index=-1 for HLS inputs;
                            // StreamBuilder requires Index >= 0 for DirectStream.
                            if (s.Index < 0)
                            {
                                s.Index = videoIdx++;
                            }
                        }
                        else if (s.Type == MediaStreamType.Audio)
                        {
                            if (s.Index < 0)
                            {
                                s.Index = audioIdx++;
                            }
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
        const int minSegments = 3;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (buffer.GetSegments().Count < minSegments && DateTime.UtcNow < deadline)
        {
            openCancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(500, openCancellationToken).ConfigureAwait(false);
        }

        var segments = buffer.GetSegments();
        _logger.LogInformation(
            "Multiplexed stream for channel {StreamId}: {Count} segments ready",
            _streamId,
            segments.Count);

        // Probe the newest segment to get real codec metadata.
        // Newest is least likely to be pruned during the probe.
        if (segments.Count > 0)
        {
            var newest = segments[^1];
            string segPath = System.IO.Path.Combine(buffer.SegmentDir, newest.Filename);
            var probed = await _multiplexer.ProbeSegmentAsync(segPath, openCancellationToken).ConfigureAwait(false);
            if (probed != null)
            {
                _mediaSource.MediaStreams = probed;
                _logger.LogInformation(
                    "Probed channel {StreamId}: {Streams}",
                    _streamId,
                    string.Join(", ", probed.ConvertAll(s => $"{s.Type}:{s.Codec} {s.Width}x{s.Height}")));
            }
            else
            {
                _logger.LogWarning("Probe failed for channel {StreamId}, using synthetic defaults", _streamId);
            }
        }
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
