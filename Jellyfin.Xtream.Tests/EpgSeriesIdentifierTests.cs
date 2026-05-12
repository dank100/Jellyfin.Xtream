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

using Jellyfin.Xtream.Service;

namespace Jellyfin.Xtream.Tests;

public class EpgSeriesIdentifierTests
{
    [Fact]
    public void Parse_DanishSeasonEpisodeInDescription_ExtractsCorrectly()
    {
        var result = EpgSeriesIdentifier.Parse(
            "Først til verdens ende",
            "En fantastisk rejse. Sæson 1 Episode 10");

        Assert.True(result.IsSeries);
        Assert.Equal(1, result.SeasonNumber);
        Assert.Equal(10, result.EpisodeNumber);
        Assert.NotEmpty(result.SeriesId);
    }

    [Fact]
    public void Parse_CompactFormat_ExtractsCorrectly()
    {
        var result = EpgSeriesIdentifier.Parse(
            "Breaking Bad S03E05",
            "Walter deals with consequences.");

        Assert.True(result.IsSeries);
        Assert.Equal(3, result.SeasonNumber);
        Assert.Equal(5, result.EpisodeNumber);
    }

    [Fact]
    public void Parse_EnglishVerboseFormat_ExtractsCorrectly()
    {
        var result = EpgSeriesIdentifier.Parse(
            "The Crown",
            "Season 2, Episode 7. The Queen faces challenges.");

        Assert.True(result.IsSeries);
        Assert.Equal(2, result.SeasonNumber);
        Assert.Equal(7, result.EpisodeNumber);
    }

    [Fact]
    public void Parse_SwedishFormat_ExtractsCorrectly()
    {
        var result = EpgSeriesIdentifier.Parse(
            "Bron",
            "Säsong 3 Avsnitt 8");

        Assert.True(result.IsSeries);
        Assert.Equal(3, result.SeasonNumber);
        Assert.Equal(8, result.EpisodeNumber);
    }

    [Fact]
    public void Parse_EpisodeOnlyInDescription_ExtractsEpisode()
    {
        var result = EpgSeriesIdentifier.Parse(
            "Nyhederne",
            "Afsnit 42 af sæsonens nyheder.");

        Assert.True(result.IsSeries);
        Assert.Equal(42, result.EpisodeNumber);
    }

    [Fact]
    public void Parse_ParenthesizedEpisode_ExtractsEpisode()
    {
        var result = EpgSeriesIdentifier.Parse(
            "Vild med dans",
            "Danseholdet kæmper videre (8/12)");

        Assert.True(result.IsSeries);
        Assert.Equal(8, result.EpisodeNumber);
    }

    [Fact]
    public void Parse_NoSeriesInfo_ReturnsNotSeries()
    {
        var result = EpgSeriesIdentifier.Parse(
            "Nyhederne kl. 21",
            "Dagens nyheder fra ind- og udland.");

        Assert.False(result.IsSeries);
        Assert.Null(result.SeasonNumber);
        Assert.Null(result.EpisodeNumber);
        Assert.NotEmpty(result.SeriesId);
    }

    [Fact]
    public void GenerateSeriesId_SameTitleDifferentEpisodes_ProducesSameId()
    {
        var result1 = EpgSeriesIdentifier.Parse(
            "Først til verdens ende",
            "Sæson 1 Episode 10");

        var result2 = EpgSeriesIdentifier.Parse(
            "Først til verdens ende",
            "Sæson 1 Episode 11");

        Assert.Equal(result1.SeriesId, result2.SeriesId);
    }

    [Fact]
    public void GenerateSeriesId_DifferentTitles_ProducesDifferentIds()
    {
        var result1 = EpgSeriesIdentifier.Parse("Show A", "Season 1 Episode 1");
        var result2 = EpgSeriesIdentifier.Parse("Show B", "Season 1 Episode 1");

        Assert.NotEqual(result1.SeriesId, result2.SeriesId);
    }

