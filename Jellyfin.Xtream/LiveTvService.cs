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
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream;

/// <summary>
/// Class LiveTvService.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="LiveTvService"/> class.
/// </remarks>
/// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
/// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
/// <param name="memoryCache">Instance of the <see cref="IMemoryCache"/> interface.</param>
/// <param name="xtreamClient">Instance of the <see cref="IXtreamClient"/> interface.</param>
/// <param name="timerStore">Instance of the <see cref="TimerStore"/> class.</param>
/// <param name="xmltvParser">Instance of the <see cref="XmltvParser"/> class.</param>
/// <param name="serviceProvider">Instance of the <see cref="IServiceProvider"/> interface.</param>
public class LiveTvService(IServerApplicationHost appHost, IHttpClientFactory httpClientFactory, ILogger<LiveTvService> logger, IMemoryCache memoryCache, IXtreamClient xtreamClient, TimerStore timerStore, XmltvParser xmltvParser, IServiceProvider serviceProvider) : ILiveTvService, ISupportsDirectStreamProvider
{
    private readonly Dictionary<string, TimerInfo> _timers = timerStore.LoadTimers();
    private readonly Dictionary<string, SeriesTimerInfo> _seriesTimers = timerStore.LoadSeriesTimers();
    private readonly Dictionary<string, EpisodeHighWaterMark> _highWaterMarks = timerStore.LoadHighWaterMarks();

    private readonly Dictionary<string, string> _recordingChannelMap = new();

    // Lazy to break circular dependency (RecordingEngine → LiveTvService → RecordingEngine)
    private RecordingEngine? _recordingEngine;
    private ConnectionMultiplexer? _connectionMultiplexer;

    /// <inheritdoc />
    public string Name => "Xtream Live";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    private RecordingEngine RecordingEngine => _recordingEngine ??= serviceProvider.GetRequiredService<RecordingEngine>();

    private ConnectionMultiplexer ConnectionMultiplexer => _connectionMultiplexer ??= serviceProvider.GetRequiredService<ConnectionMultiplexer>();

    /// <summary>
    /// Gets a snapshot of current timers for the recording engine.
    /// </summary>
    /// <returns>A read-only list of current timer infos.</returns>
    public IReadOnlyList<TimerInfo> GetTimersSnapshot()
    {
        lock (_timers)
        {
            return _timers.Values.ToList();
        }
    }

    /// <summary>
    /// Updates a timer's status in the store and persists.
    /// </summary>
    /// <param name="timer">The timer to update.</param>
    public void UpdateTimerStatus(TimerInfo timer)
    {
        lock (_timers)
        {
            _timers[timer.Id] = timer;
            PersistTimers();
        }
    }

    private void PersistTimers()
    {
        timerStore.SaveTimers(_timers.Values);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<ChannelInfo> items = [];
        foreach (StreamInfo channel in await plugin.StreamService.GetLiveStreamsWithOverrides(cancellationToken).ConfigureAwait(false))
        {
            ParsedName parsed = StreamService.ParseName(channel.Name);
            items.Add(new ChannelInfo()
            {
                Id = StreamService.ToGuid(StreamService.LiveTvPrefix, channel.StreamId, 0, 0).ToString(),
                Number = channel.Num.ToString(CultureInfo.InvariantCulture),
                ImageUrl = channel.StreamIcon,
                Name = parsed.Title,
                Tags = parsed.Tags,
            });
        }

        // Add virtual channels for active recordings
        foreach (var rec in RecordingEngine.GetReadyRecordingsSnapshot())
        {
            string channelGuid = StreamService.ToGuid(StreamService.RecordingPrefix, rec.Timer.Id.GetHashCode(StringComparison.Ordinal) & 0x7FFFFFFF, 0, 0).ToString();
            _recordingChannelMap[channelGuid] = rec.Timer.Id;
            items.Add(new ChannelInfo()
            {
                Id = channelGuid,
                Name = $"● REC: {rec.Timer.Name}",
                Number = "0",
            });
        }

        return items;
    }

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        lock (_timers)
        {
            _timers.Remove(timerId);
            PersistTimers();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(info.Id))
        {
            info.Id = Guid.NewGuid().ToString("N");
        }

        lock (_timers)
        {
            _timers[info.Id] = info;
            PersistTimers();
        }

