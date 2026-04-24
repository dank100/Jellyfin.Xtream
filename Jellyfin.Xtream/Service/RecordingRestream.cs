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
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A live stream backed by the growing TS file from an active recording.
/// Points Jellyfin at the local file so ffmpeg starts from byte 0 (the beginning)
/// and can seek with -ss, giving a normal seekbar over the full EPG timeslot.
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
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="timerId">The timer ID for this recording.</param>
    /// <param name="tsFilePath">The local path to the growing TS file.</param>
    /// <param name="timer">The timer info with schedule times, or null if unavailable.</param>
    public RecordingRestream(ILogger logger, string timerId, string tsFilePath, TimerInfo? timer = null)
    {
        _logger = logger;
        _timerId = timerId;

        UniqueId = Guid.NewGuid().ToString();

        // Compute total duration from the timer schedule (including padding) so the
        // player shows a seekbar spanning the full recording window.
        long? runTimeTicks = null;
        if (timer != null)
        {
            var start = timer.StartDate.AddSeconds(-timer.PrePaddingSeconds);
            var end = timer.EndDate.AddSeconds(timer.PostPaddingSeconds);
            runTimeTicks = (end - start).Ticks;
        }

        // Point directly at the growing TS file on disk. This lets ffmpeg:
        // - Start reading from byte 0 (beginning of recording)
        // - Seek with -ss when the user scrubs the timeline
        MediaSource = new MediaSourceInfo
        {
            Id = $"recording_{timerId}",
            Path = tsFilePath,
            Protocol = MediaProtocol.File,
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
    public bool EnableStreamSharing => false;

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public string UniqueId { get; init; }

    /// <inheritdoc />
    public Task Open(CancellationToken openCancellationToken)
    {
        _logger.LogInformation("Opening recording TS stream for timer {TimerId}", _timerId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Close()
    {
        _logger.LogInformation("Closing recording TS stream for timer {TimerId}", _timerId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Stream GetStream()
    {
        // Not used — Jellyfin reads the local TS file path from MediaSource.Path.
        throw new NotSupportedException("Recording streams use local file access, not GetStream().");
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    /// <param name="disposing">Whether to dispose managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        // No persistent resources to dispose.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
