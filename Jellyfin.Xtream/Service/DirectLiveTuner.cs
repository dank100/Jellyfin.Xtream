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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A live stream that pipes the Xtream HTTP response directly to Jellyfin's
/// FFmpeg pipeline, transparently reconnecting whenever the server closes the
/// connection (Xtream rotates streams every ~15 seconds by design).
/// No intermediate buffer or timestamp rewriter — FFmpeg sees a continuous byte
/// stream and handles any minor DTS discontinuities with its built-in clamping.
/// </summary>
public sealed class DirectLiveTuner : ILiveStream, IDirectStreamProvider, IDisposable
{
    /// <summary>
    /// The tuner host ID used to identify direct live streams.
    /// </summary>
    public const string TunerHost = "Xtream-Direct";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly string _url;
    private readonly CancellationTokenSource _disposeCts = new();
    private ReconnectingStream? _stream;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectLiveTuner"/> class.
    /// </summary>
    /// <param name="appHost">Application host for building local endpoint URLs.</param>
    /// <param name="httpClientFactory">Factory for creating HTTP clients.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="mediaSource">The media source containing the Xtream stream URL.</param>
    public DirectLiveTuner(
        IServerApplicationHost appHost,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        MediaSourceInfo mediaSource)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _url = mediaSource.Path;

        MediaSource = mediaSource;
        UniqueId = Guid.NewGuid().ToString();
        OriginalStreamId = mediaSource.Id;

        // Point Jellyfin's FFmpeg at our local live-stream endpoint.
        string path = $"/LiveTv/LiveStreamFiles/{UniqueId}/stream.ts";
        MediaSource.Path = appHost.GetSmartApiUrl(IPAddress.Any) + path;
        MediaSource.EncoderPath = appHost.GetApiUrlForLocalAccess() + path;
        MediaSource.Protocol = MediaProtocol.Http;
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
        _stream = new ReconnectingStream(_httpClientFactory, _logger, _url, MediaSource.Id, _disposeCts.Token);
        await _stream.ConnectAsync(openCancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Stream GetStream()
    {
        if (_stream == null)
        {
            throw new InvalidOperationException("Stream has not been opened.");
        }

        return _stream;
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
            _disposeCts.Cancel();
            _disposeCts.Dispose();
            _stream?.Dispose();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A <see cref="Stream"/> that reads from an Xtream HTTP endpoint and
    /// reconnects immediately whenever the server closes the connection.
    /// </summary>
    private sealed class ReconnectingStream : Stream
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly string _url;
        private readonly string _channelId;
        private readonly CancellationToken _disposeToken;
        private HttpResponseMessage? _response;
        private Stream? _inner;
        private bool _disposed;

        public ReconnectingStream(
            IHttpClientFactory httpClientFactory,
            ILogger logger,
            string url,
            string channelId,
            CancellationToken disposeToken)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _url = url;
            _channelId = channelId;
            _disposeToken = disposeToken;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _response = await _httpClientFactory
                        .CreateClient(NamedClient.Default)
                        .GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                    _response.EnsureSuccessStatusCode();
                    _inner = await _response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DirectLiveTuner channel {ChannelId} connect failed, retrying.", _channelId);
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_disposeToken, cancellationToken);
            CancellationToken token = linked.Token;

            while (!token.IsCancellationRequested)
            {
                if (_inner != null)
                {
                    try
                    {
                        int n = await _inner.ReadAsync(buffer, token).ConfigureAwait(false);
                        if (n > 0)
                        {
                            return n;
                        }

                        _logger.LogDebug("DirectLiveTuner channel {ChannelId} upstream EOF, reconnecting.", _channelId);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "DirectLiveTuner channel {ChannelId} read error, reconnecting.", _channelId);
                    }

                    await _inner.DisposeAsync().ConfigureAwait(false);
                    _response?.Dispose();
                    _inner = null;
                    _response = null;
                }

                try
                {
                    await ConnectAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _inner?.Dispose();
                _response?.Dispose();
                _disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