        logger.LogInformation("Timer created: {TimerId} for channel {ChannelId}", info.Id, info.ChannelId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
    {
        lock (_timers)
        {
            return Task.FromResult<IEnumerable<TimerInfo>>(_timers.Values.ToList());
        }
    }

    /// <inheritdoc />
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<SeriesTimerInfo>>(_seriesTimers.Values.ToList());
    }

    /// <inheritdoc />
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(info.Id))
        {
            info.Id = Guid.NewGuid().ToString("N");
        }

        // Ensure SeriesId is populated for matching against EPG programmes
        if (string.IsNullOrEmpty(info.SeriesId) && !string.IsNullOrEmpty(info.Name))
        {
            info.SeriesId = EpgSeriesIdentifier.GenerateSeriesId(info.Name);
        }

        _seriesTimers[info.Id] = info;
        timerStore.SaveSeriesTimers(_seriesTimers.Values);
        logger.LogInformation("Series timer created: {TimerId} with SeriesId={SeriesId}", info.Id, info.SeriesId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        _seriesTimers[info.Id] = info;
        timerStore.SaveSeriesTimers(_seriesTimers.Values);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
    {
        lock (_timers)
        {
            _timers[updatedTimer.Id] = updatedTimer;
            PersistTimers();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        _seriesTimers.Remove(timerId);
        timerStore.SaveSeriesTimers(_seriesTimers.Values);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        MediaSourceInfo source = await GetChannelStream(channelId, string.Empty, cancellationToken).ConfigureAwait(false);
        return [source];
    }

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task CloseLiveStream(string id, CancellationToken cancellationToken)
    {
        logger.LogInformation("Closing livestream {ChannelId}", id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
    {
        return Task.FromResult(new SeriesTimerInfo
        {
            PostPaddingSeconds = 120,
            PrePaddingSeconds = 120,
            RecordAnyChannel = false,
            RecordAnyTime = true,
            RecordNewOnly = false
        });
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        Guid guid = Guid.Parse(channelId);
        StreamService.FromGuid(guid, out int prefix, out int streamId, out int _, out int _);

        // Virtual recording channels: return a single programme spanning the EPG schedule
        if (prefix == StreamService.RecordingPrefix && _recordingChannelMap.TryGetValue(channelId, out string? timerId))
        {
            var activeRec = RecordingEngine.GetActiveRecording(timerId);
            if (activeRec != null)
            {
                var timer = activeRec.Timer;
                // Use the actual recording start time so the seekbar spans recorded content only.
                var start = activeRec.StartedUtc;
                var end = timer.EndDate + TimeSpan.FromSeconds(timer.PostPaddingSeconds);

                // Ensure the programme always covers "now" while the recording is active.
                // If the scheduled end has passed, extend it so the live TV slider stays in
                // time-of-day mode (Z = true) and the seekbar remains interactive.
                if (end < DateTime.UtcNow)
                {
                    end = DateTime.UtcNow.AddMinutes(30);
                }

                return new[]
                {
                    new ProgramInfo
                    {
                        Id = channelId + "_prog",
                        ChannelId = channelId,
                        StartDate = start,
                        EndDate = end,
                        Name = timer.Name,
                    },
                };
            }

            return Enumerable.Empty<ProgramInfo>();
        }

        if (prefix != StreamService.LiveTvPrefix)
        {
            throw new ArgumentException("Unsupported channel");
        }

        Plugin plugin = Plugin.Instance;
        plugin.Configuration.LiveTvOverrides.TryGetValue(streamId, out ChannelOverrides? overrides);
        string epgTz = overrides?.EpgTimezone ?? string.Empty;
        string myTz = plugin.Configuration.MyTimezone ?? string.Empty;
        string epgSourceId = overrides?.EpgSourceId ?? string.Empty;
        string xmltvChId = overrides?.XmltvChannelId ?? string.Empty;
        string key = $"xtream-epg-{channelId}-{epgTz}-{myTz}-{epgSourceId}-{xmltvChId}";

        ICollection<ProgramInfo>? items = null;
        if (memoryCache.TryGetValue(key, out ICollection<ProgramInfo>? o))
        {
            items = o;
        }
        else
        {
            items = new List<ProgramInfo>();
            TimeSpan epgShift = GetEpgShift(epgTz, myTz);

            // Check for external XMLTV source override
            EpgSource? epgSource = !string.IsNullOrEmpty(epgSourceId)
                ? plugin.Configuration.EpgSources.FirstOrDefault(s => s.Id == epgSourceId)
                : null;

            if (epgSource != null && !string.IsNullOrEmpty(xmltvChId))
            {
                var programmes = await xmltvParser.GetProgrammesAsync(epgSource, xmltvChId, cancellationToken).ConfigureAwait(false);
                int epgId = 0;
                foreach (var prog in programmes)
                {
                    var seriesInfo = EpgSeriesIdentifier.Parse(prog.Title, prog.Description);
                    items.Add(new()
                    {
                        Id = StreamService.ToGuid(StreamService.EpgPrefix, streamId, epgId++, 0).ToString(),
                        ChannelId = channelId,
                        StartDate = prog.Start + epgShift,
                        EndDate = prog.Stop + epgShift,
                        Name = prog.Title,
                        Overview = prog.Description,
                        ImageUrl = prog.Icon,
                        IsSeries = seriesInfo.IsSeries,
                        SeriesId = seriesInfo.IsSeries ? seriesInfo.SeriesId : null,
                        ShowId = seriesInfo.IsSeries ? seriesInfo.SeriesId : null,
                        SeasonNumber = seriesInfo.SeasonNumber,
                        EpisodeNumber = seriesInfo.EpisodeNumber,
                        EpisodeTitle = seriesInfo.EpisodeTitle,
                    });
                }
            }
            else
            {
                EpgListings epgs = await xtreamClient.GetEpgInfoAsync(plugin.Creds, streamId, cancellationToken).ConfigureAwait(false);
                foreach (EpgInfo epg in epgs.Listings)
                {
                    var seriesInfo = EpgSeriesIdentifier.Parse(epg.Title, epg.Description);
                    items.Add(new()
                    {
                        Id = StreamService.ToGuid(StreamService.EpgPrefix, streamId, epg.Id, 0).ToString(),
                        ChannelId = channelId,
                        StartDate = epg.Start + epgShift,
                        EndDate = epg.End + epgShift,
                        Name = epg.Title,
                        Overview = epg.Description,
                        IsSeries = seriesInfo.IsSeries,
                        SeriesId = seriesInfo.IsSeries ? seriesInfo.SeriesId : null,
                        ShowId = seriesInfo.IsSeries ? seriesInfo.SeriesId : null,
                        SeasonNumber = seriesInfo.SeasonNumber,
                        EpisodeNumber = seriesInfo.EpisodeNumber,
                        EpisodeTitle = seriesInfo.EpisodeTitle,
                    });
                }
            }

            memoryCache.Set(key, items, DateTimeOffset.Now.AddMinutes(10));
        }

        return from epg in items
               where epg.EndDate >= startDateUtc && epg.StartDate < endDateUtc
               select epg;
    }

    /// <inheritdoc />
    public Task ResetTuner(string id, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(string channelId, string streamId, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        Guid guid = Guid.Parse(channelId);
        StreamService.FromGuid(guid, out int prefix, out int channel, out int _, out int _);

        // Virtual recording channel: return a RecordingRestream pointing to the recording's HLS playlist
        if (prefix == StreamService.RecordingPrefix && _recordingChannelMap.TryGetValue(channelId, out string? timerId))
        {
            var activeRec = RecordingEngine.GetActiveRecording(timerId);
            if (activeRec == null)
            {
                throw new InvalidOperationException($"Recording {timerId} is no longer active");
            }

            var restream = new RecordingRestream(
                appHost,
                logger,
                RecordingEngine,
                ConnectionMultiplexer,
                timerId,
                activeRec.Timer);
            await restream.Open(cancellationToken).ConfigureAwait(false);
            restream.ConsumerCount++;

            return restream;
        }

        if (prefix != StreamService.LiveTvPrefix)
        {
            throw new ArgumentException("Unsupported channel");
        }

        Plugin plugin = Plugin.Instance;

        // If multiplexing is enabled, use the multiplexer for all live TV channels
        if (plugin.Configuration.EnableMultiplexing)
        {
            string muxTunerKey = $"multiplex_{channel}";
            ILiveStream? muxStream = currentLiveStreams.Find(s => s.TunerHostId == MultiplexedRestream.TunerHost && s.MediaSource.Id == muxTunerKey);
            if (muxStream == null)
            {
                muxStream = new MultiplexedRestream(appHost, logger, ConnectionMultiplexer, channel);
                await muxStream.Open(cancellationToken).ConfigureAwait(false);
            }

            muxStream.ConsumerCount++;
            return muxStream;
        }

        MediaSourceInfo mediaSourceInfo = plugin.StreamService.GetMediaSourceInfo(StreamType.Live, channel, restream: true);
        ILiveStream? stream = currentLiveStreams.Find(stream => stream.TunerHostId == Restream.TunerHost && stream.MediaSource.Id == mediaSourceInfo.Id);

        if (stream == null)
        {
            stream = new Restream(appHost, httpClientFactory, logger, mediaSourceInfo);
            await stream.Open(cancellationToken).ConfigureAwait(false);
        }

        stream.ConsumerCount++;
        return stream;
    }

    /// <summary>
    /// Computes the time shift between an EPG source timezone and the user's timezone.
    /// </summary>
    /// <param name="epgTimezone">IANA timezone of the EPG data (e.g. "Europe/London"). Null or empty means UTC.</param>
    /// <param name="myTimezone">IANA timezone of the user (e.g. "Europe/Copenhagen"). Null or empty means server local.</param>
    /// <returns>The TimeSpan to add to EPG times.</returns>
    internal static TimeSpan GetEpgShift(string? epgTimezone, string? myTimezone)
    {
        TimeZoneInfo epgTz;
        TimeZoneInfo myTz;

        try
        {
            epgTz = string.IsNullOrEmpty(epgTimezone)
                ? TimeZoneInfo.Utc
                : TimeZoneInfo.FindSystemTimeZoneById(epgTimezone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeSpan.Zero;
        }

        try
        {
            myTz = string.IsNullOrEmpty(myTimezone)
                ? TimeZoneInfo.Local
                : TimeZoneInfo.FindSystemTimeZoneById(myTimezone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeSpan.Zero;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return myTz.GetUtcOffset(now) - epgTz.GetUtcOffset(now);
    }

    /// <summary>
    /// Gets a snapshot of current series timers.
    /// </summary>
    /// <returns>A read-only list of series timer infos.</returns>
    public IReadOnlyList<SeriesTimerInfo> GetSeriesTimersSnapshot()
    {
        return _seriesTimers.Values.ToList();
    }

    /// <summary>
    /// Processes series timers by matching them against upcoming EPG programmes and
    /// creating individual recording timers for matching episodes.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UpdateSeriesTimersAsync(CancellationToken cancellationToken)
    {
        var seriesTimers = GetSeriesTimersSnapshot();
        if (seriesTimers.Count == 0)
        {
            return;
        }

        var channels = await GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var endWindow = now.AddDays(14);

        foreach (var seriesTimer in seriesTimers)
        {
            try
            {
                await ScheduleTimersForSeriesAsync(seriesTimer, channels, now, endWindow, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing series timer {SeriesTimerId} '{Name}'", seriesTimer.Id, seriesTimer.Name);
            }
        }
    }

    private async Task ScheduleTimersForSeriesAsync(
        SeriesTimerInfo seriesTimer,
        IEnumerable<ChannelInfo> channels,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken)
    {
        // Determine which channels to search
        IEnumerable<ChannelInfo> targetChannels = seriesTimer.RecordAnyChannel
            ? channels
            : channels.Where(c => c.Id == seriesTimer.ChannelId);

        foreach (var channel in targetChannels)
        {
            IEnumerable<ProgramInfo> programmes;
            try
            {
                programmes = await GetProgramsAsync(channel.Id, startDateUtc, endDateUtc, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not fetch programmes for channel {ChannelId}", channel.Id);
                continue;
            }

            foreach (var programme in programmes)
            {
                if (!IsSeriesMatch(seriesTimer, programme))
                {
                    continue;
                }

                // Skip programmes that have already ended
                if (programme.EndDate < DateTime.UtcNow)
                {
                    continue;
                }

                // Check RecordNewOnly: skip episodes at or below the high-water mark
                if (seriesTimer.RecordNewOnly && !IsNewEpisode(seriesTimer.Id, programme))
                {
                    continue;
                }

                // Sport programmes (season but no episode) are only recorded when
                // the title contains "live", filtering out highlights and replays.
                if (programme.SeasonNumber.HasValue && !programme.EpisodeNumber.HasValue
                    && !programme.Name.Contains("live", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Check if a timer already exists for this programme
                string timerId = $"series_{seriesTimer.Id}_{programme.Id}";
                lock (_timers)
                {
                    if (_timers.ContainsKey(timerId))
                    {
                        continue;
                    }
                }

                // Create individual timer
                var timer = new TimerInfo
                {
                    Id = timerId,
                    ChannelId = channel.Id,
                    ProgramId = programme.Id,
                    Name = programme.Name,
                    Overview = programme.Overview,
                    StartDate = programme.StartDate,
                    EndDate = programme.EndDate,
                    SeriesTimerId = seriesTimer.Id,
                    PrePaddingSeconds = seriesTimer.PrePaddingSeconds,
                    PostPaddingSeconds = seriesTimer.PostPaddingSeconds,
                    IsPrePaddingRequired = seriesTimer.IsPrePaddingRequired,
                    IsPostPaddingRequired = seriesTimer.IsPostPaddingRequired,
                };

                lock (_timers)
                {
                    _timers[timer.Id] = timer;
                    PersistTimers();
                }

                // Advance the high-water mark
                AdvanceHighWaterMark(seriesTimer.Id, programme.SeasonNumber, programme.EpisodeNumber);

                logger.LogInformation(
                    "Series timer '{SeriesName}' scheduled recording: {ProgramName} on {Channel} at {Start}",
                    seriesTimer.Name,
                    programme.Name,
                    channel.Name,
                    programme.StartDate);
            }
        }
    }

    private static bool IsSeriesMatch(SeriesTimerInfo seriesTimer, ProgramInfo programme)
    {
        // Match by SeriesId if available
        if (!string.IsNullOrEmpty(seriesTimer.SeriesId) && !string.IsNullOrEmpty(programme.SeriesId))
        {
            return string.Equals(seriesTimer.SeriesId, programme.SeriesId, StringComparison.OrdinalIgnoreCase);
        }

        // Fallback: match by programme name (case-insensitive, trimmed)
        if (!string.IsNullOrEmpty(seriesTimer.Name) && !string.IsNullOrEmpty(programme.Name))
        {
            return string.Equals(seriesTimer.Name.Trim(), programme.Name.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Determines if a programme is a new episode beyond the high-water mark.
    /// Returns true only if the episode is strictly newer (higher season, or same season with higher episode).
    /// </summary>
    private bool IsNewEpisode(string seriesTimerId, ProgramInfo programme)
    {
        if (!programme.SeasonNumber.HasValue || !programme.EpisodeNumber.HasValue)
        {
            // Without season+episode data we can't determine if it's new; skip it to be safe
            return false;
        }

        int season = programme.SeasonNumber.Value;
        int episode = programme.EpisodeNumber.Value;

        if (!_highWaterMarks.TryGetValue(seriesTimerId, out var mark))
        {
            // No recordings yet for this series — this is a new episode
            return true;
        }

        // New if: higher season, or same season with higher episode number
        if (season > mark.Season)
        {
            return true;
        }

        return season == mark.Season && episode > mark.Episode;
    }

    /// <summary>
    /// Advances the high-water mark for a series timer when a new episode is scheduled.
    /// </summary>
    private void AdvanceHighWaterMark(string seriesTimerId, int? seasonNumber, int? episodeNumber)
    {
        if (!seasonNumber.HasValue || !episodeNumber.HasValue)
        {
            return;
        }

        int season = seasonNumber.Value;
        int episode = episodeNumber.Value;

        if (!_highWaterMarks.TryGetValue(seriesTimerId, out var mark))
        {
            _highWaterMarks[seriesTimerId] = new EpisodeHighWaterMark { Season = season, Episode = episode };
        }
        else if (season > mark.Season || (season == mark.Season && episode > mark.Episode))
        {
            mark.Season = season;
            mark.Episode = episode;
        }
        else
        {
            return;
        }

        timerStore.SaveHighWaterMarks(_highWaterMarks);
    }
}
