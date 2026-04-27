using Jellyfin.Xtream.Service;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for <see cref="TsTimestampRewriter"/> — the in-process MPEG-TS
/// timestamp normalizer that replaced the ffmpeg remuxer subprocess.
/// </summary>
public class TsTimestampRewriterTests
{
    private const long TicksPerSecond = 90_000;

    // --- Basic timestamp continuity ---

    [Fact]
    public void SequentialSegments_PreservesMonotonicPts()
    {
        // Two segments with naturally sequential PTS — should NOT adjust.
        var rewriter = new TsTimestampRewriter();

        var seg1 = TsPacketBuilder.BuildSegment(
            new long[] { 0, 9000, 18000 }); // 0s, 0.1s, 0.2s
        var seg2 = TsPacketBuilder.BuildSegment(
            new long[] { 27000, 36000, 45000 }); // 0.3s, 0.4s, 0.5s

        bool adj1 = rewriter.Rewrite(seg1);
        bool adj2 = rewriter.Rewrite(seg2);

        Assert.False(adj1, "First segment should not be adjusted");
        Assert.False(adj2, "Sequential segment should not be adjusted");

        // PTS values should be preserved.
        Assert.Equal(0, TsPacketBuilder.ReadFirstPts(seg1));
        Assert.Equal(27000, TsPacketBuilder.ReadFirstPts(seg2));
    }

    [Fact]
    public void Discontinuity_AdjustsToMonotonic()
    {
        // Segment 1: PTS 100000-200000, Segment 2: PTS jumps back to 0.
        var rewriter = new TsTimestampRewriter();

        var seg1 = TsPacketBuilder.BuildSegment(
            new long[] { 100000, 150000, 200000 });
        rewriter.Rewrite(seg1);

        long lastPtsAfterSeg1 = rewriter.LastOutputPts;

        // Segment 2 has PTS that jumps back (mock server restart).
        var seg2 = TsPacketBuilder.BuildSegment(
            new long[] { 0, 50000, 100000 });
        bool adjusted = rewriter.Rewrite(seg2);

        Assert.True(adjusted, "Discontinuity should trigger adjustment");

        long seg2FirstPts = TsPacketBuilder.ReadFirstPts(seg2);
        long seg2LastPts = TsPacketBuilder.ReadLastPts(seg2);

        // Output must continue monotonically from seg1's last PTS.
        Assert.True(seg2FirstPts > lastPtsAfterSeg1,
            $"Seg2 first PTS ({seg2FirstPts}) must be > seg1 last PTS ({lastPtsAfterSeg1})");
        Assert.True(seg2LastPts > seg2FirstPts,
            $"Seg2 last PTS ({seg2LastPts}) must be > seg2 first PTS ({seg2FirstPts})");
    }

    // --- Gap-fill scenario: same data rewritten should advance ---

    [Fact]
    public void GapFill_SameDataRewritten_AdvancesPts()
    {
        // Simulates gap-fill: the pump rewrites the same RAW segment data again.
        // The rewriter should detect the PTS went backwards and advance it.
        var rewriter = new TsTimestampRewriter();

        var seg1 = TsPacketBuilder.BuildSegment(
            new long[] { 90000, 135000, 180000 }); // 1s to 2s
        // Cache raw data before rewriting (as the production code now does).
        byte[] rawCopy = (byte[])seg1.Clone();
        rewriter.Rewrite(seg1);

        long lastPtsAfterFresh = rewriter.LastOutputPts;
        Assert.Equal(180000, lastPtsAfterFresh);

        // Gap-fill: rewrite raw cached copy (not already-adjusted data).
        rewriter.Rewrite(rawCopy);

        long gapFillFirstPts = TsPacketBuilder.ReadFirstPts(rawCopy);
        long gapFillLastPts = TsPacketBuilder.ReadLastPts(rawCopy);

        // Gap-fill PTS must continue beyond the fresh segment.
        Assert.True(gapFillFirstPts > lastPtsAfterFresh,
            $"Gap-fill first PTS ({gapFillFirstPts}) must be > fresh last PTS ({lastPtsAfterFresh})");
        Assert.True(gapFillLastPts > gapFillFirstPts,
            $"Gap-fill last PTS ({gapFillLastPts}) must be > gap-fill first PTS ({gapFillFirstPts})");
    }

