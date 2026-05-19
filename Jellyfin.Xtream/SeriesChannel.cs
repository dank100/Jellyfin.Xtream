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
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Service;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream;

/// <summary>
/// The Xtream Codes API channel.
/// </summary>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
public class SeriesChannel(ILogger<SeriesChannel> logger) : IChannel, IDisableMediaSourceDisplay, IRequiresMediaInfoCallback
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private List<ChannelItemInfo>? _cachedItems;
    private DateTime _cacheExpiry = DateTime.MinValue;

    /// <inheritdoc />
    public string? Name => "Xtream Series";

    /// <inheritdoc />
    public string? Description => "Series streamed from the Xtream-compatible server.";

    /// <inheritdoc />
    public string DataVersion => Plugin.Instance.DataVersion;

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            AutoRefreshLevels = 4,
            ContentTypes = [
                ChannelMediaContentType.Episode,
            ],

            MediaTypes = [
                ChannelMediaType.Video
            ],
        };
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        switch (type)
        {
            default:
                throw new ArgumentException("Unsupported image type: " + type);
        }
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages()
    {
        return new List<ImageType>
        {
            // ImageType.Primary
        };
    }

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(query.FolderId))
            {
                return await GetAllSeries(cancellationToken).ConfigureAwait(false);
            }

            Guid guid = Guid.Parse(query.FolderId);
            StreamService.FromGuid(guid, out int prefix, out int categoryId, out int seriesId, out int seasonId);
            if (prefix == StreamService.SeriesPrefix)
            {
                return await GetSeasons(seriesId, cancellationToken).ConfigureAwait(false);
            }

            if (prefix == StreamService.SeasonPrefix)
            {
                return await GetEpisodes(seriesId, seasonId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get channel items");
            throw;
        }

        return new ChannelItemResult()
        {
            TotalRecordCount = 0,
        };
    }

    private ChannelItemInfo CreateChannelItemInfo(Series series)
    {
        ParsedName parsedName = StreamService.ParseName(series.Name);
        return new ChannelItemInfo()
        {
            CommunityRating = (float)series.Rating5Based,
            DateModified = series.LastModified,
            FolderType = ChannelFolderType.Series,
            Genres = GetGenres(series.Genre),
            Id = StreamService.ToGuid(StreamService.SeriesPrefix, series.CategoryId, series.SeriesId, 0).ToString(),
            ImageUrl = series.Cover,
            Name = parsedName.Title,
            SeriesName = parsedName.Title,
            People = GetPeople(series.Cast),
            Tags = new List<string>(parsedName.Tags),
            Type = ChannelItemType.Folder,
        };
    }

    private static List<string> GetGenres(string genreString)
    {
        return new(genreString.Split(',').Select(genre => genre.Trim()));
    }

    private static List<PersonInfo> GetPeople(string cast)
    {
        return cast.Split(',').Select(name => new PersonInfo()
        {
            Name = name.Trim()
        }).ToList();
    }

    private ChannelItemInfo CreateChannelItemInfo(int seriesId, SeriesStreamInfo series, int seasonId)
    {
        Client.Models.SeriesInfo serie = series.Info;
        string name = $"Season {seasonId}";
        string cover = series.Info.Cover;
        string? overview = null;
        DateTime? created = null;
        List<string> tags = [];

        Season? season = series.Seasons.FirstOrDefault(s => s.SeasonId == seasonId);
        if (season != null)
        {
            ParsedName parsedName = StreamService.ParseName(season.Name);
            name = parsedName.Title;
            tags.AddRange(parsedName.Tags);
            created = season.AirDate;
            overview = season.Overview;
            if (!string.IsNullOrEmpty(season.Cover))
            {
                cover = season.Cover;
            }
        }

        return new()
        {
            DateCreated = created,
            FolderType = ChannelFolderType.Season,
            Genres = GetGenres(serie.Genre),
            Id = StreamService.ToGuid(StreamService.SeasonPrefix, serie.CategoryId, seriesId, seasonId).ToString(),
            IndexNumber = seasonId,
            Name = name,
            Overview = overview,
            People = GetPeople(serie.Cast),
            Tags = tags,
            Type = ChannelItemType.Folder,
        };
    }

    private ChannelItemInfo CreateChannelItemInfo(SeriesStreamInfo series, Season? season, Episode episode)
    {
        Client.Models.SeriesInfo serie = series.Info;
        ParsedName parsedName = StreamService.ParseName(episode.Title);

        string? cover = episode.Info?.MovieImage;
        cover ??= season?.Cover;
        cover ??= serie.Cover;

        return new()
        {
            ContentType = ChannelMediaContentType.Episode,
            DateCreated = episode.Added,
            Genres = GetGenres(serie.Genre),
            Id = StreamService.ToGuid(StreamService.EpisodePrefix, 0, 0, episode.EpisodeId).ToString(),
            IndexNumber = episode.EpisodeNum,
            IsLiveStream = false,
            MediaType = ChannelMediaType.Video,
            Name = $"Episode {episode.EpisodeNum}",
            Overview = episode.Info?.Plot,
            ParentIndexNumber = episode.Season,
            People = GetPeople(serie.Cast),
            RunTimeTicks = episode.Info?.DurationSecs * TimeSpan.TicksPerSecond,
            Tags = new(parsedName.Tags),
            Type = ChannelItemType.Media,
        };
    }

    private async Task<ChannelItemResult> GetAllSeries(CancellationToken cancellationToken)
    {
        if (_cachedItems != null && DateTime.UtcNow < _cacheExpiry)
        {
            return new ChannelItemResult()
            {
                Items = _cachedItems,
                TotalRecordCount = _cachedItems.Count
            };
        }

        IEnumerable<Category> categories = await Plugin.Instance.StreamService.GetSeriesCategories(cancellationToken).ConfigureAwait(false);
        var categoryList = categories.ToList();
        logger.LogInformation("Fetching series from {Count} categories", categoryList.Count);

        List<ChannelItemInfo> items = [];
        foreach (Category category in categoryList)
        {
            List<ChannelItemInfo>? categoryItems = null;
            for (int attempt = 0; attempt < 3 && categoryItems == null; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        await Task.Delay(attempt * 2000, cancellationToken).ConfigureAwait(false);
                    }

                    IEnumerable<Series> series = await Plugin.Instance.StreamService.GetSeries(category.CategoryId, cancellationToken).ConfigureAwait(false);
                    categoryItems = new List<ChannelItemInfo>(series.Select(CreateChannelItemInfo));
                }
                catch (HttpRequestException ex) when (attempt < 2)
                {
                    logger.LogWarning(ex, "Attempt {Attempt} failed for series category {CategoryId}, retrying", attempt + 1, category.CategoryId);
                }
            }

            if (categoryItems != null)
            {
                items.AddRange(categoryItems);
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        _cachedItems = items;
        _cacheExpiry = DateTime.UtcNow + CacheDuration;
        logger.LogInformation("Cached {Count} series from {Categories} categories", items.Count, categoryList.Count);

        return new ChannelItemResult()
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    private async Task<ChannelItemResult> GetSeasons(int seriesId, CancellationToken cancellationToken)
    {
        IEnumerable<Tuple<SeriesStreamInfo, int>> seasons = await Plugin.Instance.StreamService.GetSeasons(seriesId, cancellationToken).ConfigureAwait(false);
        List<ChannelItemInfo> items = new(
            seasons.Select((Tuple<SeriesStreamInfo, int> tuple) => CreateChannelItemInfo(seriesId, tuple.Item1, tuple.Item2)));
        return new()
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    private async Task<ChannelItemResult> GetEpisodes(int seriesId, int seasonId, CancellationToken cancellationToken)
    {
        IEnumerable<Tuple<SeriesStreamInfo, Season?, Episode>> episodes = await Plugin.Instance.StreamService.GetEpisodes(seriesId, seasonId, cancellationToken).ConfigureAwait(false);
        List<ChannelItemInfo> items = new List<ChannelItemInfo>(
            episodes.Select((Tuple<SeriesStreamInfo, Season?, Episode> tuple) => CreateChannelItemInfo(tuple.Item1, tuple.Item2, tuple.Item3)));
        return new()
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId)
    {
        return Plugin.Instance.Configuration.IsSeriesVisible;
    }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        Guid guid = Guid.Parse(id);
        StreamService.FromGuid(guid, out int prefix, out int _, out int _, out int episodeId);
        if (prefix == StreamService.EpisodePrefix)
        {
            var source = Plugin.Instance.StreamService.GetMediaSourceInfo(
                StreamType.Series,
                episodeId);
            return Task.FromResult<IEnumerable<MediaSourceInfo>>([source]);
        }

        return Task.FromResult<IEnumerable<MediaSourceInfo>>([]);
    }
}
