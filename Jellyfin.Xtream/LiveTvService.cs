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
using System.IO;
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
using MediaBrowser.Model.Dto;
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

    // Maps recording channel GUIDs to timer IDs for reverse lookup
    private readonly Dictionary<string, string> _recordingChannelMap = new();

    // Lazy to break circular dependency (RecordingEngine → LiveTvService → RecordingEngine)
    private RecordingEngine? _recordingEngine;

    /// <inheritdoc />
    public string Name => "Xtream Live";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    private RecordingEngine RecordingEngine => _recordingEngine ??= serviceProvider.GetRequiredService<RecordingEngine>();

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

        // Append virtual channels for active recordings
        _recordingChannelMap.Clear();
        int recIndex = 0;
        foreach (var rec in RecordingEngine.GetReadyRecordingsSnapshot())
        {
            recIndex++;
            // Use a deterministic hash of the timer ID for the GUID encoding
            int timerHash = rec.Timer.Id.GetHashCode(StringComparison.Ordinal);
            string channelId = StreamService.ToGuid(StreamService.RecordingPrefix, timerHash, 0, 0).ToString();
            _recordingChannelMap[channelId] = rec.Timer.Id;

            items.Add(new ChannelInfo()
            {
                Id = channelId,
                Number = $"0.{recIndex}",
                Name = $"\u25cf REC: {rec.Timer.Name}",
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

        _seriesTimers[info.Id] = info;
        timerStore.SaveSeriesTimers(_seriesTimers.Values);
        logger.LogInformation("Series timer created: {TimerId}", info.Id);
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
        // Handle virtual recording channels
        if (_recordingChannelMap.TryGetValue(channelId, out string? timerId))
        {
            return GetRecordingPrograms(channelId, timerId, startDateUtc, endDateUtc);
        }

        Guid guid = Guid.Parse(channelId);
        StreamService.FromGuid(guid, out int prefix, out int streamId, out int _, out int _);
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
                    items.Add(new()
                    {
                        Id = StreamService.ToGuid(StreamService.EpgPrefix, streamId, epgId++, 0).ToString(),
                        ChannelId = channelId,
                        StartDate = prog.Start + epgShift,
                        EndDate = prog.Stop + epgShift,
                        Name = prog.Title,
                        Overview = prog.Description,
                        ImageUrl = prog.Icon,
                    });
                }
            }
            else
            {
                EpgListings epgs = await xtreamClient.GetEpgInfoAsync(plugin.Creds, streamId, cancellationToken).ConfigureAwait(false);
                foreach (EpgInfo epg in epgs.Listings)
                {
                    items.Add(new()
                    {
                        Id = StreamService.ToGuid(StreamService.EpgPrefix, streamId, epg.Id, 0).ToString(),
                        ChannelId = channelId,
                        StartDate = epg.Start + epgShift,
                        EndDate = epg.End + epgShift,
                        Name = epg.Title,
                        Overview = epg.Description,
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
        // Check if this is a virtual recording channel
        if (_recordingChannelMap.TryGetValue(channelId, out string? timerId))
        {
            string? tsPath = RecordingEngine.GetTsFilePath(timerId);
            if (tsPath == null || !File.Exists(tsPath))
            {
                throw new FileNotFoundException($"Recording TS file not found for timer {timerId}");
            }

            // Reuse an existing stream if another consumer is already watching
            ILiveStream? existing = currentLiveStreams.Find(s => s.TunerHostId == RecordingRestream.TunerHost && s.MediaSource.Id == $"recording_{timerId}");
            if (existing != null)
            {
                existing.ConsumerCount++;
                return existing;
            }

            // Look up the timer to provide duration info for the seekbar
            TimerInfo? timer;
            lock (_timers)
            {
                _timers.TryGetValue(timerId, out timer);
            }

            var recStream = new RecordingRestream(appHost, logger, timerId, tsPath, () => RecordingEngine.IsRecordingActive(timerId), timer);
            await recStream.Open(cancellationToken).ConfigureAwait(false);
            recStream.ConsumerCount++;
            return recStream;
        }

        Guid guid = Guid.Parse(channelId);
        StreamService.FromGuid(guid, out int prefix, out int channel, out int _, out int _);
        if (prefix != StreamService.LiveTvPrefix)
        {
            throw new ArgumentException("Unsupported channel");
        }

        Plugin plugin = Plugin.Instance;
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
    /// Returns a single EPG entry for a virtual recording channel, spanning the timer's scheduled time.
    /// </summary>
    private IEnumerable<ProgramInfo> GetRecordingPrograms(string channelId, string timerId, DateTime startDateUtc, DateTime endDateUtc)
    {
        TimerInfo? timer;
        lock (_timers)
        {
            _timers.TryGetValue(timerId, out timer);
        }

        if (timer == null)
        {
            return [];
        }

        var scheduledStart = timer.StartDate.AddSeconds(-timer.PrePaddingSeconds);
        var scheduledEnd = timer.EndDate.AddSeconds(timer.PostPaddingSeconds);

        // The TS file contains data from scheduledStart until now (the elapsed recording).
        // Anchor the programme at "now" so position = (now - StartDate) ≈ 0, putting the
        // seekbar at the leftmost point which matches byte 0 of the TS file.
        // Duration = elapsed recording time = how much content the file actually has.
        var now = DateTime.UtcNow;
        var elapsed = now - scheduledStart;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        // Cap at total scheduled duration in case clock drifts past the schedule
        var totalDuration = scheduledEnd - scheduledStart;
        if (elapsed > totalDuration)
        {
            elapsed = totalDuration;
        }

        var start = now;
        var end = now + elapsed;

        if (end < startDateUtc || start >= endDateUtc)
        {
            return [];
        }

        int timerHash = timerId.GetHashCode(StringComparison.Ordinal);
        return
        [
            new ProgramInfo()
            {
                Id = StreamService.ToGuid(StreamService.RecordingPrefix, timerHash, 1, 0).ToString(),
                ChannelId = channelId,
                StartDate = start,
                EndDate = end,
                Name = timer.Name,
                Overview = $"Recording: {timer.Name} (originally {scheduledStart:HH:mm}–{scheduledEnd:HH:mm} UTC)",
                IsLive = false,
            }
        ];
    }
}
