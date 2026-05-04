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
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Middleware that intercepts Jellyfin's DynamicHls transcode requests for recording
/// channels and redirects them to our direct HLS endpoint. This bypasses ffmpeg entirely,
/// giving instant playback and full seeking on all clients (web, Android TV, iOS).
/// </summary>
public class RecordingHlsMiddleware
{
    private const string RecordingMarker = "xtream_rec_";
    private readonly RequestDelegate _next;
    private readonly ILogger<RecordingHlsMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingHlsMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">Logger instance.</param>
    public RecordingHlsMiddleware(RequestDelegate next, ILogger<RecordingHlsMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Processes the request. If it's a DynamicHls request for a recording channel,
    /// redirects to our direct HLS endpoint.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task representing the async operation.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;

        // Match Jellyfin's DynamicHls endpoints: master.m3u8 and main.m3u8
        if (path.Contains("/master.m3u8", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/main.m3u8", StringComparison.OrdinalIgnoreCase))
        {
            string? liveStreamId = context.Request.Query["LiveStreamId"].ToString();

            if (!string.IsNullOrEmpty(liveStreamId) && liveStreamId.Contains(RecordingMarker, StringComparison.Ordinal))
            {
                // Extract timerId from LiveStreamId format: "{hash}_{hash}_xtream_rec_{timerId}"
                int markerIdx = liveStreamId.IndexOf(RecordingMarker, StringComparison.Ordinal);
                string timerId = liveStreamId.Substring(markerIdx + RecordingMarker.Length);

                _logger.LogInformation(
                    "Redirecting recording transcode to direct HLS for timer {TimerId}",
                    timerId);

                // Redirect to our direct HLS endpoint (no ffmpeg needed)
                string redirectUrl = $"/Xtream/Recordings/{timerId}/stream.m3u8";
                context.Response.Redirect(redirectUrl, permanent: false);
                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
