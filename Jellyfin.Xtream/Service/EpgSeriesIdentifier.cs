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
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Extracts series/episode metadata from EPG title and description and generates
/// reproducible series identifiers for Jellyfin series timer scheduling.
/// </summary>
public static partial class EpgSeriesIdentifier
{
    /// <summary>
    /// Attempts to extract series information from an EPG programme's title and description.
    /// Parses common patterns like "Sæson 1 Episode 10", "S01E10", "Season 1, Episode 10", etc.
    /// Generates a deterministic series ID by hashing the normalized title.
    /// </summary>
    /// <param name="title">The programme title.</param>
    /// <param name="description">The programme description (may be null).</param>
    /// <returns>An <see cref="EpgSeriesInfo"/> with any detected series metadata.</returns>
    public static EpgSeriesInfo Parse(string title, string? description)
    {
        int? seasonNumber = null;
        int? episodeNumber = null;
        string? episodeTitle = null;
        string seriesTitle = title;

        // Try to extract from title first, then description
        if (!TryExtractSeasonEpisode(title, out seasonNumber, out episodeNumber, out string? titleRemainder))
        {
            TryExtractSeasonEpisode(description, out seasonNumber, out episodeNumber, out _);
        }
        else if (!string.IsNullOrWhiteSpace(titleRemainder))
        {
            // If the S/E info was in the title, the remainder may be the episode title
            seriesTitle = titleRemainder.Trim();
        }

        // Try episode-only patterns in description if we still lack episode number
        if (!episodeNumber.HasValue && !string.IsNullOrEmpty(description))
        {
            TryExtractEpisodeOnly(description, out episodeNumber);
        }

        // Try extracting episode title from description patterns like "Episode title. Description..."
        if (episodeTitle == null && !string.IsNullOrEmpty(description))
        {
            episodeTitle = TryExtractEpisodeTitle(description);
        }

        // Season-only (no episode) is treated as sport — competition years like
        // "Sæson: 26" are common in sports EPG. Sport series are still IsSeries=true
        // so the "Record Series" button appears, but the scheduling loop only records
        // sport programmes whose title contains "live".
        bool isSeries = seasonNumber.HasValue || episodeNumber.HasValue;
        bool isSport = seasonNumber.HasValue && !episodeNumber.HasValue;
        string seriesId = GenerateSeriesId(isSeries ? seriesTitle : title);

        return new EpgSeriesInfo
        {
            SeriesId = seriesId,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            EpisodeTitle = episodeTitle,
            IsSeries = isSeries,
            IsSport = isSport,
        };
    }

    /// <summary>
    /// Generates a deterministic series ID from a title string.
    /// The same title will always produce the same ID regardless of episode info.
    /// </summary>
    /// <param name="title">The series title to hash.</param>
    /// <returns>A stable string identifier for the series.</returns>
    public static string GenerateSeriesId(string title)
    {
        string normalized = NormalizeTitle(title);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        // Use first 16 bytes as a GUID for compact reproducible ID
        byte[] guidBytes = new byte[16];
        Buffer.BlockCopy(hash, 0, guidBytes, 0, 16);
        return new Guid(guidBytes).ToString("N");
    }

    private static string NormalizeTitle(string title)
    {
        // Lowercase, trim, collapse whitespace
        string normalized = title.Trim().ToLowerInvariant();
        normalized = CollapseWhitespaceRegex().Replace(normalized, " ");
        return normalized;
    }

    private static bool TryExtractSeasonEpisode(string? text, out int? season, out int? episode, out string? remainder)
    {
        season = null;
        episode = null;
        remainder = null;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // Pattern: S01E10 or S1E10 (compact format)
        Match match = CompactSeasonEpisodeRegex().Match(text);
        if (match.Success)
        {
            season = int.Parse(match.Groups["season"].Value, System.Globalization.CultureInfo.InvariantCulture);
            episode = int.Parse(match.Groups["episode"].Value, System.Globalization.CultureInfo.InvariantCulture);
            remainder = text[..match.Index] + text[(match.Index + match.Length)..];
            return true;
        }

        // Pattern: "Sæson 1 Episode 10", "Season 1, Episode 10", "Säsong 1 Avsnitt 10"
        match = VerboseSeasonEpisodeRegex().Match(text);
        if (match.Success)
        {
            season = int.Parse(match.Groups["season"].Value, System.Globalization.CultureInfo.InvariantCulture);
            episode = int.Parse(match.Groups["episode"].Value, System.Globalization.CultureInfo.InvariantCulture);
            remainder = text[..match.Index] + text[(match.Index + match.Length)..];
            return true;
        }

        // Pattern: "Season 1" or "Sæson 1" without episode
        match = SeasonOnlyRegex().Match(text);
        if (match.Success)
        {
            season = int.Parse(match.Groups["season"].Value, System.Globalization.CultureInfo.InvariantCulture);
            remainder = text[..match.Index] + text[(match.Index + match.Length)..];
            return true;
        }

        return false;
    }

    private static bool TryExtractEpisodeOnly(string? text, out int? episode)
    {
        episode = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // Pattern: "Episode 10", "Ep. 10", "Afsnit 10", "Avsnitt 10", "Del 10"
        Match match = EpisodeOnlyRegex().Match(text);
        if (match.Success)
        {
            episode = int.Parse(match.Groups["episode"].Value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        // Pattern: "(10)" or "(10/24)" - common in Nordic EPG
        match = ParenthesizedEpisodeRegex().Match(text);
        if (match.Success)
        {
            episode = int.Parse(match.Groups["episode"].Value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static string? TryExtractEpisodeTitle(string description)
    {
        // Pattern: text in quotes at the start of description, e.g. "Episode Title". Rest of description
        Match match = QuotedEpisodeTitleRegex().Match(description);
        if (match.Success)
        {
            return match.Groups["title"].Value;
        }

        return null;
    }

    // S01E10, S1E10
    [GeneratedRegex(@"S(?<season>\d{1,2})E(?<episode>\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex CompactSeasonEpisodeRegex();

    // Sæson: 2026. Episode: 7, Season 2 Episode 5, Säsong 1 Avsnitt 3, Saison 2 Épisode 4
    [GeneratedRegex(@"(?<!\w)(?:S[æä]son[g]?|Season|Saison|Stagione)[:\s]*(?<season>\d{1,4})\s*[,.\s]*(?:Episode|Ep\.?|Afsnit|Avsnitt|[ÉE]pisode|Del|Episodio)[:\s]*(?<episode>\d{1,4})", RegexOptions.IgnoreCase)]
    private static partial Regex VerboseSeasonEpisodeRegex();

    // Season 1, Sæson 2, Sæson: 2026 (without episode)
    [GeneratedRegex(@"(?<!\w)(?:S[æä]son[g]?|Season|Saison|Stagione)[:\s]*(?<season>\d{1,4})", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonOnlyRegex();

    // Episode 10, Ep. 5, Afsnit 3, Avsnitt 7, Del 2, Episode: 7
    [GeneratedRegex(@"(?<!\w)(?:Episode|Ep\.?|Afsnit|Avsnitt|Del|Episodio)[:\s]*(?<episode>\d{1,4})", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeOnlyRegex();

    // (10) or (10/24)
    [GeneratedRegex(@"\((?<episode>\d{1,3})(?:/\d{1,3})?\)")]
    private static partial Regex ParenthesizedEpisodeRegex();

    // "Episode Title" or 'Episode Title' at start or after season/episode info
    [GeneratedRegex(@"[""«»„""''](?<title>[^""«»„""'']+)[""«»„""'']")]
    private static partial Regex QuotedEpisodeTitleRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespaceRegex();
}
