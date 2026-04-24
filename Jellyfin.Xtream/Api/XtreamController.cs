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
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Api.Models;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// The Jellyfin Xtream configuration API.
/// </summary>
[ApiController]
[Route("[controller]")]
[Produces(MediaTypeNames.Application.Json)]
public class XtreamController(IXtreamClient xtreamClient, XmltvParser xmltvParser, RecordingEngine recordingEngine) : ControllerBase
{
    private static CategoryResponse CreateCategoryResponse(Category category) =>
        new()
        {
            Id = category.CategoryId,
            Name = category.CategoryName,
        };

    private static ItemResponse CreateItemResponse(StreamInfo stream) =>
        new()
        {
            Id = stream.StreamId,
            Name = stream.Name,
            HasCatchup = stream.TvArchive,
            CatchupDuration = stream.TvArchiveDuration,
        };

    private static ItemResponse CreateItemResponse(Series series) =>
        new()
        {
            Id = series.SeriesId,
            Name = series.Name,
            HasCatchup = false,
            CatchupDuration = 0,
        };

    private static ChannelResponse CreateChannelResponse(StreamInfo stream) =>
        new()
        {
            Id = stream.StreamId,
            LogoUrl = stream.StreamIcon,
            Name = stream.Name,
            Number = stream.Num,
        };

    /// <summary>
    /// Test the configured provider.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the categories.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("TestProvider")]
    public async Task<ActionResult<ProviderTestResponse>> TestProvider(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        PlayerApi info = await xtreamClient.GetUserAndServerInfoAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
        return Ok(new ProviderTestResponse()
        {
            ActiveConnections = info.UserInfo.ActiveCons,
            ExpiryDate = info.UserInfo.ExpDate,
            MaxConnections = info.UserInfo.MaxConnections,
            ServerTime = info.ServerInfo.TimeNow,
            ServerTimezone = info.ServerInfo.Timezone,
            Status = info.UserInfo.Status,
            SupportsMpegTs = info.UserInfo.AllowedOutputFormats.Contains("ts"),
        });
    }

    /// <summary>
    /// Get all Live TV categories.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the categories.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("LiveCategories")]
    public async Task<ActionResult<IEnumerable<CategoryResponse>>> GetLiveCategories(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<Category> categories = await xtreamClient.GetLiveCategoryAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
        return Ok(categories.Select(CreateCategoryResponse));
    }

    /// <summary>
    /// Get all Live TV streams for the given category.
    /// </summary>
    /// <param name="categoryId">The category for which to fetch the streams.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the streams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("LiveCategories/{categoryId}")]
    public async Task<ActionResult<IEnumerable<StreamInfo>>> GetLiveStreams(int categoryId, CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<StreamInfo> streams = await xtreamClient.GetLiveStreamsByCategoryAsync(
          plugin.Creds,
          categoryId,
          cancellationToken).ConfigureAwait(false);
        return Ok(streams.Select(CreateItemResponse));
    }

    /// <summary>
    /// Get all VOD categories.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the categories.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("VodCategories")]
    public async Task<ActionResult<IEnumerable<CategoryResponse>>> GetVodCategories(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<Category> categories = await xtreamClient.GetVodCategoryAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
        return Ok(categories.Select(CreateCategoryResponse));
    }

    /// <summary>
    /// Get all VOD streams for the given category.
    /// </summary>
    /// <param name="categoryId">The category for which to fetch the streams.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the streams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("VodCategories/{categoryId}")]
    public async Task<ActionResult<IEnumerable<StreamInfo>>> GetVodStreams(int categoryId, CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<StreamInfo> streams = await xtreamClient.GetVodStreamsByCategoryAsync(
          plugin.Creds,
          categoryId,
          cancellationToken).ConfigureAwait(false);
        return Ok(streams.Select(CreateItemResponse));
    }

    /// <summary>
    /// Get all Series categories.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the categories.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("SeriesCategories")]
    public async Task<ActionResult<IEnumerable<CategoryResponse>>> GetSeriesCategories(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<Category> categories = await xtreamClient.GetSeriesCategoryAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
        return Ok(categories.Select(CreateCategoryResponse));
    }

    /// <summary>
    /// Get all Series streams for the given category.
    /// </summary>
    /// <param name="categoryId">The category for which to fetch the streams.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the streams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("SeriesCategories/{categoryId}")]
    public async Task<ActionResult<IEnumerable<StreamInfo>>> GetSeriesStreams(int categoryId, CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<Series> series = await xtreamClient.GetSeriesByCategoryAsync(
          plugin.Creds,
          categoryId,
          cancellationToken).ConfigureAwait(false);
        return Ok(series.Select(CreateItemResponse));
    }

