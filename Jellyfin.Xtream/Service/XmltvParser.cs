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
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Xtream.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Parses XMLTV EPG data from external sources.
/// </summary>
public class XmltvParser
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<XmltvParser> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="XmltvParser"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="memoryCache">Instance of the <see cref="IMemoryCache"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{XmltvParser}"/> interface.</param>
    public XmltvParser(IHttpClientFactory httpClientFactory, IMemoryCache memoryCache, ILogger<XmltvParser> logger)
    {
        _httpClientFactory = httpClientFactory;
        _memoryCache = memoryCache;
        _logger = logger;
    }

    /// <summary>
    /// Gets programmes for a specific channel from an XMLTV source.
    /// </summary>
    /// <param name="source">The EPG source configuration.</param>
    /// <param name="xmltvChannelId">The XMLTV channel ID to filter by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of programme info.</returns>
    public async Task<IList<XmltvProgramme>> GetProgrammesAsync(EpgSource source, string xmltvChannelId, CancellationToken cancellationToken)
    {
        var allProgrammes = await GetAllProgrammesAsync(source, cancellationToken).ConfigureAwait(false);

        if (allProgrammes.TryGetValue(xmltvChannelId, out var programmes))
        {
            return programmes;
        }

        _logger.LogWarning("XMLTV channel ID '{ChannelId}' not found in source '{Source}'", xmltvChannelId, source.Name);
        return [];
    }

    /// <summary>
    /// Gets all available channel IDs from an XMLTV source.
    /// </summary>
    /// <param name="source">The EPG source configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of channel IDs and display names.</returns>
    public async Task<IList<XmltvChannel>> GetChannelsAsync(EpgSource source, CancellationToken cancellationToken)
    {
        string cacheKey = $"xmltv-channels-{source.Id}";
        if (_memoryCache.TryGetValue(cacheKey, out IList<XmltvChannel>? cached) && cached != null)
        {
            return cached;
        }

        var doc = await FetchXmltvDocumentAsync(source, cancellationToken).ConfigureAwait(false);
        if (doc == null)
        {
            return [];
        }

        var channels = doc.Root?.Elements("channel")
            .Select(ch => new XmltvChannel
            {
                Id = ch.Attribute("id")?.Value ?? string.Empty,
                DisplayName = ch.Element("display-name")?.Value ?? ch.Attribute("id")?.Value ?? string.Empty,
            })
            .Where(ch => !string.IsNullOrEmpty(ch.Id))
            .ToList() ?? [];

        _memoryCache.Set(cacheKey, (IList<XmltvChannel>)channels, DateTimeOffset.Now.Add(CacheDuration));
        return channels;
    }

    private async Task<Dictionary<string, List<XmltvProgramme>>> GetAllProgrammesAsync(EpgSource source, CancellationToken cancellationToken)
    {
        string cacheKey = $"xmltv-programmes-{source.Id}";
        if (_memoryCache.TryGetValue(cacheKey, out Dictionary<string, List<XmltvProgramme>>? cached) && cached != null)
        {
            return cached;
        }

        var doc = await FetchXmltvDocumentAsync(source, cancellationToken).ConfigureAwait(false);
        if (doc == null)
        {
            return new Dictionary<string, List<XmltvProgramme>>();
        }

        var result = new Dictionary<string, List<XmltvProgramme>>();

        foreach (var prog in doc.Root?.Elements("programme") ?? [])
        {
            string? channelId = prog.Attribute("channel")?.Value;
            string? startStr = prog.Attribute("start")?.Value;
            string? stopStr = prog.Attribute("stop")?.Value;

            if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(startStr) || string.IsNullOrEmpty(stopStr))
            {
                continue;
            }

            if (!TryParseXmltvDateTime(startStr, out DateTime start) || !TryParseXmltvDateTime(stopStr, out DateTime stop))
            {
                continue;
            }

            var programme = new XmltvProgramme
            {
                ChannelId = channelId,
                Start = start,
                Stop = stop,
                Title = prog.Element("title")?.Value ?? string.Empty,
                Description = prog.Element("desc")?.Value,
                Category = prog.Element("category")?.Value,
                Icon = prog.Element("icon")?.Attribute("src")?.Value,
            };

            if (!result.TryGetValue(channelId, out var list))
            {
                list = [];
                result[channelId] = list;
            }

            list.Add(programme);
        }

        _logger.LogInformation("Parsed XMLTV source '{Name}': {Channels} channels, {Programmes} programmes", source.Name, result.Count, result.Values.Sum(l => l.Count));
        _memoryCache.Set(cacheKey, result, DateTimeOffset.Now.Add(CacheDuration));
        return result;
    }

    private async Task<XDocument?> FetchXmltvDocumentAsync(EpgSource source, CancellationToken cancellationToken)
    {
        string cacheKey = $"xmltv-doc-{source.Id}";
        if (_memoryCache.TryGetValue(cacheKey, out XDocument? cached))
        {
            return cached;
        }

        try
        {
            Stream stream;
            if (Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                using var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                    CheckCertificateRevocationList = true,
                };
                using var client = new HttpClient(handler);
                stream = await client.GetStreamAsync(uri, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                stream = File.OpenRead(source.Url);
            }

            await using (stream.ConfigureAwait(false))
            {
                // Decompress gzip if the URL ends with .gz
                Stream xmlStream = source.Url.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                    ? new GZipStream(stream, CompressionMode.Decompress)
                    : stream;

                await using (xmlStream.ConfigureAwait(false))
                {
                    var doc = await XDocument.LoadAsync(xmlStream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
                    _memoryCache.Set(cacheKey, doc, DateTimeOffset.Now.Add(CacheDuration));
                    return doc;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch XMLTV data from '{Url}'", source.Url);
            return null;
        }
    }

    /// <summary>
    /// Parses XMLTV datetime format: "20260420183000 +0200" or "20260420183000".
    /// Always returns UTC.
    /// </summary>
    private static bool TryParseXmltvDateTime(string value, out DateTime result)
    {
        result = default;

        // Formats: "20260420183000 +0200", "20260420183000", "202604201830"
        string[] formats =
        [
            "yyyyMMddHHmmss zzz",
            "yyyyMMddHHmmss",
            "yyyyMMddHHmm zzz",
            "yyyyMMddHHmm",
        ];

        if (DateTimeOffset.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset dto))
        {
            result = dto.UtcDateTime;
            return true;
        }

        return false;
    }
}
