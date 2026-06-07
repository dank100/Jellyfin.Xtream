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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A minimal live stream that pipes the Xtream HTTP response directly to Jellyfin's
/// FFmpeg pipeline without any intermediate buffering or timestamp rewriting.
/// </summary>
public class DirectLiveStream : ILiveStream, IDisposable
{
    /// <summary>
    /// The tuner host ID used to identify direct live streams.
    /// </summary>
    public const string TunerHost = "Xtream-Direct";

    private readonly HttpClient _httpClient;
    private HttpResponseMessage? _response;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectLiveStream"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory for creating HTTP clients.</param>
    /// <param name="mediaSource">The media source containing the direct Xtream URL.</param>
    public DirectLiveStream(IHttpClientFactory httpClientFactory, MediaSourceInfo mediaSource)
    {
        _httpClient = httpClientFactory.CreateClient();
        MediaSource = mediaSource;
        UniqueId = Guid.NewGuid().ToString();
        OriginalStreamId = mediaSource.Id;
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
    public async Task Open(CancellationToken openCancellationToken)
    {
        string url = MediaSource.Path;
        _response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, openCancellationToken).ConfigureAwait(false);
        _response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public Stream GetStream()
    {
        if (_response == null)
        {
            throw new InvalidOperationException("Stream has not been opened.");
        }

        return _response.Content.ReadAsStream();
    }

    /// <inheritdoc />
    public Task Close()
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _response?.Dispose();
            _httpClient.Dispose();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }
}
