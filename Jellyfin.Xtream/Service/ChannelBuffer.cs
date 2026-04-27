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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Per-channel buffer used by <see cref="ConnectionMultiplexer"/>.
/// Holds the rolling HLS playlist and segment metadata for a single channel.
/// </summary>
public sealed class ChannelBuffer : IDisposable
{
    private readonly object _lock = new();
    private readonly List<SegmentInfo> _segments = [];
    private int _segmentCounter;
    private int _prunedCount;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelBuffer"/> class.
    /// </summary>
    /// <param name="streamId">The Xtream stream ID for this channel.</param>
    /// <param name="baseDir">Root temp directory for multiplexer segments.</param>
    public ChannelBuffer(int streamId, string baseDir)
    {
        StreamId = streamId;
        SegmentDir = Path.Combine(baseDir, $"mux_{streamId}");
        Directory.CreateDirectory(SegmentDir);
        PlaylistPath = Path.Combine(SegmentDir, "playlist.m3u8");
    }

    /// <summary>Gets the Xtream stream ID.</summary>
    public int StreamId { get; }

    /// <summary>Gets the directory where segments are stored.</summary>
    public string SegmentDir { get; }

    /// <summary>Gets the path to the HLS playlist file.</summary>
    public string PlaylistPath { get; }

    /// <summary>Gets or sets the current channel state.</summary>
    public ChannelBufferState State { get; set; } = ChannelBufferState.Idle;

    /// <summary>Gets or sets the subscriber (viewer) count.</summary>
    public int SubscriberCount { get; set; }

    /// <summary>Gets or sets the number of live TV viewers (subset of subscribers).</summary>
    public int LiveViewerCount { get; set; }

    /// <summary>Gets the current discontinuity sequence number for the HLS playlist.</summary>
    public int DiscontinuitySequence { get; private set; }

    /// <summary>
    /// Gets or sets the accumulated timestamp offset in seconds.
    /// Each successful capture advances this by the segment's actual duration,
    /// so the next capture's <c>-output_ts_offset</c> produces continuous PTS.
    /// </summary>
    public double TsOffsetSeconds { get; set; }

    /// <summary>
    /// Allocates the next segment filename for this channel.
    /// </summary>
    /// <returns>The filename (not full path) for the new segment.</returns>
    public string AllocateSegmentFilename()
    {
        int num = Interlocked.Increment(ref _segmentCounter);
        return $"seg_{num:D5}.ts";
    }

    /// <summary>
    /// Registers a newly captured segment and rebuilds the HLS playlist.
    /// </summary>
    /// <param name="segment">The segment metadata.</param>
    /// <param name="isDiscontinuity">Whether to insert an EXT-X-DISCONTINUITY before this segment.</param>
    public void AddSegment(SegmentInfo segment, bool isDiscontinuity)
    {
        lock (_lock)
        {
            if (isDiscontinuity)
            {
                DiscontinuitySequence++;
            }

            _segments.Add(segment);
            RebuildPlaylist();

            if (State == ChannelBufferState.Buffering)
            {
                State = ChannelBufferState.Ready;
            }
        }
    }

    /// <summary>
    /// Removes segments older than <paramref name="retentionSeconds"/> and deletes
    /// the corresponding files from disk.
    /// </summary>
    /// <param name="retentionSeconds">Maximum age of segments to keep.</param>
    public void PruneSegments(int retentionSeconds)
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-retentionSeconds);
            var expired = _segments.Where(s => s.CapturedUtc < cutoff).ToList();
            foreach (var seg in expired)
            {
                _segments.Remove(seg);
                _prunedCount++;
                var path = Path.Combine(SegmentDir, seg.Filename);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            if (expired.Count > 0)
            {
                RebuildPlaylist();
            }
        }
    }

    /// <summary>
    /// Gets a snapshot of the current segments.
    /// </summary>
    /// <returns>A list copy of the current segments.</returns>
    public IReadOnlyList<SegmentInfo> GetSegments()
    {
        lock (_lock)
        {
            return _segments.ToList();
        }
    }

    /// <summary>
    /// Converts a local segment list index to a global index that accounts for pruned segments.
    /// </summary>
    /// <param name="localIndex">Index into the current segment list.</param>
    /// <returns>The global segment index.</returns>
    public int GetGlobalIndex(int localIndex)
    {
        lock (_lock)
        {
            return _prunedCount + localIndex;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Clean up segment directory
        if (Directory.Exists(SegmentDir))
        {
            try
            {
                Directory.Delete(SegmentDir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup
            }
        }
    }

    /// <summary>
    /// Rebuilds the HLS playlist file from the current segment list.
    /// Must be called under <see cref="_lock"/>.
    /// </summary>
    private void RebuildPlaylist()
    {
        if (_segments.Count == 0)
        {
            return;
        }

        double targetDuration = _segments.Max(s => s.DurationSeconds);
        var lines = new List<string>
        {
            "#EXTM3U",
            $"#EXT-X-VERSION:3",
            $"#EXT-X-TARGETDURATION:{Math.Ceiling(targetDuration):F0}",
            $"#EXT-X-MEDIA-SEQUENCE:{_prunedCount}",
        };

        bool needDiscontinuity = false;
        DateTime? lastCapture = null;

        foreach (var seg in _segments)
        {
            // Insert discontinuity when there is a time gap between captures
            // (each round-robin cycle reconnects to the stream)
            if (lastCapture.HasValue && (seg.CapturedUtc - lastCapture.Value).TotalSeconds > seg.DurationSeconds * 2)
            {
                needDiscontinuity = true;
            }

            if (needDiscontinuity)
            {
                lines.Add("#EXT-X-DISCONTINUITY");
                needDiscontinuity = false;
            }

            lines.Add($"#EXTINF:{seg.DurationSeconds:F3},");
            lines.Add(seg.Filename);
            lastCapture = seg.CapturedUtc;
        }

        File.WriteAllLines(PlaylistPath, lines);
    }
}
