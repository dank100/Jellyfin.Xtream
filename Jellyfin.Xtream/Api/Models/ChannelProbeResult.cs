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

using System.Collections.ObjectModel;

namespace Jellyfin.Xtream.Api.Models;

/// <summary>
/// Result of probing a live TV channel stream.
/// </summary>
public class ChannelProbeResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the probe succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the error message if the probe failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the video codec name (e.g., "h264", "hevc", "mpeg2video").
    /// </summary>
    public string? VideoCodec { get; set; }

    /// <summary>
    /// Gets or sets the audio codec name (e.g., "aac", "mp3", "ac3").
    /// </summary>
    public string? AudioCodec { get; set; }

    /// <summary>
    /// Gets or sets the video width in pixels.
    /// </summary>
    public int? Width { get; set; }

    /// <summary>
    /// Gets or sets the video height in pixels.
    /// </summary>
    public int? Height { get; set; }

    /// <summary>
    /// Gets or sets the video frame rate.
    /// </summary>
    public double? FrameRate { get; set; }

    /// <summary>
    /// Gets or sets the video codec profile (e.g., "Main", "High").
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>
    /// Gets or sets the video codec level.
    /// </summary>
    public int? Level { get; set; }

    /// <summary>
    /// Gets or sets the container format detected by ffprobe.
    /// </summary>
    public string? Container { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the video is interlaced.
    /// </summary>
    public bool? IsInterlaced { get; set; }

    /// <summary>
    /// Gets a list of client types estimated to support direct play.
    /// This is an estimate based on codec/container — actual playback depends on client settings.
    /// </summary>
    public Collection<string> EstimatedDirectPlay { get; } = new();
}
