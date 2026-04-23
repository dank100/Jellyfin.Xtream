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
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A live stream that points clients at the HLS playlist for an active recording.
/// Does NOT implement IDirectStreamProvider — the client plays the HLS URL directly,
/// which gives native seeking support on all platforms (HLS.js, ExoPlayer, AVPlayer).
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
    public RecordingRestream(IServerApplicationHost appHost, ILogger logger, string timerId)
    {
        _logger = logger;
        _timerId = timerId;

        UniqueId = Guid.NewGuid().ToString();

        string hlsUrl = $"{appHost.GetSmartApiUrl(System.Net.IPAddress.Any)}/Xtream/Recordings/{timerId}/stream.m3u8";
        string hlsUrlLocal = $"{appHost.GetApiUrlForLocalAccess()}/Xtream/Recordings/{timerId}/stream.m3u8";
        MediaSource = new MediaSourceInfo
        {
            Id = $"recording_{timerId}",
            Path = hlsUrl,
            EncoderPath = hlsUrlLocal,
            Protocol = MediaProtocol.Http,
            Container = "hls",
            SupportsDirectPlay = true,
            SupportsDirectStream = false,
            SupportsTranscoding = false,
            IsInfiniteStream = false,
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
        _logger.LogInformation("Opening recording HLS stream for timer {TimerId}", _timerId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Close()
    {
        _logger.LogInformation("Closing recording HLS stream for timer {TimerId}", _timerId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Stream GetStream()
    {
        // Not used — clients play the HLS URL directly from MediaSource.Path.
        throw new NotSupportedException("Recording streams use HLS direct play, not GetStream().");
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
