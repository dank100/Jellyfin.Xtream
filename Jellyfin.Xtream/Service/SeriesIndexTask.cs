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
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Scheduled task that indexes all series episodes by browsing each series and season
/// through the channel manager, ensuring episodes are persisted to the Jellyfin database
/// and discoverable via the /Shows/Episodes endpoint.
/// </summary>
public class SeriesIndexTask : IScheduledTask
{
    private readonly ILogger<SeriesIndexTask> _logger;
    private readonly IChannelManager _channelManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeriesIndexTask"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{SeriesIndexTask}"/> interface.</param>
    /// <param name="channelManager">Instance of the <see cref="IChannelManager"/> interface.</param>
    public SeriesIndexTask(
        ILogger<SeriesIndexTask> logger,
        IChannelManager channelManager)
    {
        _logger = logger;
        _channelManager = channelManager;
    }

    /// <inheritdoc />
    public string Name => "Index Xtream Series Episodes";

    /// <inheritdoc />
    public string Key => "XtreamSeriesIndex";

    /// <inheritdoc />
    public string Description => "Browses all Xtream series and seasons to index episodes, ensuring they appear in the Jellyfin client.";

    /// <inheritdoc />
    public string Category => "Xtream";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
            }
        ];
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Xtream series episode indexing");
        progress.Report(0);

        // Find the Series channel
        QueryResult<Channel> channels = await _channelManager.GetChannelsInternalAsync(new()).ConfigureAwait(false);
        Channel? seriesChannel = channels.Items.FirstOrDefault(c => c.Name == "Xtream Series");
        if (seriesChannel == null)
        {
            _logger.LogWarning("Xtream Series channel not found, skipping indexing");
            return;
        }

        Guid channelId = seriesChannel.Id;
        _logger.LogInformation("Found Xtream Series channel: {ChannelId}", channelId);

        // Step 1: Get all series (root items in channel)
        var rootQuery = new InternalItemsQuery
        {
            ChannelIds = [channelId],
            IsFolder = true,
        };

        QueryResult<BaseItem> rootResult = await _channelManager
            .GetChannelItemsInternal(rootQuery, new Progress<double>(), cancellationToken)
            .ConfigureAwait(false);

        var seriesItems = rootResult.Items.ToList();
        _logger.LogInformation("Found {Count} series to index", seriesItems.Count);
        progress.Report(10);

        int totalSeries = seriesItems.Count;
        int processed = 0;
        int totalEpisodes = 0;

        foreach (var series in seriesItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Step 2: Get seasons for this series
                var seasonQuery = new InternalItemsQuery
                {
                    ChannelIds = [channelId],
                    ParentId = series.Id,
                    IsFolder = true,
                };

                QueryResult<BaseItem> seasonResult = await _channelManager
                    .GetChannelItemsInternal(seasonQuery, new Progress<double>(), cancellationToken)
                    .ConfigureAwait(false);

                foreach (var season in seasonResult.Items)
                {
                    // Step 3: Get episodes for this season
                    var episodeQuery = new InternalItemsQuery
                    {
                        ChannelIds = [channelId],
                        ParentId = season.Id,
                    };

                    QueryResult<BaseItem> episodeResult = await _channelManager
                        .GetChannelItemsInternal(episodeQuery, new Progress<double>(), cancellationToken)
                        .ConfigureAwait(false);

                    totalEpisodes += episodeResult.Items.Count;
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Failed to index series {Name}, skipping", series.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unexpected error indexing series {Name}", series.Name);
            }

            processed++;
            double progressPct = 10 + (90.0 * processed / totalSeries);
            progress.Report(progressPct);
        }

        progress.Report(100);
        _logger.LogInformation("Xtream series episode indexing completed: {Episodes} episodes across {Series} series", totalEpisodes, totalSeries);
    }
}
