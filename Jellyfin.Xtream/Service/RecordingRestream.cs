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
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A live stream that points directly at the plugin's own HLS endpoint for an
/// active recording. By using direct-play with a VOD-style HLS playlist
/// (containing #EXT-X-ENDLIST), the player starts from the beginning of the
/// recording instead of the live edge.
/// </summary>
public class RecordingRestream : ILiveStream, IDisposable
{
    /// <summary>
    /// The global constant for the recording restream tuner host.
    /// </summary>
    public const string TunerHost = "Xtream-Recording";

    private readonly ILogger _logger;
    private readonly string _timerId;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingRestream"/> class.
    /// </summary>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="timerId">The timer ID for this recording.</param>
    /// <param name="timer">The timer info with schedule times, or null if unavailable.</param>
    public RecordingRestream(IServerApplicationHost appHost, ILogger logger, string timerId, TimerInfo? timer = null)
    {
        _logger = logger;
        _timerId = timerId;

        UniqueId = Guid.NewGuid().ToString();

        // Point directly at the plugin's HLS endpoint which serves the
        // ffmpeg EVENT playlist. Without #EXT-X-ENDLIST, HLS.js treats it
        // as live and positions at the live edge — matching the Jellyfin
        // guide overlay which also positions at wall clock "now".
        // All segments from the beginning are available for backward seeking.
        string hlsPath = $"/Xtream/Recordings/{timerId}/stream.m3u8";
        string baseUrl = appHost.GetSmartApiUrl(IPAddress.Any);

        MediaSource = new MediaSourceInfo
        {
            Id = $"recording_{timerId}",
            Path = baseUrl + hlsPath,
            Protocol = MediaProtocol.Http,
            Container = "hls",
            // No RunTimeTicks — ensures HLS.js is used (not native HLS).
            // Direct play bypasses Jellyfin's transcoder.
            SupportsDirectPlay = true,
            SupportsDirectStream = false,
            SupportsTranscoding = false,
            IsInfiniteStream = false,
            MediaStreams = new List<MediaStream>
            {
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "h264",
                    IsDefault = true,
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = "aac",
                    IsDefault = true,
                },
            },
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
    public Task Open(CancellationToken openCancellationToken)
    {
        _logger.LogInformation("Opening recording HLS stream for timer {TimerId}, URL: {Url}", _timerId, MediaSource.Path);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Close()
    {
        _logger.LogInformation("Closing recording stream for timer {TimerId}", _timerId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Stream GetStream()
    {
        // Not used — playback goes through direct-play HLS, not IDirectStreamProvider.
        throw new NotSupportedException("RecordingRestream uses direct-play HLS; GetStream should not be called.");
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    /// <param name="disposing">Whether to dispose managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
