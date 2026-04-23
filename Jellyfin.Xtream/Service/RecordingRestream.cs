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
/// A live stream backed by a growing recording file on disk.
/// Each call to <see cref="GetStream"/> opens an independent reader that tails the file.
/// </summary>
public class RecordingRestream : ILiveStream, IDirectStreamProvider, IDisposable
{
    /// <summary>
    /// The global constant for the recording restream tuner host.
    /// </summary>
    public const string TunerHost = "Xtream-Recording";

    private readonly ILogger _logger;
    private readonly string _tsFilePath;
    private readonly Func<bool> _isStillGrowing;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingRestream"/> class.
    /// </summary>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="tsFilePath">Path to the growing .ts file.</param>
    /// <param name="timerId">The timer ID for this recording.</param>
    /// <param name="isStillGrowing">Delegate returning true while the recording is in progress.</param>
    public RecordingRestream(IServerApplicationHost appHost, ILogger logger, string tsFilePath, string timerId, Func<bool> isStillGrowing)
    {
        _logger = logger;
        _tsFilePath = tsFilePath;
        _isStillGrowing = isStillGrowing;

        UniqueId = Guid.NewGuid().ToString();

        string path = $"/LiveTv/LiveStreamFiles/{UniqueId}/stream.ts";
        MediaSource = new MediaSourceInfo
        {
            Id = $"recording_{timerId}",
            Path = appHost.GetSmartApiUrl(IPAddress.Any) + path,
            EncoderPath = appHost.GetApiUrlForLocalAccess() + path,
            Protocol = MediaProtocol.Http,
            IsInfiniteStream = true,
            Container = "ts",
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
        _logger.LogInformation("Opening recording restream for {Path}", _tsFilePath);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Close()
    {
        _logger.LogInformation("Closing recording restream for {Path}", _tsFilePath);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Stream GetStream()
    {
        _logger.LogInformation("Opening recording tail reader {Count} for {Path}", ConsumerCount, _tsFilePath);
        return new TailingFileStream(_tsFilePath, _isStillGrowing);
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    /// <param name="disposing">Whether to dispose managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        // No persistent resources — each consumer's TailingFileStream is independently disposed.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