    /// <summary>
    /// Get all configured TV channels.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the streams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("LiveTv")]
    public async Task<ActionResult<IEnumerable<StreamInfo>>> GetLiveTvChannels(CancellationToken cancellationToken)
    {
        IEnumerable<StreamInfo> streams = await Plugin.Instance.StreamService.GetLiveStreams(cancellationToken).ConfigureAwait(false);
        var channels = streams.Select(CreateChannelResponse).ToList();
        return Ok(channels);
    }

    /// <summary>
    /// Get all channels from an XMLTV EPG source.
    /// </summary>
    /// <param name="sourceId">The EPG source ID.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the XMLTV channels.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("EpgChannels/{sourceId}")]
    public async Task<ActionResult<IEnumerable<XmltvChannel>>> GetEpgChannels(string sourceId, CancellationToken cancellationToken)
    {
        EpgSource? source = Plugin.Instance.Configuration.EpgSources.FirstOrDefault(s => s.Id == sourceId);
        if (source == null)
        {
            return NotFound();
        }

        var channels = await xmltvParser.GetChannelsAsync(source, cancellationToken).ConfigureAwait(false);
        return Ok(channels);
    }

    /// <summary>
    /// Serves the HLS playlist for an active recording.
    /// Anonymous access allowed — the timer ID acts as an unguessable access token.
    /// </summary>
    /// <param name="timerId">The timer ID of the recording.</param>
    /// <returns>The m3u8 playlist file.</returns>
    [AllowAnonymous]
    [HttpGet("Recordings/{timerId}/stream.m3u8")]
    [Produces("application/vnd.apple.mpegurl")]
    public ActionResult GetRecordingPlaylist(string timerId)
    {
        // timerId is not used to build paths directly — GetHlsDirectory performs a dictionary
        // lookup that only returns paths the plugin itself created for active recordings.
#pragma warning disable CA3003
        string? hlsDir = recordingEngine.GetHlsDirectory(timerId);
        if (hlsDir == null || !Directory.Exists(hlsDir))
        {
            return NotFound("Recording not active or not found");
        }

        string playlistPath = Path.Combine(hlsDir, "live.m3u8");
        if (!System.IO.File.Exists(playlistPath))
        {
            return NotFound("Playlist not yet available");
        }

        string[] lines = System.IO.File.ReadAllLines(playlistPath);
#pragma warning restore CA3003

        var result = new List<string>();
        foreach (string line in lines)
        {
            result.Add(line);

            // After the #EXTM3U header, inject a start-offset tag so players and
            // ffmpeg begin at the first segment instead of the live edge.
            if (line.StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                result.Add("#EXT-X-START:TIME-OFFSET=0,PRECISE=YES");
            }

            // Rewrite segment filenames to route through the API
            if (!line.StartsWith('#') && line.StartsWith("seg_", StringComparison.Ordinal))
            {
                result[^1] = "segments/" + line;
            }
        }

        string content = string.Join('\n', result);

        Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
        Response.Headers.Append("Pragma", "no-cache");
        Response.Headers.Append("Access-Control-Allow-Origin", "*");

        return Content(content, "application/vnd.apple.mpegurl");
    }

    /// <summary>
    /// Serves an HLS segment for an active recording.
    /// Anonymous access allowed — the timer ID acts as an unguessable access token.
    /// </summary>
    /// <param name="timerId">The timer ID of the recording.</param>
    /// <param name="filename">The segment filename.</param>
    /// <returns>The .ts segment file.</returns>
    [AllowAnonymous]
    [HttpGet("Recordings/{timerId}/segments/{filename}")]
    public ActionResult GetRecordingSegment(string timerId, string filename)
    {
        // Sanitize filename to prevent path traversal
        if (filename.Contains("..", StringComparison.Ordinal)
            || filename.Contains('/', StringComparison.Ordinal)
            || filename.Contains('\\', StringComparison.Ordinal))
        {
            return BadRequest("Invalid filename");
        }

        // timerId is validated via GetHlsDirectory (dictionary lookup of known active recordings).
        // filename is sanitized above.
#pragma warning disable CA3003
        string? hlsDir = recordingEngine.GetHlsDirectory(timerId);
        if (hlsDir == null || !Directory.Exists(hlsDir))
        {
            return NotFound("Recording not active or not found");
        }

        string segmentPath = Path.Combine(hlsDir, filename);
        if (!System.IO.File.Exists(segmentPath))
        {
            return NotFound("Segment not found");
        }

        Response.Headers.Append("Access-Control-Allow-Origin", "*");
        return PhysicalFile(segmentPath, "video/MP2T");
#pragma warning restore CA3003
    }
}
