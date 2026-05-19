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
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Providers;
using Jellyfin.Xtream.Service;
using MediaBrowser.Controller.Channels;
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
public class VodChannel(ILogger<VodChannel> logger) : IChannel, IDisableMediaSourceDisplay, IRequiresMediaInfoCallback
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private List<ChannelItemInfo>? _cachedItems;
    private DateTime _cacheExpiry = DateTime.MinValue;

    /// <inheritdoc />
    public string? Name => "Xtream Video On-Demand";

    /// <inheritdoc />
    public string? Description => "Video On-Demand streamed from the Xtream-compatible server.";

    /// <inheritdoc />
    public string DataVersion => Plugin.Instance.DataVersion;

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new()
        {
            AutoRefreshLevels = 2,
            ContentTypes = [
                ChannelMediaContentType.Movie,
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
                return await GetAllStreams(cancellationToken).ConfigureAwait(false);
            }

            return new ChannelItemResult()
            {
                TotalRecordCount = 0,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get channel items");
            throw;
        }
    }

    private Task<ChannelItemInfo> CreateChannelItemInfo(StreamInfo stream)
    {
        long added = long.Parse(stream.Added, CultureInfo.InvariantCulture);
        ParsedName parsedName = StreamService.ParseName(stream.Name);

        string id = $"{StreamService.StreamPrefix}{stream.StreamId}";
        if (!string.IsNullOrEmpty(stream.ContainerExtension))
        {
            id += $".{stream.ContainerExtension}";
        }

        ChannelItemInfo result = new ChannelItemInfo()
        {
            ContentType = ChannelMediaContentType.Movie,
            DateCreated = DateTimeOffset.FromUnixTimeSeconds(added).DateTime,
            Id = id,
            ImageUrl = stream.StreamIcon,
            IsLiveStream = false,
            MediaType = ChannelMediaType.Video,
            Name = parsedName.Title,
            Tags = new List<string>(parsedName.Tags),
            Type = ChannelItemType.Media,
            ProviderIds = { { XtreamVodProvider.ProviderName, stream.StreamId.ToString(CultureInfo.InvariantCulture) } },
        };

        return Task.FromResult(result);
    }

    private async Task<ChannelItemResult> GetAllStreams(CancellationToken cancellationToken)
    {
        if (_cachedItems != null && DateTime.UtcNow < _cacheExpiry)
        {
            return new ChannelItemResult()
            {
                Items = _cachedItems,
                TotalRecordCount = _cachedItems.Count
            };
        }

        IEnumerable<Category> categories = await Plugin.Instance.StreamService.GetVodCategories(cancellationToken).ConfigureAwait(false);
        var categoryList = categories.ToList();
        logger.LogInformation("Fetching VOD streams from {Count} categories", categoryList.Count);

        List<ChannelItemInfo> items = [];
        foreach (Category category in categoryList)
        {
            // Retry with backoff if the provider resets the connection
            ChannelItemInfo[]? categoryItems = null;
            for (int attempt = 0; attempt < 3 && categoryItems == null; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        await Task.Delay(attempt * 2000, cancellationToken).ConfigureAwait(false);
                    }

                    IEnumerable<StreamInfo> streams = await Plugin.Instance.StreamService.GetVodStreams(category.CategoryId, cancellationToken).ConfigureAwait(false);
                    categoryItems = await Task.WhenAll(streams.Select(CreateChannelItemInfo)).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (attempt < 2)
                {
                    logger.LogWarning(ex, "Attempt {Attempt} failed for category {CategoryId}, retrying", attempt + 1, category.CategoryId);
                }
            }

            if (categoryItems != null)
            {
                items.AddRange(categoryItems);
            }

            // Brief delay between requests to avoid rate limiting
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        _cachedItems = items;
        _cacheExpiry = DateTime.UtcNow + CacheDuration;
        logger.LogInformation("Cached {Count} VOD items from {Categories} categories", items.Count, categoryList.Count);

        return new ChannelItemResult()
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId)
    {
        return Plugin.Instance.Configuration.IsVodVisible;
    }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        // Channel item ID format: "{StreamPrefix}{streamId}" or "{StreamPrefix}{streamId}.{extension}"
        string prefix = StreamService.StreamPrefix.ToString(CultureInfo.InvariantCulture);
        string idStr = id.StartsWith(prefix, StringComparison.Ordinal)
            ? id[prefix.Length..]
            : id;

        string? extension = null;
        int dotIndex = idStr.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex >= 0)
        {
            extension = idStr[(dotIndex + 1)..];
            idStr = idStr[..dotIndex];
        }

        if (int.TryParse(idStr, CultureInfo.InvariantCulture, out int streamId))
        {
            var source = Plugin.Instance.StreamService.GetMediaSourceInfo(
                StreamType.Vod,
                streamId,
                extension: extension);
            return Task.FromResult<IEnumerable<MediaSourceInfo>>([source]);
        }

        return Task.FromResult<IEnumerable<MediaSourceInfo>>([]);
    }
}