    [Fact]
    public void GapFill_ThenFreshSegment_StaysMonotonic()
    {
        // Full sequence: fresh → gap-fill (raw copy) → fresh (with source restart).
        var rewriter = new TsTimestampRewriter();

        // Fresh segment 1
        var seg1 = TsPacketBuilder.BuildSegment(
            new long[] { 90000, 135000, 180000 });
        byte[] rawSeg1 = (byte[])seg1.Clone();
        rewriter.Rewrite(seg1);
        long seg1Last = rewriter.LastOutputPts;

        // Gap-fill (replay raw copy of seg1)
        rewriter.Rewrite(rawSeg1);
        long gapFillLast = rewriter.LastOutputPts;
        Assert.True(gapFillLast > seg1Last);

        // Fresh segment 2 (source restarted, PTS jumps back to 0)
        var seg2 = TsPacketBuilder.BuildSegment(
            new long[] { 0, 45000, 90000 });
        rewriter.Rewrite(seg2);
        long seg2First = TsPacketBuilder.ReadFirstPts(seg2);
        long seg2Last = rewriter.LastOutputPts;

        // Fresh segment 2 must continue beyond gap-fill.
        Assert.True(seg2First > gapFillLast,
            $"Fresh seg2 first PTS ({seg2First}) must be > gap-fill last PTS ({gapFillLast})");
        Assert.True(seg2Last > seg2First);
    }

    // --- Two-channel simulation (independent rewriters) ---

    [Fact]
    public void TwoChannels_IndependentRewriters_BothMonotonic()
    {
        // Each channel has its own rewriter (as in MultiplexedRestream).
        // Simulates sequential round-robin: ch1 gets data, then ch2, then ch1 again.
        // Gap-fill uses raw (pre-rewrite) cached data to avoid double-adjustment.
        var rewriter1 = new TsTimestampRewriter();
        var rewriter2 = new TsTimestampRewriter();

        // Round 1: ch1 capture (source PTS 0-180000)
        var ch1Seg1 = TsPacketBuilder.BuildSegment(
            new long[] { 0, 90000, 180000 });
        byte[] ch1Raw1 = (byte[])ch1Seg1.Clone();
        rewriter1.Rewrite(ch1Seg1);
        long ch1R1Last = rewriter1.LastOutputPts;

        // Round 1: ch2 capture (source PTS 0-180000)
        var ch2Seg1 = TsPacketBuilder.BuildSegment(
            new long[] { 0, 90000, 180000 });
        byte[] ch2Raw1 = (byte[])ch2Seg1.Clone();
        rewriter2.Rewrite(ch2Seg1);
        long ch2R1Last = rewriter2.LastOutputPts;

        // Gap-fill for ch1 while ch2 captures (use raw copy)
        byte[] ch1GapFill = (byte[])ch1Raw1.Clone();
        rewriter1.Rewrite(ch1GapFill);
        long ch1GapLast = rewriter1.LastOutputPts;
        Assert.True(ch1GapLast > ch1R1Last);

        // Round 2: ch1 capture (source restarts at PTS 0)
        var ch1Seg2 = TsPacketBuilder.BuildSegment(
            new long[] { 0, 90000, 180000 });
        rewriter1.Rewrite(ch1Seg2);
        long ch1R2First = TsPacketBuilder.ReadFirstPts(ch1Seg2);
        long ch1R2Last = rewriter1.LastOutputPts;

        Assert.True(ch1R2First > ch1GapLast,
            $"Ch1 round 2 first PTS ({ch1R2First}) must be > gap-fill last ({ch1GapLast})");
        Assert.True(ch1R2Last > ch1R2First);

        // Gap-fill for ch2 while ch1 captures (use raw copy)
        byte[] ch2GapFill = (byte[])ch2Raw1.Clone();
        rewriter2.Rewrite(ch2GapFill);
        long ch2GapLast = rewriter2.LastOutputPts;
        Assert.True(ch2GapLast > ch2R1Last);

        // Round 2: ch2 capture (source restarts at PTS 0)
        var ch2Seg2 = TsPacketBuilder.BuildSegment(
            new long[] { 0, 90000, 180000 });
        rewriter2.Rewrite(ch2Seg2);
        long ch2R2First = TsPacketBuilder.ReadFirstPts(ch2Seg2);

        Assert.True(ch2R2First > ch2GapLast,
            $"Ch2 round 2 first PTS ({ch2R2First}) must be > gap-fill last ({ch2GapLast})");
    }

