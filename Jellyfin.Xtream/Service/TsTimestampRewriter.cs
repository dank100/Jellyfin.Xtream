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

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Rewrites PTS/DTS/PCR timestamps in MPEG-TS packets to ensure monotonic
/// continuity across capture session boundaries. This eliminates the need
/// for a remuxer subprocess while keeping timestamps smooth for downstream
/// transcoders.
/// </summary>
internal sealed class TsTimestampRewriter
{
    private const int TsPacketSize = 188;
    private const byte SyncByte = 0x47;

    // 90 kHz clock: 1 second = 90000 ticks, PTS wraps at 2^33.
    private const long PtsWrap = 1L << 33;
    private const long BackwardThreshold = 90000 / 2; // 0.5s — catches source restarts while tolerating B-frame jitter
    private const long ForwardThreshold = 90000 * 30; // 30s — catches large forward jumps

    // Per-PID continuity counter tracking for seamless segment concatenation.
    private readonly Dictionary<int, int> _ccCounters = new();

    private long _lastPts = -1;
    private long _adjustment;

    /// <summary>
    /// Gets the last observed output PTS (after adjustment), or -1 if none seen yet.
    /// </summary>
    public long LastOutputPts => _lastPts;

    /// <summary>
    /// Gets the current adjustment offset (in 90kHz ticks).
    /// </summary>
    public long Adjustment => _adjustment;

    /// <summary>
    /// Rewrites all PTS/DTS/PCR values in <paramref name="data"/> so that
    /// timestamps are monotonically increasing. If a discontinuity is detected
    /// (first PTS of a new capture session jumps away from the last output PTS),
    /// a new adjustment offset is calculated automatically.
    /// Uses wall-clock time to prevent PTS drift when captures produce more
    /// content than real time (e.g., burst reads from HTTP sources).
    /// </summary>
    /// <param name="data">Raw MPEG-TS data (multiple 188-byte packets).</param>
    /// <returns>True if an adjustment was applied, false if data was passed through unchanged.</returns>
    public bool Rewrite(Span<byte> data)
    {
        long firstPts = ReadFirstPts(data);
        if (firstPts >= 0 && _lastPts >= 0)
        {
            long expectedDelta = WrapDiff(firstPts + _adjustment, _lastPts);
            if (expectedDelta < -BackwardThreshold || expectedDelta > ForwardThreshold)
            {
                // Discontinuity: map new content to _lastPts + 1 so output PTS
                // remains strictly monotonic without large gaps. Wall-clock based
                // targets caused PTS jumps that crashed the HLS transcoder/player.
                long target = _lastPts + 1;
                _adjustment = WrapDiff(target, firstPts);
            }
        }
        else if (firstPts >= 0 && _lastPts < 0)
        {
            // First segment ever — keep original timestamps (adjustment = 0).
            // Only adjust on actual discontinuities.
            _adjustment = 0;
        }

        bool adjusted = false;
        if (_adjustment != 0)
        {
            AdjustAllTimestamps(data, _adjustment);
            adjusted = true;
        }

        // Fix continuity counters across segment boundaries so decoders
        // don't detect packet loss and drop data.
        FixContinuityCounters(data);

        // Track the last PTS we output.
        // Data was already adjusted in-place above, so ReadLastPts returns
        // the final output PTS — do NOT add _adjustment again.
        long lastPts = ReadLastPts(data);
        if (lastPts >= 0)
        {
            _lastPts = Wrap(lastPts);
        }

        return adjusted;
    }

    /// <summary>
    /// Computes the signed difference (a - b) with 33-bit PTS wrap handling.
    /// Positive means a is ahead of b; negative means a is behind.
    /// Exposed for wrap-safe duplicate detection in the pump.
    /// </summary>
    /// <param name="a">First PTS value.</param>
    /// <param name="b">Second PTS value.</param>
    /// <returns>Signed difference accounting for 33-bit PTS wrap.</returns>
    internal static long WrapDiff(long a, long b)
    {
        long diff = Wrap(a) - Wrap(b);
        if (diff > PtsWrap / 2)
        {
            diff -= PtsWrap;
        }

        if (diff < -PtsWrap / 2)
        {
            diff += PtsWrap;
        }

        return diff;
    }

    private static long Wrap(long pts) => ((pts % PtsWrap) + PtsWrap) % PtsWrap;

