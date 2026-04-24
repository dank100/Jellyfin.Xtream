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
/// Metadata for a single HLS segment captured by the multiplexer.
/// </summary>
public sealed class SegmentInfo
{
    /// <summary>
    /// Gets the filename (not full path) of the segment, e.g. "seg_00001.ts".
    /// </summary>
    public required string Filename { get; init; }

    /// <summary>
    /// Gets the duration in seconds of this segment.
    /// </summary>
    public required double DurationSeconds { get; init; }

    /// <summary>
    /// Gets the UTC time at which this segment was captured.
    /// </summary>
    public required DateTime CapturedUtc { get; init; }
}
