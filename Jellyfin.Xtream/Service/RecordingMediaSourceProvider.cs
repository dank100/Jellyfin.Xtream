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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A custom <see cref="IMediaSourceProvider"/> that intercepts playback of active
/// recording library items (.strm files) and provides a DVR-style media source
/// with finite duration from EPG data, bypassing the live TV pipeline's
/// Normalize() which forces IsInfiniteStream=true.
/// </summary>
public partial class RecordingMediaSourceProvider : IMediaSourceProvider
{
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<RecordingMediaSourceProvider> _logger;
    private readonly RecordingEngine _recordingEngine;
    private readonly ConnectionMultiplexer _multiplexer;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingMediaSourceProvider"/> class.
    /// </summary>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="recordingEngine">The recording engine singleton.</param>
    /// <param name="multiplexer">The connection multiplexer (for probing).</param>
    public RecordingMediaSourceProvider(
        IServerApplicationHost appHost,
        ILogger<RecordingMediaSourceProvider> logger,
        RecordingEngine recordingEngine,
        ConnectionMultiplexer multiplexer)
    {
        _appHost = appHost;
        _logger = logger;
        _recordingEngine = recordingEngine;
        _multiplexer = multiplexer;
    }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        // Only intercept items whose path matches our recording HLS URL pattern.
        // The .strm file contents become the item.Path in Jellyfin.
        string? path = item.Path;
        if (string.IsNullOrEmpty(path))
        {
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
        }

        // Parse as URI to match AbsolutePath (robust against host/port changes)
        string? timerId = ExtractTimerId(path);
        if (timerId == null)
        {
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
        }

        // Check if this recording is still active (or in the merge/serve window)
        var activeRec = _recordingEngine.GetActiveRecording(timerId);
        if (activeRec == null)
        {
            // Not an active recording — let the normal .strm handler deal with it
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
        }

        var timer = activeRec.Timer;

        // Calculate EPG duration including pre/post padding.
        // Setting RunTimeTicks + IsInfiniteStream=false bypasses the live TV pipeline's
        // nullification of RunTimeTicks, giving us CanSeek=true and a regular seekbar
        // from 0 to programme duration. The user can seek to any position within the
        // recorded content, and "jump to live" by seeking to the rightmost position.
        var start = timer.StartDate - TimeSpan.FromSeconds(timer.PrePaddingSeconds);
        var end = timer.EndDate + TimeSpan.FromSeconds(timer.PostPaddingSeconds);
        long durationTicks = (end - start).Ticks;

        _logger.LogInformation(
            "Providing DVR media source for active recording {TimerId} ({Name}), duration={Duration}",
            timerId,
            timer.Name,
            TimeSpan.FromTicks(durationTicks));

        // Point directly at the HLS m3u8 playlist URL — all clients (ExoPlayer, AVPlayer,
        // HLS.js) can seek natively via segment-level jumps in an EVENT playlist.
        string baseUrl = _appHost.GetApiUrlForLocalAccess()?.TrimEnd('/') ?? string.Empty;
        string hlsUrl = $"{baseUrl}/Xtream/Recordings/{timerId}/stream.m3u8";

        var source = new MediaSourceInfo
        {
            Id = $"xtream_rec_{timerId}",
            Path = hlsUrl,
            Protocol = MediaProtocol.Http,
            Container = "hls",
            RunTimeTicks = durationTicks,
            AnalyzeDurationMs = 500,
            IsInfiniteStream = false,
            SupportsDirectPlay = true,
            SupportsDirectStream = false,
            SupportsTranscoding = false,
            SupportsProbing = false,
            IsRemote = false,
            ReadAtNativeFramerate = false,
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

        return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { source });
    }

    /// <inheritdoc />
    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        // The openToken has the provider prefix prepended by MediaSourceManager.SetKeyProperties.
        // Strip the prefix to get the raw timer ID.
        string timerId = openToken;
        if (openToken.Length > 33 && openToken[32] == '_')
        {
            timerId = openToken[33..];
        }

        _logger.LogInformation("OpenMediaSource called for recording timer {TimerId} (raw token: {Token})", timerId, openToken);

        var activeRec = _recordingEngine.GetActiveRecording(timerId);
        if (activeRec == null)
        {
            throw new InvalidOperationException($"Recording {timerId} is no longer active");
        }

        var restream = new RecordingRestream(
            _appHost,
            _logger,
            _recordingEngine,
            _multiplexer,
            timerId,
            activeRec.Timer);

        return Task.FromResult<ILiveStream>(restream);
    }

    /// <summary>
    /// Extracts the timer ID from a recording HLS URL.
    /// </summary>
    private static string? ExtractTimerId(string path)
    {
        // Try to parse as a URI first for robustness
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            var match = RecordingUrlPattern().Match(uri.AbsolutePath);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        // Fallback: match against the raw string
        var rawMatch = RecordingUrlPattern().Match(path);
        return rawMatch.Success ? rawMatch.Groups[1].Value : null;
    }

    // Matches /Xtream/Recordings/{timerId}/stream.m3u8 in the item's path URL
    [GeneratedRegex(@"/Xtream/Recordings/([^/]+)/stream\.m3u8$", RegexOptions.IgnoreCase)]
    private static partial Regex RecordingUrlPattern();
}