    /// <summary>
    /// Reads the first PTS found in the raw (pre-rewrite) TS data.
    /// Exposed for source-content duplicate detection.
    /// </summary>
    /// <param name="data">Raw MPEG-TS data (multiple 188-byte packets).</param>
    /// <returns>The first PTS value found, or -1 if no PTS present.</returns>
    internal static long ReadFirstPts(ReadOnlySpan<byte> data)
    {
        for (int offset = 0; offset + TsPacketSize <= data.Length; offset += TsPacketSize)
        {
            long pts = TryReadPesTimestamp(data, offset);
            if (pts >= 0)
            {
                return pts;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads the last PTS found in the raw (pre-rewrite) TS data.
    /// Exposed for source-content duplicate detection.
    /// </summary>
    /// <param name="data">Raw MPEG-TS data (multiple 188-byte packets).</param>
    /// <returns>The last PTS value found, or -1 if no PTS present.</returns>
    internal static long ReadLastPts(ReadOnlySpan<byte> data)
    {
        long last = -1;
        for (int offset = 0; offset + TsPacketSize <= data.Length; offset += TsPacketSize)
        {
            long pts = TryReadPesTimestamp(data, offset);
            if (pts >= 0)
            {
                last = pts;
            }
        }

        return last;
    }

    /// <summary>
    /// Tries to read the PTS from a PES header in one TS packet. Returns -1 if none.
    /// </summary>
    private static long TryReadPesTimestamp(ReadOnlySpan<byte> data, int packetOffset)
    {
        if (data[packetOffset] != SyncByte)
        {
            return -1;
        }

        bool payloadStart = (data[packetOffset + 1] & 0x40) != 0;
        if (!payloadStart)
        {
            return -1;
        }

        int adaptControl = (data[packetOffset + 3] >> 4) & 0x03;
        int payloadOffset = packetOffset + 4;

        if ((adaptControl & 0x02) != 0)
        {
            int adaptLen = data[payloadOffset];
            payloadOffset += 1 + adaptLen;
        }

        if ((adaptControl & 0x01) == 0)
        {
            return -1;
        }

        int end = packetOffset + TsPacketSize;
        if (payloadOffset + 9 > end)
        {
            return -1;
        }

        if (data[payloadOffset] != 0 || data[payloadOffset + 1] != 0 || data[payloadOffset + 2] != 1)
        {
            return -1;
        }

        byte streamId = data[payloadOffset + 3];
        if (streamId < 0xBC)
        {
            return -1;
        }

        // Only streams with optional PES header (video, audio, private).
        if (streamId == 0xBC || streamId == 0xBE || streamId == 0xBF ||
            streamId == 0xF0 || streamId == 0xF1 || streamId == 0xFF ||
            streamId == 0xF2 || streamId == 0xF8)
        {
            return -1;
        }

        byte flags = data[payloadOffset + 7];
        if ((flags & 0x80) == 0)
        {
            return -1;
        }

        int ptsPos = payloadOffset + 9;
        if (ptsPos + 5 > end)
        {
            return -1;
        }

        return DecodePts(data, ptsPos);
    }

    private static void AdjustAllTimestamps(Span<byte> data, long adjustment)
    {
        for (int offset = 0; offset + TsPacketSize <= data.Length; offset += TsPacketSize)
        {
            if (data[offset] != SyncByte)
            {
                continue;
            }

            int adaptControl = (data[offset + 3] >> 4) & 0x03;
            int payloadOffset = offset + 4;

            // Adjust PCR in adaptation field.
            if ((adaptControl & 0x02) != 0)
            {
                int adaptLen = data[payloadOffset];
                if (adaptLen >= 6 && (data[payloadOffset + 1] & 0x10) != 0)
                {
                    AdjustPcr(data, payloadOffset + 2, adjustment);
                }

                payloadOffset += 1 + adaptLen;
            }

            if ((adaptControl & 0x01) == 0)
            {
                continue;
            }

            bool payloadStart = (data[offset + 1] & 0x40) != 0;
            if (!payloadStart)
            {
                continue;
            }

            int end = offset + TsPacketSize;
            if (payloadOffset + 9 > end)
            {
                continue;
            }

            if (data[payloadOffset] != 0 || data[payloadOffset + 1] != 0 || data[payloadOffset + 2] != 1)
            {
                continue;
            }

            byte streamId = data[payloadOffset + 3];
            if (streamId < 0xBC)
            {
                continue;
            }

            if (streamId == 0xBC || streamId == 0xBE || streamId == 0xBF ||
                streamId == 0xF0 || streamId == 0xF1 || streamId == 0xFF ||
                streamId == 0xF2 || streamId == 0xF8)
            {
                continue;
            }

            byte flags = data[payloadOffset + 7];
            bool hasPts = (flags & 0x80) != 0;
            bool hasDts = (flags & 0x40) != 0;

            int tsPos = payloadOffset + 9;

            if (hasPts && tsPos + 5 <= end)
            {
                long pts = DecodePts(data, tsPos);
                EncodePts(data, tsPos, Wrap(pts + adjustment), hasDts ? (byte)0x03 : (byte)0x02);
                tsPos += 5;
            }

            if (hasDts && tsPos + 5 <= end)
            {
                long dts = DecodePts(data, tsPos);
                EncodePts(data, tsPos, Wrap(dts + adjustment), 0x01);
            }
        }
    }

    private static long DecodePts(ReadOnlySpan<byte> data, int i)
    {
        long pts = ((long)(data[i] & 0x0E)) << 29;
        pts |= (long)data[i + 1] << 22;
        pts |= ((long)(data[i + 2] & 0xFE)) << 14;
        pts |= (long)data[i + 3] << 7;
        pts |= ((long)(data[i + 4] & 0xFE)) >> 1;
        return pts;
    }

    private static void EncodePts(Span<byte> data, int i, long pts, byte marker)
    {
        pts = Wrap(pts);
        data[i] = (byte)(((marker & 0x0F) << 4) | (int)((pts >> 29) & 0x0E) | 0x01);
        data[i + 1] = (byte)((pts >> 22) & 0xFF);
        data[i + 2] = (byte)(((pts >> 14) & 0xFE) | 0x01);
        data[i + 3] = (byte)((pts >> 7) & 0xFF);
        data[i + 4] = (byte)(((pts << 1) & 0xFE) | 0x01);
    }

    private static void AdjustPcr(Span<byte> data, int i, long adjustment)
    {
        long pcrBase = ((long)data[i] << 25) | ((long)data[i + 1] << 17) |
                       ((long)data[i + 2] << 9) | ((long)data[i + 3] << 1) |
                       (long)((data[i + 4] >> 7) & 0x01);
        int pcrExt = ((data[i + 4] & 0x01) << 8) | data[i + 5];

        pcrBase = Wrap(pcrBase + adjustment);

        data[i] = (byte)(pcrBase >> 25);
        data[i + 1] = (byte)(pcrBase >> 17);
        data[i + 2] = (byte)(pcrBase >> 9);
        data[i + 3] = (byte)(pcrBase >> 1);
        data[i + 4] = (byte)((((int)(pcrBase & 1)) << 7) | 0x7E | ((pcrExt >> 8) & 0x01));
        data[i + 5] = (byte)(pcrExt & 0xFF);
    }

    /// <summary>
    /// Fixes MPEG-TS continuity counters across segment boundaries.
    /// Each PID has a 4-bit counter (0-15) that must increment by 1 per packet.
    /// When segments are concatenated, the counter resets — this method
    /// remaps them to continue from the previous segment's last value.
    /// </summary>
    private void FixContinuityCounters(Span<byte> data)
    {
        for (int offset = 0; offset + TsPacketSize <= data.Length; offset += TsPacketSize)
        {
            if (data[offset] != SyncByte)
            {
                continue;
            }

            int pid = ((data[offset + 1] & 0x1F) << 8) | data[offset + 2];
            int adaptControl = (data[offset + 3] >> 4) & 0x03;

            // Only packets with payload have meaningful continuity counters.
            if ((adaptControl & 0x01) == 0)
            {
                continue;
            }

            int inputCc = data[offset + 3] & 0x0F;

            if (_ccCounters.TryGetValue(pid, out int lastCc))
            {
                int expectedCc = (lastCc + 1) & 0x0F;
                data[offset + 3] = (byte)((data[offset + 3] & 0xF0) | expectedCc);
                _ccCounters[pid] = expectedCc;
            }
            else
            {
                // First time seeing this PID — keep original CC.
                _ccCounters[pid] = inputCc;
            }
        }
    }
}