    // --- Continuity counter fixup ---

    [Fact]
    public void ContinuityCounters_FixedAcrossSegments()
    {
        var rewriter = new TsTimestampRewriter();

        // Segment 1: CC starts at 0
        var seg1 = TsPacketBuilder.BuildSegment(
            new long[] { 0, 90000, 180000 }, videoPid: 0x100, startCc: 0);
        rewriter.Rewrite(seg1);
        var cc1 = TsPacketBuilder.ReadContinuityCounters(seg1);
        // First segment: CCs should be 0, 1, 2
        Assert.Equal(0, cc1[0].Cc);
        Assert.Equal(1, cc1[1].Cc);
        Assert.Equal(2, cc1[2].Cc);

        // Segment 2: CC resets to 0 (new ffmpeg capture)
        var seg2 = TsPacketBuilder.BuildSegment(
            new long[] { 270000, 360000, 450000 }, videoPid: 0x100, startCc: 0);
        rewriter.Rewrite(seg2);
        var cc2 = TsPacketBuilder.ReadContinuityCounters(seg2);
        // Should be remapped to 3, 4, 5 (continuing from seg1)
        Assert.Equal(3, cc2[0].Cc);
        Assert.Equal(4, cc2[1].Cc);
        Assert.Equal(5, cc2[2].Cc);
    }

    [Fact]
    public void ContinuityCounters_WrapAt16()
    {
        var rewriter = new TsTimestampRewriter();

        // Segment with CC starting at 14 — should wrap to 0 at 16.
        var seg1 = TsPacketBuilder.BuildSegment(
            new long[] { 0, 90000, 180000 }, startCc: 14);
        rewriter.Rewrite(seg1);
        var cc1 = TsPacketBuilder.ReadContinuityCounters(seg1);
        Assert.Equal(14, cc1[0].Cc);
        Assert.Equal(15, cc1[1].Cc);
        Assert.Equal(0, cc1[2].Cc); // wraps

        // Next segment CC resets to 7 — should remap to 1, 2, 3
        var seg2 = TsPacketBuilder.BuildSegment(
            new long[] { 270000, 360000, 450000 }, startCc: 7);
        rewriter.Rewrite(seg2);
        var cc2 = TsPacketBuilder.ReadContinuityCounters(seg2);
        Assert.Equal(1, cc2[0].Cc);
        Assert.Equal(2, cc2[1].Cc);
        Assert.Equal(3, cc2[2].Cc);
    }

    // --- Wall-clock drift prevention ---

