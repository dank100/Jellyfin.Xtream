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

#pragma warning disable CA1815
namespace Jellyfin.Xtream.Service;

/// <summary>
/// Holds parsed series/episode metadata extracted from EPG data.
/// </summary>
public readonly struct EpgSeriesInfo
{
    /// <summary>
    /// Gets the deterministic series ID derived from the programme title.
    /// </summary>
    public string SeriesId { get; init; }

    /// <summary>
    /// Gets the season number, or null if not detected.
    /// </summary>
    public int? SeasonNumber { get; init; }

    /// <summary>
    /// Gets the episode number, or null if not detected.
    /// </summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>
    /// Gets the episode title, or null if not detected.
    /// </summary>
    public string? EpisodeTitle { get; init; }

    /// <summary>
    /// Gets a value indicating whether series information was detected.
    /// </summary>
    public bool IsSeries { get; init; }
}
