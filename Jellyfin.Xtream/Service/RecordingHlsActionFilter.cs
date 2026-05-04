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

        // Only intercept DynamicHls master/main playlist requests
        if (!path.Contains("/master.m3u8", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("/main.m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string liveStreamId = context.HttpContext.Request.Query["LiveStreamId"].ToString();
        if (string.IsNullOrEmpty(liveStreamId) || !liveStreamId.Contains(RecordingMarker, StringComparison.Ordinal))
        {
            return;
        }

        // Extract timerId from LiveStreamId: "{hash}_{hash}_xtream_rec_{timerId}"
        int markerIdx = liveStreamId.IndexOf(RecordingMarker, StringComparison.Ordinal);
        string timerId = liveStreamId.Substring(markerIdx + RecordingMarker.Length);

        _logger.LogInformation(
            "Intercepting DynamicHls request for recording {TimerId}, redirecting to direct HLS",
            timerId);

        // Short-circuit the action — redirect client to our direct HLS endpoint
        string redirectUrl = $"/Xtream/Recordings/{timerId}/stream.m3u8";
        context.Result = new RedirectResult(redirectUrl, permanent: false);
    }

    /// <inheritdoc />
    public void OnActionExecuted(ActionExecutedContext context)
    {
        // No-op
    }
}