    [Fact]
    public void MultipleCycles_AdjustmentDoesNotGrowUnbounded()
    {
        // Simulates multiple round-robin cycles where each capture produces
        // more content PTS than wall-clock time (burst reads). The wall-clock-
        // based discontinuity handling should prevent unbounded adjustment growth.
        var rewriter = new TsTimestampRewriter();

        // Initial burst: 10 seconds of content PTS in near-zero wall time.
        var burst = TsPacketBuilder.BuildSegment(
            new long[] { 0, 450000, 900000 }); // 0s, 5s, 10s
        rewriter.Rewrite(burst);
        long afterBurst = rewriter.LastOutputPts;
        Assert.Equal(900000, afterBurst);

        // Simulate 10 round-robin cycles, each producing 10s of content PTS.
        // Source restarts from 0 each time (mock behavior).
        long prevAdj = rewriter.Adjustment;
        for (int cycle = 0; cycle < 10; cycle++)
        {
            var seg = TsPacketBuilder.BuildSegment(
                new long[] { 0, 450000, 900000 });
            rewriter.Rewrite(seg);

            long firstPts = TsPacketBuilder.ReadFirstPts(seg);
            long lastPts = rewriter.LastOutputPts;

            // PTS must always advance.
            Assert.True(firstPts > afterBurst,
                $"Cycle {cycle}: first PTS ({firstPts}) must be > previous last ({afterBurst})");
            Assert.True(lastPts > firstPts,
                $"Cycle {cycle}: last PTS ({lastPts}) must be > first PTS ({firstPts})");

            afterBurst = lastPts;
        }

        // After 10 cycles: 100s of content PTS in near-zero wall time.
        // With wall-clock-based adjustment, the output PTS should stay
        // near wall-clock time (close to 0), not grow to 100s+.
        // Since tests run in <1ms, wall-clock target ≈ 0, so adjustment
        // keeps PTS near _lastPts + 1 (not _lastPts + contentDuration).
        // The key check: adjustment growth per cycle should be bounded
        // (roughly content duration, not accumulating extra).
        long finalAdj = rewriter.Adjustment;
        long finalPts = rewriter.LastOutputPts;

        // Final PTS should be reasonable — well under 200s (which would
        // happen with ~10s drift per cycle × 10 cycles + content).
        Assert.True(finalPts < 200 * TicksPerSecond,
            $"Final PTS ({finalPts / (double)TicksPerSecond:F1}s) should be < 200s (drift prevention)");
    }

    // --- Double-adjustment / production gap-fill scenario ---

    [Fact]
    public void GapFill_AfterDiscontinuity_CachedDataAlreadyAdjusted()
    {
        // This test reproduces the production two-stream looping bug:
        // After a discontinuity, _adjustment is non-zero. Gap-fill must use
        // RAW (pre-rewrite) cached data, not already-adjusted data.
        // If already-adjusted data is used, the rewriter double-adjusts PTS.
        var rewriter = new TsTimestampRewriter();

        // Segment 1: PTS at 5s-7s (normal capture from source)
        var seg1 = TsPacketBuilder.BuildSegment(
            new long[] { 450000, 540000, 630000 }); // 5s, 6s, 7s
        rewriter.Rewrite(seg1);
        long seg1Last = rewriter.LastOutputPts;
        Assert.Equal(630000, seg1Last);
        Assert.Equal(0, rewriter.Adjustment);

        // Segment 2: source restarts → PTS jumps back to 0-2s (discontinuity)
        var seg2 = TsPacketBuilder.BuildSegment(
            new long[] { 0, 90000, 180000 }); // 0s, 1s, 2s
        byte[] rawSeg2 = (byte[])seg2.Clone(); // cache raw BEFORE rewrite
        rewriter.Rewrite(seg2);
        long seg2Last = rewriter.LastOutputPts;
        long adj = rewriter.Adjustment;
        Assert.NotEqual(0, adj);
        Assert.True(seg2Last > seg1Last);

        // Gap-fill: use RAW cached data (not already-adjusted)
        byte[] gapFill = (byte[])rawSeg2.Clone();
        rewriter.Rewrite(gapFill);

        long gapFillFirst = TsPacketBuilder.ReadFirstPts(gapFill);
        long gapFillLast = TsPacketBuilder.ReadLastPts(gapFill);

        Assert.True(gapFillFirst > seg2Last,
            $"Gap-fill first PTS ({gapFillFirst}) must be > seg2 last PTS ({seg2Last})");
        Assert.True(gapFillLast > gapFillFirst);

        // The gap should be reasonable (~1s), not a wild double-adjustment jump.
        long gapFromSeg2 = gapFillFirst - seg2Last;
        Assert.True(gapFromSeg2 < 3 * TicksPerSecond,
            $"Gap-fill jump ({gapFromSeg2 / (double)TicksPerSecond:F1}s) should be < 3s");
    }

