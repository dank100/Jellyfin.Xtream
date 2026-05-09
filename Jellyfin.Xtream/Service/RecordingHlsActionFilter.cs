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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Global MVC action filter that intercepts DynamicHls transcode requests for recording
/// channels and redirects them to our direct HLS endpoint. This bypasses ffmpeg entirely.
/// Registered as a global filter via MvcOptions — works from plugins unlike IStartupFilter.
/// </summary>
public class RecordingHlsActionFilter : IActionFilter
{
    private const string RecordingMarker = "xtream_rec_";
    private readonly ILogger<RecordingHlsActionFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingHlsActionFilter"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public RecordingHlsActionFilter(ILogger<RecordingHlsActionFilter> logger)
    {
        _logger = logger;
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
            "Intercepting DynamicHls request for recording {TimerId}, redirecting to direct HLS",
            timerId);

        // Redirect all clients (including Android TV) to our direct HLS endpoint.
        // This bypasses ffmpeg entirely — the recording segments are already in a
        // playable format (H264+AAC in MPEG-TS). Letting ffmpeg process the HLS input
        // causes exit code 234 crashes on seek, producing black screens.
        string redirectUrl = $"/Xtream/Recordings/{timerId}/stream.m3u8";
        context.Result = new RedirectResult(redirectUrl, permanent: false);
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
