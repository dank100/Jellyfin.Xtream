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
using Jellyfin.Xtream.Service;
using Xunit;

namespace Jellyfin.Xtream.Tests;

public class PpvEpgParserTests
{
    // All tests use UTC to make assertions timezone-independent.
    private const string UtcTz = "UTC";

    [Fact]
    public void TryParse_Nullish_NoTime_ReturnsNull()
    {
        Assert.Null(PpvEpgParser.TryParse("UFC 300: Jones vs Smith", UtcTz));
    }

    [Fact]
    public void TryParse_SingleTime_DefaultsDurationToThreeHours()
    {
        var result = PpvEpgParser.TryParse("UFC Fight Night 22:00", UtcTz);

        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromHours(22), result!.Start.TimeOfDay);
        Assert.Equal(TimeSpan.FromHours(3), result.End - result.Start);
    }

    [Fact]
    public void TryParse_ExplicitRange_SameDay()
    {
        var result = PpvEpgParser.TryParse("F1 GP Monaco [14:00-17:00]", UtcTz);

        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromHours(14), result!.Start.TimeOfDay);
        Assert.Equal(TimeSpan.FromHours(17), result.End.TimeOfDay);
        Assert.Equal(result.Start.Date, result.End.Date);
    }

    [Fact]
    public void TryParse_RangeWrappingMidnight_EndIsNextDay()
    {
        var result = PpvEpgParser.TryParse("Boxing PPV 22:00-01:00", UtcTz);

        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromHours(22), result!.Start.TimeOfDay);
        Assert.Equal(TimeSpan.FromHours(1), result.End.TimeOfDay);
        Assert.Equal(result.Start.Date.AddDays(1), result.End.Date);
    }

    [Fact]
    public void TryParse_RangeWithSpaces_ParsesCorrectly()
    {
        var result = PpvEpgParser.TryParse("PPV Event 20:00 - 23:30", UtcTz);

        Assert.NotNull(result);
        Assert.Equal(new TimeSpan(20, 0, 0), result!.Start.TimeOfDay);
        Assert.Equal(new TimeSpan(23, 30, 0), result.End.TimeOfDay);
    }

    [Fact]
    public void TryParse_BracketedRange_ParsesCorrectly()
    {
        var result = PpvEpgParser.TryParse("F1 Qualifying (14:00-16:00)", UtcTz);

        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromHours(14), result!.Start.TimeOfDay);
        Assert.Equal(TimeSpan.FromHours(16), result.End.TimeOfDay);
    }

    [Fact]
    public void TryParse_CleanTitle_TimeRemovedFromName()
    {
        var result = PpvEpgParser.TryParse("UFC 300: Jones vs Smith 22:00-01:00", UtcTz);

        Assert.NotNull(result);
        Assert.Equal("UFC 300: Jones vs Smith", result!.CleanTitle);
    }

    [Fact]
    public void TryParse_CleanTitle_BracketedTimeRemovedFromName()
    {
        var result = PpvEpgParser.TryParse("F1 GP Monaco [14:00-17:00]", UtcTz);

        Assert.NotNull(result);
        Assert.Equal("F1 GP Monaco", result!.CleanTitle);
    }

    [Fact]
    public void TryParse_NoTimezone_UsesUtc()
    {
        var result = PpvEpgParser.TryParse("Event 12:00", null);
        var resultUtc = PpvEpgParser.TryParse("Event 12:00", "UTC");

        Assert.NotNull(result);
        Assert.NotNull(resultUtc);
        Assert.Equal(result!.Start, resultUtc!.Start);
    }

    [Fact]
    public void TryParse_InvalidTimezone_FallsBackToUtc()
    {
        var resultBad = PpvEpgParser.TryParse("Event 12:00", "Invalid/Timezone");
        var resultUtc = PpvEpgParser.TryParse("Event 12:00", "UTC");

        Assert.NotNull(resultBad);
        Assert.NotNull(resultUtc);
        Assert.Equal(resultBad!.Start, resultUtc!.Start);
    }

    [Fact]
    public void TryParse_InvalidHour_ReturnsNull()
    {
        // 25:00 is not a valid clock time
        Assert.Null(PpvEpgParser.TryParse("Event 25:00", UtcTz));
    }

    [Fact]
    public void TryParse_StartDateInUTC_IsKindUtc()
    {
        var result = PpvEpgParser.TryParse("Event 10:00", UtcTz);

        Assert.NotNull(result);
        Assert.Equal(DateTimeKind.Utc, result!.Start.Kind);
        Assert.Equal(DateTimeKind.Utc, result.End.Kind);
    }
}
