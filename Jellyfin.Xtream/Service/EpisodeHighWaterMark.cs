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

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Tracks the highest season and episode number that has been scheduled for a series timer.
/// When RecordNewOnly is enabled, only episodes beyond this mark are recorded.
/// </summary>
public class EpisodeHighWaterMark
{
    /// <summary>
    /// Gets or sets the highest season number that has been scheduled.
    /// </summary>
    public int Season { get; set; }

    /// <summary>
    /// Gets or sets the highest episode number that has been scheduled within the highest season.
    /// </summary>
    public int Episode { get; set; }
}
