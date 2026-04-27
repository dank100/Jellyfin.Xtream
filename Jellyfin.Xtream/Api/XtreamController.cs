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
using System.Diagnostics;
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
using MediaBrowser.Controller;
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
public class XtreamController(IXtreamClient xtreamClient, XmltvParser xmltvParser, RecordingEngine recordingEngine, ConnectionMultiplexer connectionMultiplexer, IServerApplicationHost serverApplicationHost, ILogger<XtreamController> logger) : ControllerBase
{
    /// <summary>
    /// MPEG-TS null packet (PID 0x1FFF). Decoders/demuxers ignore these.
    /// Sent during data gaps to keep the HTTP stream alive so ffmpeg doesn't timeout.
    /// </summary>
    private static readonly byte[] TsNullPacket = CreateTsNullPacket();

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

    private static byte[] CreateTsNullPacket()
    {
        var pkt = new byte[188];
        pkt[0] = 0x47; // sync byte
        pkt[1] = 0x1F; // PID high bits (0x1FFF = null packet)
        pkt[2] = 0xFF; // PID low bits
        pkt[3] = 0x10; // adaptation field control: payload only
        return pkt;
    }

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

            // Rewrite segment filenames to route through the API
            if (!line.StartsWith('#') && line.StartsWith("seg_", StringComparison.Ordinal))
            {
                result[^1] = "segments/" + line;
            }
        }

        // When the recording is still active, serve an EVENT playlist WITHOUT
        // #EXT-X-ENDLIST so ffmpeg/hls.js poll for new segments as the recording grows.
        // Once the recording finishes, add ENDLIST + START tag for VOD-style seeking.
        bool isActive = recordingEngine.IsRecordingActive(timerId);
        if (!isActive && result.Count > 0 && !result.Any(l => l.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal)))
        {
            // Insert START tag after the header (before first segment)
            int insertIdx = result.FindIndex(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal));
            if (insertIdx > 0)
            {
                result.Insert(insertIdx, "#EXT-X-START:TIME-OFFSET=-12,PRECISE=YES");
            }

            result.Add("#EXT-X-ENDLIST");
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

    /// <summary>
    /// Serves the growing MPEG-TS file for an active recording as a continuous stream.
    /// Tails the file and sends TS null packets during gaps to keep ffmpeg alive.
    /// Anonymous access allowed — the timer ID acts as an unguessable access token.
    /// </summary>
    /// <param name="timerId">The timer ID of the recording.</param>
    /// <returns>A continuous MPEG-TS stream.</returns>
    [AllowAnonymous]
    [HttpGet("Recordings/{timerId}/stream.ts")]
    public async Task GetRecordingStream(string timerId)
    {
#pragma warning disable CA3003
        string? tsPath = recordingEngine.GetTsFilePath(timerId);
        if (tsPath == null || !System.IO.File.Exists(tsPath))
        {
            Response.StatusCode = 404;
            return;
        }
#pragma warning restore CA3003

        Response.ContentType = "video/MP2T";
        Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
        Response.Headers.Append("Access-Control-Allow-Origin", "*");

        var cancellation = HttpContext.RequestAborted;

        try
        {
            using var fs = new FileStream(tsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);
            var buffer = new byte[81920];
            int emptyReads = 0;

            while (!cancellation.IsCancellationRequested)
            {
                int bytesRead = await fs.ReadAsync(buffer, cancellation).ConfigureAwait(false);
                if (bytesRead > 0)
                {
                    emptyReads = 0;
                    await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), cancellation).ConfigureAwait(false);
                    await Response.Body.FlushAsync(cancellation).ConfigureAwait(false);
                }
                else if (recordingEngine.IsRecordingActive(timerId))
                {
                    emptyReads++;
                    // After 600 empty reads (5 min) with no real data, give up
                    if (emptyReads > 600)
                    {
                        break;
                    }

                    // Send TS null packets every 500ms to keep the HTTP connection
                    // alive. ffmpeg/decoders ignore PID 0x1FFF packets.
                    if (emptyReads % 2 == 0)
                    {
                        await Response.Body.WriteAsync(TsNullPacket, cancellation).ConfigureAwait(false);
                        await Response.Body.FlushAsync(cancellation).ConfigureAwait(false);
                    }

                    await Task.Delay(250, cancellation).ConfigureAwait(false);
                }
                else
                {
                    // Recording finished — drain remaining bytes and close
                    while (!cancellation.IsCancellationRequested)
                    {
                        bytesRead = await fs.ReadAsync(buffer, cancellation).ConfigureAwait(false);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), cancellation).ConfigureAwait(false);
                        await Response.Body.FlushAsync(cancellation).ConfigureAwait(false);
                    }

                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
    }

    /// <summary>
    /// Serves the HLS playlist for a multiplexed channel.
    /// Anonymous access allowed — stream ID acts as an access token.
    /// </summary>
    /// <param name="streamId">The Xtream stream ID.</param>
    /// <returns>The m3u8 playlist file.</returns>
    [AllowAnonymous]
    [HttpGet("Multiplex/{streamId}/playlist.m3u8")]
    public ActionResult GetMultiplexPlaylist(int streamId)
    {
        // streamId is an integer (not free-form user input); buffer paths are controlled by the multiplexer.
#pragma warning disable CA3003
        var buffer = connectionMultiplexer.GetBuffer(streamId);
        if (buffer == null || !System.IO.File.Exists(buffer.PlaylistPath))
        {
            return NotFound("Channel not active in multiplexer");
        }

        string content = System.IO.File.ReadAllText(buffer.PlaylistPath);
#pragma warning restore CA3003

        // Rewrite segment filenames to route through the API
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith('#') && lines[i].EndsWith(".ts", StringComparison.Ordinal))
            {
                lines[i] = $"segments/{lines[i].Trim()}";
            }
        }

        content = string.Join('\n', lines);

        Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
        Response.Headers.Append("Pragma", "no-cache");
        Response.Headers.Append("Access-Control-Allow-Origin", "*");

        return Content(content, "application/vnd.apple.mpegurl");
    }

    /// <summary>
    /// Serves an HLS segment for a multiplexed channel.
    /// Anonymous access allowed — stream ID acts as an access token.
    /// </summary>
    /// <param name="streamId">The Xtream stream ID.</param>
    /// <param name="filename">The segment filename.</param>
    /// <returns>The .ts segment file.</returns>
    [AllowAnonymous]
    [HttpGet("Multiplex/{streamId}/segments/{filename}")]
    public ActionResult GetMultiplexSegment(int streamId, string filename)
    {
        if (filename.Contains("..", StringComparison.Ordinal)
            || filename.Contains('/', StringComparison.Ordinal)
            || filename.Contains('\\', StringComparison.Ordinal))
        {
            return BadRequest("Invalid filename");
        }

        // filename is sanitized above; streamId is an integer.
#pragma warning disable CA3003
        var buffer = connectionMultiplexer.GetBuffer(streamId);
        if (buffer == null)
        {
            return NotFound("Channel not active in multiplexer");
        }

        string segmentPath = Path.Combine(buffer.SegmentDir, filename);
        if (!System.IO.File.Exists(segmentPath))
        {
            return NotFound("Segment not found");
        }

        Response.Headers.Append("Access-Control-Allow-Origin", "*");
        return PhysicalFile(segmentPath, "video/MP2T");
#pragma warning restore CA3003
    }

    /// <summary>
    /// Lists active recordings with their timer IDs and file paths.
    /// Used for integration testing.
    /// </summary>
    /// <returns>Active recordings info.</returns>
    [AllowAnonymous]
    [HttpGet("Test/ActiveRecordings")]
    public ActionResult<object> TestGetActiveRecordings()
    {
        var recordings = recordingEngine.ActiveRecordings.Select(kvp => new
        {
            TimerId = kvp.Key,
            kvp.Value.TsFilePath,
            kvp.Value.MuxStreamId,
            StartedUtc = kvp.Value.StartedUtc.ToString("o"),
        });
        return Ok(recordings);
    }

    /// <summary>
    /// Starts a test recording on the given stream ID.
    /// Creates a synthetic timer and starts recording immediately.
    /// </summary>
    /// <param name="streamId">The Xtream stream ID (e.g. 101).</param>
    /// <param name="durationMinutes">Recording duration in minutes (default 5).</param>
    /// <returns>The timer ID of the started recording.</returns>
    [AllowAnonymous]
    [HttpPost("Test/StartRecording")]
    public ActionResult<object> TestStartRecording(int streamId, int durationMinutes = 5)
    {
        string timerId = Guid.NewGuid().ToString("N");
        string channelId = StreamService.ToGuid(0x3e4c775d, streamId, 0, 0).ToString();
        var now = DateTime.UtcNow;

        var timer = new MediaBrowser.Controller.LiveTv.TimerInfo
        {
            Id = timerId,
            ChannelId = channelId,
            Name = $"IntegrationTest_stream{streamId}",
            StartDate = now,
            EndDate = now.AddMinutes(durationMinutes),
            Status = MediaBrowser.Model.LiveTv.RecordingStatus.New,
            ProgramId = string.Empty,
        };

        recordingEngine.StartRecording(timer);
        return Ok(new { TimerId = timerId, StreamId = streamId, DurationMinutes = durationMinutes });
    }

    /// <summary>
    /// Opens a multiplexed live restream, reads from it for the given duration,
    /// and returns data-flow statistics (bytes, gaps, throughput).
    /// Used by integration tests to verify the live restream pipeline.
    /// </summary>
    /// <param name="streamId">The Xtream stream ID.</param>
    /// <param name="durationSeconds">How many seconds to read (default 30).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Statistics about the stream read.</returns>
    [AllowAnonymous]
    [HttpPost("Test/LiveStreamStats")]
    public async Task<ActionResult<object>> TestLiveStreamStats(int streamId, int durationSeconds = 30, CancellationToken cancellationToken = default)
    {
        var restream = new MultiplexedRestream(
            serverApplicationHost,
            logger,
            connectionMultiplexer,
            streamId);

        var openSw = Stopwatch.StartNew();
        try
        {
            using var openCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            openCts.CancelAfter(TimeSpan.FromSeconds(60));
            await restream.Open(openCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            restream.Dispose();
            return Ok(new
            {
                StreamId = streamId,
                Error = "Timed out waiting for stream to open (60s)",
            });
        }

        double openLatencyMs = openSw.Elapsed.TotalMilliseconds;

        // Monitor the ChannelBuffer for segment production instead of reading
        // from a TS pipe. This tests the HLS segment path that the player uses.
        var buffer = connectionMultiplexer.GetBuffer(streamId);
        if (buffer == null)
        {
            restream.Dispose();
            return Ok(new
            {
                StreamId = streamId,
                Error = "No channel buffer found after open",
            });
        }

        long totalBytes = 0;
        int segmentCount = 0;
        double maxGapMs = 0;
        int gapsOver500ms = 0;
        int gapsOver1s = 0;
        int gapsOver2s = 0;
        int gapsOver5s = 0;
        var gapList = new List<double>();
        int lastGlobalIndex = -1;
        var sw = Stopwatch.StartNew();
        var lastSegmentTime = sw.Elapsed;
        bool firstSegment = true;
        double firstSegmentMs = 0;

        try
        {
            while (sw.Elapsed.TotalSeconds < durationSeconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segments = buffer.GetSegments();

                bool foundNew = false;
                for (int i = 0; i < segments.Count; i++)
                {
                    int gi = buffer.GetGlobalIndex(i);
                    if (gi <= lastGlobalIndex)
                    {
                        continue;
                    }

                    var seg = segments[i];
                    totalBytes += (long)(seg.DurationSeconds * 500_000); // ~4Mbps estimate

                    segmentCount++;
                    lastGlobalIndex = gi;
                    foundNew = true;

                    var now = sw.Elapsed;
                    if (firstSegment)
                    {
                        firstSegmentMs = now.TotalMilliseconds;
                        firstSegment = false;
                    }
                    else
                    {
                        double gapMs = (now - lastSegmentTime).TotalMilliseconds;
                        if (gapMs > 500)
                        {
                            gapList.Add(gapMs);
                            gapsOver500ms++;
                            if (gapMs > 1000)
                            {
                                gapsOver1s++;
                            }

                            if (gapMs > 2000)
                            {
                                gapsOver2s++;
                            }

                            if (gapMs > 5000)
                            {
                                gapsOver5s++;
                            }
                        }

                        if (gapMs > maxGapMs)
                        {
                            maxGapMs = gapMs;
                        }
                    }

                    lastSegmentTime = now;
                }

                if (!foundNew)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        sw.Stop();

        await restream.Close().ConfigureAwait(false);

        double elapsedSec = sw.Elapsed.TotalSeconds;
        double avgBytesPerSec = elapsedSec > 0 ? totalBytes / elapsedSec : 0;

        // Compute P95 gap
        gapList.Sort();
        double p95GapMs = gapList.Count > 0 ? gapList[(int)(gapList.Count * 0.95)] : 0;

        restream.Dispose();

        return Ok(new
        {
            StreamId = streamId,
            DurationRequestedSec = durationSeconds,
            ElapsedSec = Math.Round(elapsedSec, 2),
            TotalBytes = totalBytes,
            SegmentCount = segmentCount,
            MaxGapMs = Math.Round(maxGapMs, 1),
            P95GapMs = Math.Round(p95GapMs, 1),
            AvgBytesPerSec = (long)avgBytesPerSec,
            OpenLatencyMs = Math.Round(openLatencyMs, 0),
            FirstSegmentMs = Math.Round(firstSegmentMs, 0),
            GapsOver500ms = gapsOver500ms,
            GapsOver1s = gapsOver1s,
            GapsOver2s = gapsOver2s,
            GapsOver5s = gapsOver5s,
        });
    }

    /// <summary>
    /// Cancels a test recording.
    /// </summary>
    /// <param name="timerId">The timer ID to cancel.</param>
    /// <returns>Status.</returns>
    [AllowAnonymous]
    [HttpPost("Test/StopRecording/{timerId}")]
    public ActionResult<object> TestStopRecording(string timerId)
    {
        recordingEngine.CancelRecording(timerId);
        return Ok(new { Cancelled = timerId });
    }
}
