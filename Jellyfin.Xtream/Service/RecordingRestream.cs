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
/// A live stream backed by the growing TS file from an active recording.
/// Implements IDirectStreamProvider so Jellyfin serves the data through the
/// LiveStreamFiles endpoint, starting from byte 0 (the beginning of the recording).
/// This ensures the player always starts from the beginning rather than the live edge.
/// </summary>
public class RecordingRestream : ILiveStream, IDirectStreamProvider, IDisposable
{
    /// <summary>
    /// The global constant for the recording restream tuner host.
    /// </summary>
    public const string TunerHost = "Xtream-Recording";

    private readonly ILogger _logger;
    private readonly string _timerId;
    private readonly string _tsFilePath;
    private readonly Func<bool> _isStillGrowing;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingRestream"/> class.
    /// </summary>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="timerId">The timer ID for this recording.</param>
    /// <param name="tsFilePath">The local path to the growing TS file.</param>
    /// <param name="isStillGrowing">Delegate that returns true while the recording is in progress.</param>
    /// <param name="timer">The timer info with schedule times, or null if unavailable.</param>
    public RecordingRestream(IServerApplicationHost appHost, ILogger logger, string timerId, string tsFilePath, Func<bool> isStillGrowing, TimerInfo? timer = null)
    {
        _logger = logger;
        _timerId = timerId;
        _tsFilePath = tsFilePath;
        _isStillGrowing = isStillGrowing;

        UniqueId = Guid.NewGuid().ToString();

        // RunTimeTicks = remaining recording time (scheduledEnd - now), matching the
        // programme duration [now, scheduledEnd] so seekbar and media source agree.
        long? runTimeTicks = null;
        if (timer != null)
        {
            var end = timer.EndDate.AddSeconds(timer.PostPaddingSeconds);
            var remaining = end - DateTime.UtcNow;
            if (remaining.Ticks > 0)
            {
                runTimeTicks = remaining.Ticks;
            }
        }

        // Serve through the LiveStreamFiles endpoint so Jellyfin reads from our
        // GetStream() (byte 0) instead of opening the file itself at the live edge.
        string path = $"/LiveTv/LiveStreamFiles/{UniqueId}/stream.ts";
        MediaSource = new MediaSourceInfo
        {
            Id = $"recording_{timerId}",
            Path = appHost.GetSmartApiUrl(IPAddress.Any) + path,
            EncoderPath = appHost.GetApiUrlForLocalAccess() + path,
            Protocol = MediaProtocol.Http,
            Container = "ts",
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            IsInfiniteStream = false,
            RunTimeTicks = runTimeTicks,
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
        _logger.LogInformation("Opening recording stream for timer {TimerId} from {Path}", _timerId, _tsFilePath);
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
        _logger.LogInformation("Serving recording TS from byte 0 for timer {TimerId}", _timerId);
        return new TailingFileStream(_tsFilePath, _isStillGrowing);
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    /// <param name="disposing">Whether to dispose managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        // TailingFileStream instances are disposed by their consumers.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
