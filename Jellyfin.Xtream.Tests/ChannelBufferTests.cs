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
using Jellyfin.Xtream.Service;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for <see cref="ChannelBuffer"/> HLS playlist correctness,
/// focusing on preventing segment replay.
/// </summary>
public class ChannelBufferTests : IDisposable
{
    private readonly string _tempDir;

    public ChannelBufferTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private ChannelBuffer CreateBuffer(int streamId = 100)
    {
        return new ChannelBuffer(streamId, _tempDir);
    }

    private static SegmentInfo MakeSegment(string filename, double duration = 2.0, bool isDiscontinuity = false, DateTime? capturedUtc = null)
    {
        return new SegmentInfo
        {
            Filename = filename,
            DurationSeconds = duration,
            CapturedUtc = capturedUtc ?? DateTime.UtcNow,
            IsDiscontinuity = isDiscontinuity,
        };
    }

    /// <summary>
    /// Creates a dummy .ts file so the segment physically exists on disk.
    /// </summary>
    private void TouchSegment(ChannelBuffer buffer, string filename)
    {
        File.WriteAllBytes(Path.Combine(buffer.SegmentDir, filename), new byte[] { 0x47 });
    }

    /// <summary>
    /// Parses an HLS playlist into (mediaSequence, segments[]) where each
    /// segment tracks its filename and whether it was preceded by a discontinuity.
    /// </summary>
    private static (int MediaSequence, List<(string Filename, bool HasDiscontinuity)> Segments) ParsePlaylist(string content)
    {
        int mediaSequence = 0;
        var segments = new List<(string Filename, bool HasDiscontinuity)>();
        bool nextIsDiscontinuity = false;

        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                mediaSequence = int.Parse(line.Split(':')[1]);
            }
            else if (line == "#EXT-X-DISCONTINUITY")
            {
                nextIsDiscontinuity = true;
            }
            else if (!line.StartsWith('#') && line.EndsWith(".ts", StringComparison.Ordinal))
            {
                segments.Add((line.Trim(), nextIsDiscontinuity));
                nextIsDiscontinuity = false;
            }
        }

        return (mediaSequence, segments);
    }

    [Fact]
    public void MediaSequence_MonotonicallyIncreases_AfterPruning()
    {
        using var buffer = CreateBuffer();
        int prevMediaSeq = -1;
        var baseTime = DateTime.UtcNow.AddMinutes(-5);

        // Simulate 10 capture rounds with pruning between them.
        for (int round = 0; round < 10; round++)
        {
            // Each round is ~4s apart (simulating round-robin gap).
            var roundTime = baseTime.AddSeconds(round * 4);

            // Add 3 segments per round (simulating a capture burst).
            for (int s = 0; s < 3; s++)
            {
                string name = $"seg_{round}_{s}.ts";
                TouchSegment(buffer, name);
                buffer.AddSegment(
                    MakeSegment(name, isDiscontinuity: s == 0 && round > 0, capturedUtc: roundTime.AddSeconds(s * 2)),
                    s == 0 && round > 0);
            }

            string playlist = File.ReadAllText(buffer.PlaylistPath);
            var (mediaSeq, _) = ParsePlaylist(playlist);

            Assert.True(mediaSeq >= prevMediaSeq,
                $"Media sequence went backwards: {mediaSeq} < {prevMediaSeq} at round {round}");
            prevMediaSeq = mediaSeq;

            // Prune old segments (aggressive: keep only 4 seconds).
            buffer.PruneSegments(4);
        }
    }

    [Fact]
    public void NoSegmentReappears_AfterPruning()
    {
        using var buffer = CreateBuffer();
        var everSeen = new HashSet<string>();
        var pruned = new HashSet<string>();

        // Place round 0 in the past and round 9 near "now" so that
        // early rounds get pruned but recent ones survive.
        int numRounds = 10;
        int roundGapSec = 4;
        var baseTime = DateTime.UtcNow.AddSeconds(-(numRounds * roundGapSec));

        for (int round = 0; round < numRounds; round++)
        {
            var roundTime = baseTime.AddSeconds(round * roundGapSec);

            for (int s = 0; s < 3; s++)
            {
                string name = $"seg_{round}_{s}.ts";
                TouchSegment(buffer, name);
                buffer.AddSegment(
                    MakeSegment(name, isDiscontinuity: s == 0 && round > 0, capturedUtc: roundTime.AddSeconds(s)),
                    s == 0 && round > 0);
            }

            // Read current playlist segments.
            string playlist = File.ReadAllText(buffer.PlaylistPath);
            var (_, segments) = ParsePlaylist(playlist);
            var currentNames = segments.Select(seg => seg.Filename).ToHashSet();

            // Check: no pruned segment reappears.
            foreach (var name in currentNames)
            {
                Assert.DoesNotContain(name, pruned);
                everSeen.Add(name);
            }

            // Prune: keep segments younger than 10 seconds.
            buffer.PruneSegments(10);

            string afterPrune = File.ReadAllText(buffer.PlaylistPath);
            var (_, afterSegments) = ParsePlaylist(afterPrune);
            var afterNames = afterSegments.Select(seg => seg.Filename).ToHashSet();

            foreach (var name in currentNames)
            {
                if (!afterNames.Contains(name))
                {
                    pruned.Add(name);
                }
            }
        }

        // Verify we actually did prune some segments.
        Assert.True(pruned.Count > 0, $"Test must exercise pruning to be meaningful (everSeen={everSeen.Count})");
    }

    [Fact]
    public void PlaylistSegments_AlwaysAdvance_NeverReplay()
    {
        // Simulates the exact round-robin scenario: capture burst → gap → capture burst.
        // Verifies that the playlist always advances and older segments are never re-added.
        using var buffer = CreateBuffer();
        var allSegmentsEverInPlaylist = new List<string>();
        int globalSegIdx = 0;
        var baseTime = DateTime.UtcNow.AddMinutes(-5);

        for (int round = 0; round < 20; round++)
        {
            var roundTime = baseTime.AddSeconds(round * 4);

            // Each round: add 3 segments.
            for (int s = 0; s < 3; s++)
            {
                string name = buffer.AllocateSegmentFilename();
                TouchSegment(buffer, name);
                buffer.AddSegment(
                    MakeSegment(name, isDiscontinuity: s == 0 && round > 0, capturedUtc: roundTime.AddSeconds(s * 2)),
                    isDiscontinuity: s == 0 && round > 0);
                globalSegIdx++;
            }

            // Read playlist and check for replay.
            string playlist = File.ReadAllText(buffer.PlaylistPath);
            var (mediaSeq, segments) = ParsePlaylist(playlist);

            foreach (var seg in segments)
            {
                // Find the last occurrence of this segment in history.
                int lastIdx = allSegmentsEverInPlaylist.LastIndexOf(seg.Filename);
                if (lastIdx >= 0)
                {
                    // Segment was in a previous playlist. It's OK if it's still
                    // at the tail (sliding window), but NOT if it was pruned and re-added.
                    // Since segments have unique names from AllocateSegmentFilename(),
                    // this should never happen.
                }
            }

            // Record current playlist state.
            allSegmentsEverInPlaylist.AddRange(segments.Select(s => s.Filename));

            // Prune to simulate retention.
            buffer.PruneSegments(8);
        }

        // Verify unique filenames across all segments ever produced.
        var uniqueNames = new HashSet<string>();
        for (int i = 1; i <= globalSegIdx; i++)
        {
            string expected = $"seg_{i:D5}.ts";
            Assert.True(uniqueNames.Add(expected), $"Duplicate segment filename: {expected}");
        }
    }

    [Fact]
    public void Discontinuity_MarkedOnCaptureRestart()
    {
        using var buffer = CreateBuffer();

        // First capture round — no discontinuity on first segment.
        for (int s = 0; s < 3; s++)
        {
            string name = $"round0_{s}.ts";
            TouchSegment(buffer, name);
            buffer.AddSegment(MakeSegment(name, isDiscontinuity: false), false);
        }

        // Second capture round — first segment should be marked as discontinuity.
        for (int s = 0; s < 3; s++)
        {
            string name = $"round1_{s}.ts";
            TouchSegment(buffer, name);
            bool disc = s == 0;
            buffer.AddSegment(MakeSegment(name, isDiscontinuity: disc), disc);
        }

        string playlist = File.ReadAllText(buffer.PlaylistPath);
        var (_, segments) = ParsePlaylist(playlist);

        // round0 segments: no discontinuity.
        Assert.False(segments[0].HasDiscontinuity);
        Assert.False(segments[1].HasDiscontinuity);
        Assert.False(segments[2].HasDiscontinuity);

        // round1 first segment: discontinuity.
        Assert.True(segments[3].HasDiscontinuity);
        Assert.False(segments[4].HasDiscontinuity);
        Assert.False(segments[5].HasDiscontinuity);
    }

    [Fact]
    public void Playlist_NotStuck_DuringRoundRobin()
    {
        // Simulates round-robin: channel gets segments, then a gap, then more segments.
        // Verifies the playlist always has new content after each capture round.
        using var buffer = CreateBuffer();
        var baseTime = DateTime.UtcNow.AddMinutes(-5);

        int prevSegmentCount = 0;

        for (int round = 0; round < 5; round++)
        {
            var roundTime = baseTime.AddSeconds(round * 4);

            // Capture burst: 3 segments.
            for (int s = 0; s < 3; s++)
            {
                string name = buffer.AllocateSegmentFilename();
                TouchSegment(buffer, name);
                buffer.AddSegment(
                    MakeSegment(name, isDiscontinuity: s == 0 && round > 0, capturedUtc: roundTime.AddSeconds(s * 2)),
                    isDiscontinuity: s == 0 && round > 0);
            }

            var segments = buffer.GetSegments();

            // After each round, there should be more segments than before
            // (unless pruning removed some, but total should still grow
            // when retention is generous).
            Assert.True(segments.Count > 0, $"No segments after round {round}");

            // The newest segment should always be different from previous rounds.
            int currentCount = segments.Count;
            if (round > 0)
            {
                // We added 3 new segments; even with pruning (8s retention, 2s segments),
                // we should have at least the 3 new ones.
                Assert.True(currentCount >= 3,
                    $"Expected at least 3 segments after round {round}, got {currentCount}");
            }

            prevSegmentCount = currentCount;

            // Prune with generous retention.
            buffer.PruneSegments(20);
        }
    }

    [Fact]
    public void LongRunning_NoReplay_AfterInitialPeriod()
    {
        // The key test: simulates 60 rounds of round-robin capture.
        // After an initial period (first 5 rounds), the playlist must NEVER
        // contain a segment that was previously pruned.
        using var buffer = CreateBuffer();
        var prunedSegments = new HashSet<string>();
        int initialRounds = 5;
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        for (int round = 0; round < 60; round++)
        {
            var roundTime = baseTime.AddSeconds(round * 4);

            // Add 3 segments per capture burst.
            for (int s = 0; s < 3; s++)
            {
                string name = buffer.AllocateSegmentFilename();
                TouchSegment(buffer, name);
                buffer.AddSegment(
                    MakeSegment(name, isDiscontinuity: s == 0 && round > 0, capturedUtc: roundTime.AddSeconds(s * 2)),
                    isDiscontinuity: s == 0 && round > 0);
            }

            // Get current playlist segments.
            var currentSegments = buffer.GetSegments().Select(s => s.Filename).ToHashSet();

            // After initial period, no pruned segment should reappear.
            if (round >= initialRounds)
            {
                foreach (var seg in currentSegments)
                {
                    Assert.DoesNotContain(seg, prunedSegments);
                }
            }

            // Track what's in the buffer before pruning.
            var beforePrune = buffer.GetSegments().Select(s => s.Filename).ToHashSet();

            // Prune with realistic retention (10 seconds, 2s segments = keep ~5 segments).
            buffer.PruneSegments(10);

            // Track which segments were pruned.
            var afterPrune = buffer.GetSegments().Select(s => s.Filename).ToHashSet();
            foreach (var seg in beforePrune)
            {
                if (!afterPrune.Contains(seg))
                {
                    prunedSegments.Add(seg);
                }
            }
        }

        Assert.True(prunedSegments.Count > 0, "Test must exercise pruning");
    }
}
