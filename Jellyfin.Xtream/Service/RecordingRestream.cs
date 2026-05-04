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
/// An <see cref="ILiveStream"/> that wraps an active recording's HLS playlist,
/// mirroring <see cref="MultiplexedRestream"/> so the live TV pipeline treats it
/// identically to a regular live channel. This gives the "paused client" experience:
/// start at the live edge, accumulate a seekable buffer, and jump to live.
/// </summary>
public class RecordingRestream : ILiveStream, IDisposable
{
    private readonly ILogger _logger;
    private readonly RecordingEngine _recordingEngine;
    private readonly ConnectionMultiplexer _multiplexer;
    private readonly string _timerId;
    private MediaSourceInfo _mediaSource;
    private FileStream? _fileStream;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingRestream"/> class.
    /// </summary>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="recordingEngine">The recording engine.</param>
    /// <param name="multiplexer">The connection multiplexer (for probing).</param>
    /// <param name="timerId">The timer ID of the active recording.</param>
    /// <param name="timer">The timer info with EPG start/end dates.</param>
    public RecordingRestream(
        IServerApplicationHost appHost,
        ILogger logger,
        RecordingEngine recordingEngine,
        ConnectionMultiplexer multiplexer,
        string timerId,
        TimerInfo timer)
    {
        _logger = logger;
        _recordingEngine = recordingEngine;
        _multiplexer = multiplexer;
        _timerId = timerId;
        UniqueId = Guid.NewGuid().ToString();

        // Calculate EPG duration including padding — setting RunTimeTicks > 0 is critical
        // because the web client uses it to determine CanSeek:
        //   CanSeek = (RunTimeTicks || 0) > 0 || canPlayerSeek(player)
        // Without this, the time-of-day seekbar renders but is disabled/non-interactive.
        var start = timer.StartDate - TimeSpan.FromSeconds(timer.PrePaddingSeconds);
        var end = timer.EndDate + TimeSpan.FromSeconds(timer.PostPaddingSeconds);
        long durationTicks = (end - start).Ticks;

        // Use the growing TS file directly — ffmpeg reads from byte 0 and can seek
        // with -ss anywhere in the file. The HLS EVENT playlist starts from the live
        // edge which prevents backward seeking.
        string tsPath = recordingEngine.GetTsFilePath(timerId)
            ?? throw new InvalidOperationException($"No TS file for recording {timerId}");

        // Encode recording start epoch ms in the Name so the web client JS can read it
        // from the PlaybackInfo response (DOM-based detection fails because Jellyfin
        // clears startTimeText/endTimeText for live TV channels).
        long startEpochMs = (long)(start - DateTime.UnixEpoch).TotalMilliseconds;

        _mediaSource = new MediaSourceInfo
        {
            Id = $"xtream_rec_{timerId}",
            Name = $"xtream_rec_start_{startEpochMs}",
            Path = tsPath,
            EncoderPath = tsPath,
            Protocol = MediaProtocol.File,
            Container = "ts",
            RunTimeTicks = durationTicks,
            AnalyzeDurationMs = 500,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            IsInfiniteStream = false,
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
                    Width = 1280,
                    Height = 720,
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
    public string TunerHostId => "Xtream-Recording";

    /// <inheritdoc />
    public bool EnableStreamSharing => false;

    /// <inheritdoc />
    public MediaSourceInfo MediaSource
    {
        get => _mediaSource;
        set => _mediaSource = value;
    }

    /// <inheritdoc />
    public string UniqueId { get; init; }

    /// <inheritdoc />
    public Stream GetStream()
    {
        string? tsPath = _recordingEngine.GetTsFilePath(_timerId);
        if (string.IsNullOrEmpty(tsPath) || !File.Exists(tsPath))
        {
            _logger.LogWarning("GetStream() — no TS file for recording {TimerId}", _timerId);
            return Stream.Null;
        }

        _logger.LogInformation(
            "GetStream() — opening TS file from byte 0 for recording {TimerId}: {Path}",
            _timerId,
            tsPath);

        // Open with ReadWrite sharing so the recording engine can keep writing.
        // Reading from position 0 gives the transcoder ALL recorded content.
        _fileStream = new FileStream(
            tsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 65536);

        return _fileStream;
    }

    /// <inheritdoc />
    public async Task Open(CancellationToken openCancellationToken)
    {
        _logger.LogInformation("Opening recording restream for timer {TimerId}", _timerId);

        // Wait for at least one segment to be available
        string? hlsDir = _recordingEngine.GetHlsDirectory(_timerId);
        if (hlsDir != null)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                openCancellationToken.ThrowIfCancellationRequested();
                string playlistPath = Path.Combine(hlsDir, "live.m3u8");
                if (File.Exists(playlistPath))
                {
                    break;
                }

                await Task.Delay(500, openCancellationToken).ConfigureAwait(false);
            }

            // Probe a real segment if available for accurate metadata
            await ProbeRecordingSegmentAsync(hlsDir, openCancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Recording restream opened for timer {TimerId}", _timerId);
    }

    /// <inheritdoc />
    public Task Close()
    {
        _logger.LogInformation("Closing recording restream for timer {TimerId}", _timerId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Probes a segment in the recording HLS directory to get real codec metadata.
    /// </summary>
    private async Task ProbeRecordingSegmentAsync(string hlsDir, CancellationToken ct)
    {
        try
        {
            // Find the newest .ts segment
            string? newestSegment = null;
            foreach (var file in Directory.GetFiles(hlsDir, "seg_*.ts"))
            {
                if (newestSegment == null || string.Compare(file, newestSegment, StringComparison.Ordinal) > 0)
                {
                    newestSegment = file;
                }
            }

            if (newestSegment == null)
            {
                _logger.LogDebug("No segments found in {Dir} for probing", hlsDir);
                return;
            }

            var probed = await _multiplexer.ProbeSegmentAsync(newestSegment, ct).ConfigureAwait(false);
            if (probed != null)
            {
                _mediaSource.MediaStreams = probed;
                _logger.LogInformation(
                    "Probed recording {TimerId}: {Streams}",
                    _timerId,
                    string.Join(", ", probed.ConvertAll(s => $"{s.Type}:{s.Codec} {s.Width}x{s.Height}")));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe recording segment for {TimerId}", _timerId);
        }
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
            _fileStream?.Dispose();
            _fileStream = null;
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