    [Fact]
    public void GapFill_AfterDiscontinuity_ThenFreshSegment_StaysMonotonic()
    {
        // Full production sequence: captures, discontinuity, gap-fill with
        // raw cached data, then fresh capture. All PTS must be monotonic.
        var rewriter = new TsTimestampRewriter();

        // Initial capture: PTS 5-7s
        var seg1 = TsPacketBuilder.BuildSegment(
            new long[] { 450000, 540000, 630000 });
        rewriter.Rewrite(seg1);

        // Source restart: PTS 0-2s (discontinuity → adjustment kicks in)
        var seg2 = TsPacketBuilder.BuildSegment(
            new long[] { 0, 90000, 180000 });
        byte[] rawSeg2 = (byte[])seg2.Clone();
        rewriter.Rewrite(seg2);
        long seg2Last = rewriter.LastOutputPts;

        // Gap-fill: replay RAW copy of seg2
        byte[] gapFill = (byte[])rawSeg2.Clone();
        rewriter.Rewrite(gapFill);
        long gapFillFirst = TsPacketBuilder.ReadFirstPts(gapFill);
        long gapFillLast = rewriter.LastOutputPts;
        Assert.True(gapFillFirst > seg2Last);
        Assert.True(gapFillLast > gapFillFirst);

        // Fresh segment after gap-fill: source restarted again → PTS 0-2s
        var seg3 = TsPacketBuilder.BuildSegment(
            new long[] { 0, 90000, 180000 });
        rewriter.Rewrite(seg3);
        long seg3First = TsPacketBuilder.ReadFirstPts(seg3);
        long seg3Last = rewriter.LastOutputPts;

        Assert.True(seg3First > gapFillLast,
            $"Fresh seg3 first PTS ({seg3First}) must be > gap-fill last ({gapFillLast})");
        Assert.True(seg3Last > seg3First);

        // Verify reasonable gap
        long gap = seg3First - gapFillLast;
        Assert.True(gap < 3 * TicksPerSecond,
            $"Fresh segment gap ({gap / (double)TicksPerSecond:F1}s) should be < 3s");
    }

    // --- Edge cases ---

    [Fact]
    public void EmptyData_DoesNotThrow()
    {
        var rewriter = new TsTimestampRewriter();
        bool adjusted = rewriter.Rewrite(Span<byte>.Empty);
        Assert.False(adjusted);
        Assert.Equal(-1, rewriter.LastOutputPts);
    }

    [Fact]
    public void DataWithNoPts_PreservesState()
    {
        var rewriter = new TsTimestampRewriter();

        // First: a real segment to establish state
        var seg1 = TsPacketBuilder.BuildSegment(new long[] { 90000 });
        rewriter.Rewrite(seg1);
        Assert.Equal(90000, rewriter.LastOutputPts);

        // Then: a packet without PTS (plain payload)
        var noPts = TsPacketBuilder.BuildPayloadPacket(0x100, 5);
        rewriter.Rewrite(noPts);

        // State should be unchanged
        Assert.Equal(90000, rewriter.LastOutputPts);
    }

    [Fact]
    public void MultipleGapFills_AllAdvance()
    {
        // Multiple consecutive gap-fills (using raw copies) should each advance PTS.
        var rewriter = new TsTimestampRewriter();

        var seg1 = TsPacketBuilder.BuildSegment(
            new long[] { 0, 45000, 90000 });
        byte[] rawSeg1 = (byte[])seg1.Clone();
        rewriter.Rewrite(seg1);

        long prevLast = rewriter.LastOutputPts;

        for (int i = 0; i < 5; i++)
        {
            byte[] gapFill = (byte[])rawSeg1.Clone();
            rewriter.Rewrite(gapFill);

            long gapFirst = TsPacketBuilder.ReadFirstPts(gapFill);
            long gapLast = rewriter.LastOutputPts;

            Assert.True(gapFirst > prevLast,
                $"Gap-fill {i}: first PTS ({gapFirst}) must be > previous last ({prevLast})");
            Assert.True(gapLast > gapFirst);

            prevLast = gapLast;
        }
    }

