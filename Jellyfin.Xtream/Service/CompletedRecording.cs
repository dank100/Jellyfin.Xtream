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
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Represents a completed recording.
/// </summary>
public class CompletedRecording
{
    /// <summary>
    /// Gets or sets the timer ID.
    /// </summary>
    public string TimerId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timer info.
    /// </summary>
    public TimerInfo Timer { get; set; } = null!;

    /// <summary>
    /// Gets or sets the file path of the recording.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the recording was completed.
    /// </summary>
    public DateTime CompletedAt { get; set; }
}
