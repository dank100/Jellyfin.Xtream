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
using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Parses EPG time ranges embedded in PPV channel names.
/// </summary>
/// <remarks>
/// PPV channels typically embed event times directly in their title, e.g.:
/// "UFC 300: Jones vs Smith 22:00-01:00" or "F1 GP Monaco [14:00]".
/// When no EPG data is available from the Xtream provider, this parser
/// synthesises a <see cref="PpvEpgResult"/> from the embedded time.
/// </remarks>
public static partial class PpvEpgParser
{
    // Matches an optional opening bracket, a 24h start time, an optional
    // dash-separated end time, and an optional closing bracket.
    // Examples: 22:00, 22:00-01:00, [14:00-17:00], (20:00 - 23:30)
    [GeneratedRegex(
        @"[\[\(]?\b(\d{1,2}:\d{2})(?:\s*[-–]\s*(\d{1,2}:\d{2}))?\b[\]\)]?",
        RegexOptions.None)]
    private static partial Regex TimeRangeRegex();

    /// <summary>
    /// Attempts to parse a PPV event time range from a channel name.
    /// </summary>
    /// <param name="channelName">The full channel name, possibly containing an embedded time.</param>
    /// <param name="myTimezone">IANA timezone used to interpret the embedded time. Null or empty means UTC.</param>
    /// <returns>
    /// A <see cref="PpvEpgResult"/> with UTC start/end and a cleaned title, or <c>null</c>
    /// if no recognisable time pattern was found.
    /// </returns>
    public static PpvEpgResult? TryParse(string channelName, string? myTimezone)
    {
        var match = TimeRangeRegex().Match(channelName);
        if (!match.Success)
        {
            return null;
        }

        if (!TimeSpan.TryParse(match.Groups[1].Value, out TimeSpan startTime))
        {
            return null;
        }

        // Validate that it looks like a 24h clock value
        if (startTime.TotalHours >= 24 || startTime.Minutes >= 60)
        {
            return null;
        }

        TimeZoneInfo tz;
        try
        {
            tz = string.IsNullOrEmpty(myTimezone)
                ? TimeZoneInfo.Utc
                : TimeZoneInfo.FindSystemTimeZoneById(myTimezone);
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.Utc;
        }

        DateTime nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        DateTime startLocal = nowLocal.Date + startTime;

        // If start is more than 6 hours in the past, assume the event is tomorrow.
        if ((nowLocal - startLocal).TotalHours > 6)
        {
            startLocal = startLocal.AddDays(1);
        }

        DateTime endLocal;
        if (match.Groups[2].Success && TimeSpan.TryParse(match.Groups[2].Value, out TimeSpan endTime)
            && endTime.TotalHours < 24)
        {
            endLocal = startLocal.Date + endTime;
            if (endLocal <= startLocal)
            {
                // End time wraps to the next day (e.g. 22:00-01:00)
                endLocal = endLocal.AddDays(1);
            }
        }
        else
        {
            // Default to 3-hour event when no end time is specified
            endLocal = startLocal.AddHours(3);
        }

        DateTime startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, tz);
        DateTime endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, tz);

        // Strip the matched time token (and any surrounding brackets) from the title
        string cleanTitle = TimeRangeRegex().Replace(channelName, string.Empty).Trim(' ', '-', '–', ':').Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            cleanTitle = channelName.Trim();
        }

        return new PpvEpgResult(startUtc, endUtc, cleanTitle);
    }
}