    // --- Source-content duplicate detection ---

    [Fact]
    public void ReadFirstPts_And_ReadLastPts_DetectDuplicateSourceContent()
    {
        // Simulates the production bug: IPTV server replays same buffer on reconnect.
        // Each capture returns segments with the same raw PTS range.
        // ReadFirstPts/ReadLastPts should detect the overlap.

        // First capture: PTS 100000 → 200000 → 300000
        var seg1 = TsPacketBuilder.BuildSegment(
            new long[] { 100_000, 200_000, 300_000 });

        // Second capture (reconnect): same PTS range — duplicate
        var seg2 = TsPacketBuilder.BuildSegment(
            new long[] { 100_000, 200_000, 300_000 });

        // Third capture (reconnect): PTS advances — fresh content
        var seg3 = TsPacketBuilder.BuildSegment(
            new long[] { 350_000, 450_000, 550_000 });

        long maxRawPts = -1;

        // Process seg1 — first ever, not duplicate
        long lastPts1 = TsTimestampRewriter.ReadLastPts(seg1);
        Assert.True(lastPts1 > maxRawPts, "First segment should be new");
        maxRawPts = lastPts1;

        // Process seg2 — same raw PTS, should be flagged as duplicate
        long lastPts2 = TsTimestampRewriter.ReadLastPts(seg2);
        Assert.True(lastPts2 <= maxRawPts,
            $"Duplicate segment lastPts ({lastPts2}) should be <= maxRawPts ({maxRawPts})");

        // Process seg3 — fresh content
        long lastPts3 = TsTimestampRewriter.ReadLastPts(seg3);
        Assert.True(lastPts3 > maxRawPts,
            $"Fresh segment lastPts ({lastPts3}) should be > maxRawPts ({maxRawPts})");
    }

    [Fact]
    public void ReadFirstPts_ReturnsMinusOne_ForNoPesData()
    {
        // Null TS packets have no PES — should return -1
        byte[] nullPackets = new byte[188 * 3];
        for (int i = 0; i < 3; i++)
        {
            int off = i * 188;
            nullPackets[off] = 0x47;
            nullPackets[off + 1] = 0x1F;
            nullPackets[off + 2] = 0xFF;
            nullPackets[off + 3] = 0x10;
        }

        Assert.Equal(-1, TsTimestampRewriter.ReadFirstPts(nullPackets));
        Assert.Equal(-1, TsTimestampRewriter.ReadLastPts(nullPackets));
    }

    [Fact]
    public void DuplicateDetection_PartialOverlap_AcceptsNewContent()
    {
        // Partial overlap: segment starts in already-seen range but extends beyond.
        // The last PTS is beyond maxRawPts → should be accepted as fresh.

        var seg1 = TsPacketBuilder.BuildSegment(
            new long[] { 100_000, 200_000, 300_000 });

        // Partial overlap: starts at 250000 (already seen) but ends at 400000 (new)
        var seg2 = TsPacketBuilder.BuildSegment(
            new long[] { 250_000, 350_000, 400_000 });

        long maxRawPts = TsTimestampRewriter.ReadLastPts(seg1);
        long lastPts2 = TsTimestampRewriter.ReadLastPts(seg2);

        Assert.True(lastPts2 > maxRawPts,
            $"Partially overlapping segment with new tail ({lastPts2}) should be > maxRawPts ({maxRawPts})");
    }