    [Fact]
    public void GenerateSeriesId_CaseInsensitive_ProducesSameId()
    {
        string id1 = EpgSeriesIdentifier.GenerateSeriesId("The Crown");
        string id2 = EpgSeriesIdentifier.GenerateSeriesId("the crown");

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void GenerateSeriesId_WhitespaceInsensitive_ProducesSameId()
    {
        string id1 = EpgSeriesIdentifier.GenerateSeriesId("The  Crown");
        string id2 = EpgSeriesIdentifier.GenerateSeriesId("The Crown");

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Parse_NullDescription_DoesNotThrow()
    {
        var result = EpgSeriesIdentifier.Parse("Some Show", null);

        Assert.False(result.IsSeries);
        Assert.NotEmpty(result.SeriesId);
    }

    [Fact]
    public void Parse_SeasonOnlyInDescription_IsSportSeries()
    {
        // Season-only (no episode) is treated as a sport series
        var result = EpgSeriesIdentifier.Parse(
            "Borgen",
            "Sæson 3. Birgitte tager en stor beslutning.");

        Assert.True(result.IsSeries);
        Assert.True(result.IsSport);
        Assert.Equal(3, result.SeasonNumber);
        Assert.Null(result.EpisodeNumber);
    }

    [Fact]
    public void Parse_SportsSeasonIsSportSeries()
    {
        var result = EpgSeriesIdentifier.Parse(
            "Liga Portugal: Benfica-Braga",
            "Sæson: 26. . Benfica og danske Alexander Bah får besøg af Braga i 33. spillerunde.");

        Assert.True(result.IsSeries);
        Assert.True(result.IsSport);
        Assert.Equal(26, result.SeasonNumber);
        Assert.Null(result.EpisodeNumber);
    }

    [Fact]
    public void Parse_RegularSeries_NotSport()
    {
        var result = EpgSeriesIdentifier.Parse(
            "Game of Thrones",
            "Season 3 Episode 9. The wedding.");

        Assert.True(result.IsSeries);
        Assert.False(result.IsSport);
        Assert.Equal(3, result.SeasonNumber);
        Assert.Equal(9, result.EpisodeNumber);
    }

    [Fact]
    public void Parse_QuotedEpisodeTitle_ExtractsTitle()
    {
        var result = EpgSeriesIdentifier.Parse(
            "Game of Thrones",
            "\"The Rains of Castamere\" Season 3 Episode 9");

        Assert.True(result.IsSeries);
        Assert.Equal("The Rains of Castamere", result.EpisodeTitle);
        Assert.Equal(3, result.SeasonNumber);
        Assert.Equal(9, result.EpisodeNumber);
    }

    [Fact]
    public void Parse_DanishColonFormat_ExtractsCorrectly()
    {
        var result = EpgSeriesIdentifier.Parse(
            "Kontant: Hvem stopper iden",
            "Sæson: 2026. Episode: 7. 'Hvem stopper identitetstyvene?'. Jacob Kragelund har et hemmeligt investeringstip.");

        Assert.True(result.IsSeries);
        Assert.Equal(2026, result.SeasonNumber);
        Assert.Equal(7, result.EpisodeNumber);
        Assert.Equal("Hvem stopper identitetstyvene?", result.EpisodeTitle);
    }

    [Fact]
    public void Parse_DanishColonFormat_SeriesIdFromTitle()
    {
        var result1 = EpgSeriesIdentifier.Parse(
            "Kontant: Hvem stopper iden",
            "Sæson: 2026. Episode: 7. 'Hvem stopper identitetstyvene?'.");

        var result2 = EpgSeriesIdentifier.Parse(
            "Kontant: Hvem stopper iden",
            "Sæson: 2026. Episode: 8. 'Ny episode'.");

        // Same title produces same SeriesId
        Assert.Equal(result1.SeriesId, result2.SeriesId);
    }

    [Fact]
    public void Parse_WordBoundary_DelInsideWord_NotDetected()
    {
        // "model 15" should NOT match "Del 15" pattern
        var result = EpgSeriesIdentifier.Parse(
            "Indycar Highlights",
            "It's NTT IndyCar Series race day. This model 15 car is fast.");

        Assert.False(result.IsSeries);
        Assert.Null(result.SeasonNumber);
        Assert.Null(result.EpisodeNumber);
    }

    [Fact]
    public void Parse_WordBoundary_EpInsideWord_NotDetected()
    {
        // "Prep 5" should NOT match "Ep 5" pattern
        var result = EpgSeriesIdentifier.Parse(
            "Race Preview",
            "The prep 5 session was cancelled due to rain.");

        Assert.False(result.IsSeries);
        Assert.Null(result.EpisodeNumber);
    }

    [Fact]
    public void Parse_WordBoundary_DelAsStandaloneWord_Detected()
    {
        // "Del 15" as a standalone word SHOULD match
        var result = EpgSeriesIdentifier.Parse(
            "Nature Documentary",
            "Del 15. The arctic fox hunts in the snow.");

        Assert.True(result.IsSeries);
        Assert.Equal(15, result.EpisodeNumber);
    }
}
