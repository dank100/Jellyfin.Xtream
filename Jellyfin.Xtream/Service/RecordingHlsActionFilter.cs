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
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Global MVC action filter that intercepts DynamicHls transcode requests for recording
/// channels and serves our HLS playlist inline. This bypasses ffmpeg entirely — the
/// recording segments are already in a playable format (H264+AAC in MPEG-TS).
/// Registered as a global filter via MvcOptions — works from plugins unlike IStartupFilter.
/// </summary>
public class RecordingHlsActionFilter : IActionFilter
{
    private const string RecordingMarker = "xtream_rec_";
    private readonly ILogger<RecordingHlsActionFilter> _logger;
    private readonly RecordingEngine _recordingEngine;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingHlsActionFilter"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="recordingEngine">The recording engine singleton.</param>
    public RecordingHlsActionFilter(ILogger<RecordingHlsActionFilter> logger, RecordingEngine recordingEngine)
    {
        _logger = logger;
        _recordingEngine = recordingEngine;
    }

    /// <inheritdoc />
    public void OnActionExecuting(ActionExecutingContext context)
    {
        string path = context.HttpContext.Request.Path.Value ?? string.Empty;

        // Only intercept DynamicHls master/main/live playlist requests
        if (!path.Contains("/master.m3u8", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("/main.m3u8", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("/live.m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Try to extract the timer ID from LiveStreamId or mediaSourceId
        string? timerId = ExtractTimerIdFromQuery(context);

        if (timerId == null)
        {
            return;
        }

        _logger.LogInformation(
            "Intercepting DynamicHls request for recording {TimerId}, serving HLS inline",
            timerId);

        // Serve the recording HLS playlist inline instead of redirecting.
        // Redirects (302) break some HLS players (ExoPlayer, AVPlayer) because they
        // don't follow redirects for m3u8 playlists correctly. Serving the content
        // inline with absolute segment URLs avoids this entirely.
        // timerId is not used to build paths directly — GetHlsDirectory performs a dictionary
        // lookup that only returns paths the plugin itself created for active recordings.
#pragma warning disable CA3003
        string? hlsDir = _recordingEngine.GetHlsDirectory(timerId);
        if (hlsDir == null || !Directory.Exists(hlsDir))
        {
            _logger.LogWarning("Recording HLS directory not found for timer {TimerId}", timerId);
            context.Result = new NotFoundResult();
            return;
        }

        string playlistPath = Path.Combine(hlsDir, "live.m3u8");
        if (!File.Exists(playlistPath))
        {
            _logger.LogWarning("Recording playlist not yet available for timer {TimerId}", timerId);
            context.Result = new NotFoundResult();
            return;
        }

        string[] lines = File.ReadAllLines(playlistPath);
        bool isActive = _recordingEngine.IsRecordingActive(timerId);
#pragma warning restore CA3003

        // Build the base URL for absolute segment references
        var request = context.HttpContext.Request;
        string baseUrl = $"{request.Scheme}://{request.Host}/Xtream/Recordings/{timerId}";

        var result = new List<string>();
        foreach (string line in lines)
        {
            // Rewrite segment filenames to absolute URLs through our API
            if (!line.StartsWith('#') && line.StartsWith("seg_", StringComparison.Ordinal))
            {
                result.Add($"{baseUrl}/segments/{line}");
            }
            else
            {
                result.Add(line);
            }
        }

        // Add START tag for player positioning
        if (isActive)
        {
            int insertIdx = result.FindIndex(l => l.StartsWith("#EXT-X-TARGETDURATION", StringComparison.Ordinal));
            if (insertIdx >= 0)
            {
                result.Insert(insertIdx + 1, "#EXT-X-START:TIME-OFFSET=0,PRECISE=YES");
            }
        }

        // Add ENDLIST for completed recordings
        if (!isActive && result.Count > 0 && !result.Any(l => l.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal)))
        {
            int insertIdx = result.FindIndex(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal));
            if (insertIdx > 0)
            {
                result.Insert(insertIdx, "#EXT-X-START:TIME-OFFSET=-12,PRECISE=YES");
            }

            result.Add("#EXT-X-ENDLIST");
        }

        string content = string.Join('\n', result);

        context.HttpContext.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        context.HttpContext.Response.Headers["Pragma"] = "no-cache";
        context.HttpContext.Response.Headers["Access-Control-Allow-Origin"] = "*";

        context.Result = new ContentResult
        {
            Content = content,
            ContentType = "application/vnd.apple.mpegurl",
            StatusCode = 200,
        };
    }

    /// <summary>
    /// Tries to extract the recording timer ID from query parameters.
    /// Checks LiveStreamId first, then falls back to mediaSourceId.
    /// </summary>
    private string? ExtractTimerIdFromQuery(ActionExecutingContext context)
    {
        // Try LiveStreamId (used when opened via live TV path)
        string liveStreamId = context.HttpContext.Request.Query["LiveStreamId"].ToString();
        if (string.IsNullOrEmpty(liveStreamId))
        {
            liveStreamId = context.HttpContext.Request.Query["liveStreamId"].ToString();
        }

        if (string.IsNullOrEmpty(liveStreamId) && context.ActionArguments.TryGetValue("liveStreamId", out var argValue))
        {
            liveStreamId = argValue?.ToString() ?? string.Empty;
        }

        if (!string.IsNullOrEmpty(liveStreamId) && liveStreamId.Contains(RecordingMarker, StringComparison.Ordinal))
        {
            int markerIdx = liveStreamId.IndexOf(RecordingMarker, StringComparison.Ordinal);
            return liveStreamId.Substring(markerIdx + RecordingMarker.Length);
        }

        // Fallback: check mediaSourceId (used when opened as a regular video/.strm item)
        string mediaSourceId = context.HttpContext.Request.Query["mediaSourceId"].ToString();
        if (string.IsNullOrEmpty(mediaSourceId) && context.ActionArguments.TryGetValue("mediaSourceId", out var msValue))
        {
            mediaSourceId = msValue?.ToString() ?? string.Empty;
        }

        if (!string.IsNullOrEmpty(mediaSourceId) && mediaSourceId.StartsWith(RecordingMarker, StringComparison.Ordinal))
        {
            return mediaSourceId.Substring(RecordingMarker.Length);
        }

        return null;
    }

    /// <inheritdoc />
    public void OnActionExecuted(ActionExecutedContext context)
    {
        // No-op
    }
}
