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

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Represents a programme from an XMLTV source.
/// </summary>
public class XmltvProgramme
{
    /// <summary>
    /// Gets or sets the XMLTV channel ID.
    /// </summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the start time in UTC.
    /// </summary>
    public DateTime Start { get; set; }

    /// <summary>
    /// Gets or sets the stop time in UTC.
    /// </summary>
    public DateTime Stop { get; set; }

    /// <summary>
    /// Gets or sets the programme title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the programme description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the programme category.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Gets or sets the programme icon URL.
    /// </summary>
    public string? Icon { get; set; }
}
