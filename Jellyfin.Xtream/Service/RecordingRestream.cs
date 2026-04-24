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
/// A live stream that points at the growing TS file for an active recording.
/// Using Protocol=File allows the transcoder's ffmpeg to seek anywhere in the
/// file, enabling backward seeking to the beginning of the recording.
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
    /// <param name="tsFilePath">Path to the growing TS file, or null to fall back to HLS endpoint.</param>
    /// <param name="recordingStartUtc">When the recording actually started (for RunTimeTicks calculation).</param>
    public RecordingRestream(IServerApplicationHost appHost, ILogger logger, string timerId, TimerInfo? timer = null, string? tsFilePath = null, DateTime? recordingStartUtc = null)
    {
        _logger = logger;
        _timerId = timerId;

        UniqueId = Guid.NewGuid().ToString();

        if (!string.IsNullOrEmpty(tsFilePath) && File.Exists(tsFilePath))
        {
            // Point directly at the growing TS file on disk.
            // Protocol=File lets the transcoder's ffmpeg seek to any byte
            // position, enabling backward seeking to the recording start.
            // IsInfiniteStream=false + RunTimeTicks tells the player this
            // is a seekable file with a known duration.
            var elapsed = recordingStartUtc.HasValue
                ? DateTime.UtcNow - recordingStartUtc.Value
                : TimeSpan.Zero;
            MediaSource = new MediaSourceInfo
            {
                Id = $"recording_{timerId}",
                Path = tsFilePath,
                Protocol = MediaProtocol.File,
                Container = "mpegts",
                SupportsDirectPlay = false,
                SupportsDirectStream = false,
                SupportsTranscoding = true,
                IsInfiniteStream = false,
                RunTimeTicks = elapsed.Ticks,
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
        }
        else
        {
            // Fallback: use plugin HLS endpoint via HTTP
            string hlsPath = $"/Xtream/Recordings/{timerId}/stream.m3u8";
            string baseUrl = appHost.GetSmartApiUrl(IPAddress.Any);

            MediaSource = new MediaSourceInfo
            {
                Id = $"recording_{timerId}",
                Path = baseUrl + hlsPath,
                Protocol = MediaProtocol.Http,
                Container = "hls",
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
        }

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