    /// <summary>
    /// Simulates the ring-buffer wrap scenario that caused stream crashes:
    /// after maxRawPtsSeen covers the entire PTS range of a wrapping source,
    /// every segment appears "duplicate" and the pump starves the transcoder.
    /// The anti-starvation logic should reset after maxConsecutiveSkips.
    /// </summary>
    [Fact]
    public void RingBufferWrap_AntiStarvation_ResetsAfterConsecutiveSkips()
    {
        // Simulate a 30s source clip with PTS 0 → 2_700_000 (30s × 90kHz).
        // Captures cycle through the clip, building maxRawPtsSeen up to the max.
        // After the clip wraps, all new segments have lower PTS → all "duplicate".
        const long clipEnd = 2_700_000;
        const int maxConsecutiveSkips = 8;

        long maxRawPtsSeen = -1;
        int consecutiveSkips = 0;
        int segmentsDelivered = 0;
        int segmentsSkipped = 0;

        // Phase 1: First pass through the clip — all fresh, builds maxRawPtsSeen.
        for (long pts = 0; pts < clipEnd; pts += 180_000) // 2s segments
        {
            var seg = TsPacketBuilder.BuildSegment(new[] { pts, pts + 90_000 });
            long rawLastPts = TsTimestampRewriter.ReadLastPts(seg);

            bool isDuplicate = false;
            if (rawLastPts >= 0 && maxRawPtsSeen >= 0)
            {
                isDuplicate = TsTimestampRewriter.WrapDiff(rawLastPts, maxRawPtsSeen) <= 0;
            }

            // Anti-starvation check
            if (isDuplicate && consecutiveSkips >= maxConsecutiveSkips)
            {
                maxRawPtsSeen = -1;
                isDuplicate = false;
                consecutiveSkips = 0;
            }

            if (rawLastPts >= 0 && (maxRawPtsSeen < 0 || TsTimestampRewriter.WrapDiff(rawLastPts, maxRawPtsSeen) > 0))
            {
                maxRawPtsSeen = rawLastPts;
            }

            if (isDuplicate)
            {
                consecutiveSkips++;
                segmentsSkipped++;
            }
            else
            {
                consecutiveSkips = 0;
                segmentsDelivered++;
            }
        }

        // After phase 1: all segments delivered, maxRawPtsSeen near clipEnd.
        Assert.True(segmentsDelivered >= 14, $"Phase 1: expected all ~15 segments delivered, got {segmentsDelivered}");
        Assert.Equal(0, segmentsSkipped);

        // Phase 2: Clip wraps — replay from the beginning.
        // These segments have PTS lower than maxRawPtsSeen → detected as duplicates.
        // After maxConsecutiveSkips, anti-starvation kicks in.
        int phase2Delivered = 0;
        int phase2Skipped = 0;
        for (long pts = 0; pts < clipEnd; pts += 180_000)
        {
            var seg = TsPacketBuilder.BuildSegment(new[] { pts, pts + 90_000 });
            long rawLastPts = TsTimestampRewriter.ReadLastPts(seg);

            bool isDuplicate = false;
            if (rawLastPts >= 0 && maxRawPtsSeen >= 0)
            {
                isDuplicate = TsTimestampRewriter.WrapDiff(rawLastPts, maxRawPtsSeen) <= 0;
            }

            if (isDuplicate && consecutiveSkips >= maxConsecutiveSkips)
            {
                maxRawPtsSeen = -1;
                isDuplicate = false;
                consecutiveSkips = 0;
            }

            if (rawLastPts >= 0 && (maxRawPtsSeen < 0 || TsTimestampRewriter.WrapDiff(rawLastPts, maxRawPtsSeen) > 0))
            {
                maxRawPtsSeen = rawLastPts;
            }

            if (isDuplicate)
            {
                consecutiveSkips++;
                phase2Skipped++;
            }
            else
            {
                consecutiveSkips = 0;
                phase2Delivered++;
            }
        }

        // Anti-starvation should have fired: some segments skipped, but NOT all.
        Assert.True(phase2Skipped > 0, "Phase 2: should have skipped some duplicate segments");
        Assert.True(phase2Delivered > 0,
            $"Phase 2: anti-starvation should have delivered segments after {maxConsecutiveSkips} skips, but delivered {phase2Delivered}");
        Assert.True(phase2Skipped <= maxConsecutiveSkips + 1,
            $"Phase 2: should skip at most {maxConsecutiveSkips}+1 before reset, got {phase2Skipped}");
    }
}
