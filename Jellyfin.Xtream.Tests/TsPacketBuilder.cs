// Helper to build synthetic MPEG-TS packets with known PTS/PCR values for testing.

using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Builds synthetic 188-byte MPEG-TS packets for unit testing.
/// </summary>
internal static class TsPacketBuilder
{
    private const int PacketSize = 188;
    private const byte SyncByte = 0x47;

    /// <summary>
    /// Build a TS segment (multiple packets) that simulates a real capture segment
    /// with video PES packets containing known PTS values.
    /// </summary>
    /// <param name="ptsValues">PTS values (90kHz ticks) for each PES-bearing packet.</param>
    /// <param name="videoPid">The PID for video packets.</param>
    /// <param name="startCc">Starting continuity counter value.</param>
    /// <param name="includePcr">Whether to include PCR in adaptation field.</param>
    /// <returns>Raw TS data.</returns>
    public static byte[] BuildSegment(long[] ptsValues, int videoPid = 0x100, int startCc = 0, bool includePcr = true)
    {
        var packets = new List<byte[]>();
        int cc = startCc;

        for (int i = 0; i < ptsValues.Length; i++)
        {
            bool addPcr = includePcr && i == 0;
            packets.Add(BuildPesPacket(videoPid, ptsValues[i], ptsValues[i], cc & 0x0F, addPcr));
            cc++;
        }

        using var ms = new MemoryStream();
        foreach (var p in packets)
        {
            ms.Write(p, 0, p.Length);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a single TS packet with a PES header containing PTS and optional DTS.
    /// </summary>
    public static byte[] BuildPesPacket(int pid, long pts, long dts, int cc, bool includePcr = false)
    {
        var packet = new byte[PacketSize];
        int pos = 0;

        // Sync byte
        packet[pos++] = SyncByte;

        // PID + payload unit start indicator
        packet[pos++] = (byte)(0x40 | ((pid >> 8) & 0x1F)); // PUSI=1
        packet[pos++] = (byte)(pid & 0xFF);

        if (includePcr)
        {
            // Adaptation field present + payload present, CC
            packet[pos++] = (byte)(0x30 | (cc & 0x0F));

            // Adaptation field
            int adaptLenPos = pos;
            packet[pos++] = 7; // adaptation field length (1 flag + 6 PCR)
            packet[pos++] = 0x10; // PCR flag set

            // PCR (6 bytes) — use PTS as PCR base
            EncodePcr(packet, pos, pts);
            pos += 6;
        }
        else
        {
            // Payload only, CC
            packet[pos++] = (byte)(0x10 | (cc & 0x0F));
        }

        // PES header
        packet[pos++] = 0x00; // start code prefix
        packet[pos++] = 0x00;
        packet[pos++] = 0x01;
        packet[pos++] = 0xE0; // stream ID: video

        // PES packet length (0 = unbounded for video)
        packet[pos++] = 0x00;
        packet[pos++] = 0x00;

        // PES header flags: PTS + DTS present
        packet[pos++] = 0x80; // marker bits
        packet[pos++] = 0xC0; // PTS + DTS flags
        packet[pos++] = 10;   // PES header data length

        // PTS (5 bytes)
        EncodePts(packet, pos, pts, 0x03);
        pos += 5;

        // DTS (5 bytes)
        EncodePts(packet, pos, dts, 0x01);
        pos += 5;

        // Fill rest with 0xFF (padding)
        for (int i = pos; i < PacketSize; i++)
        {
            packet[i] = 0xFF;
        }

        return packet;
    }

    /// <summary>
    /// Builds a plain TS packet without PES header (e.g., for padding/filler).
    /// </summary>
    public static byte[] BuildPayloadPacket(int pid, int cc, byte fillByte = 0xFF)
    {
        var packet = new byte[PacketSize];
        packet[0] = SyncByte;
        packet[1] = (byte)((pid >> 8) & 0x1F);
        packet[2] = (byte)(pid & 0xFF);
        packet[3] = (byte)(0x10 | (cc & 0x0F)); // payload only
        for (int i = 4; i < PacketSize; i++)
        {
            packet[i] = fillByte;
        }

        return packet;
    }

    /// <summary>
    /// Reads the first PTS value from TS data. Returns -1 if none found.
    /// </summary>
    public static long ReadFirstPts(ReadOnlySpan<byte> data)
    {
        for (int offset = 0; offset + PacketSize <= data.Length; offset += PacketSize)
        {
            long pts = TryReadPts(data, offset);
            if (pts >= 0)
            {
                return pts;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads the last PTS value from TS data. Returns -1 if none found.
    /// </summary>
    public static long ReadLastPts(ReadOnlySpan<byte> data)
    {
        long last = -1;
        for (int offset = 0; offset + PacketSize <= data.Length; offset += PacketSize)
        {
            long pts = TryReadPts(data, offset);
            if (pts >= 0)
            {
                last = pts;
            }
        }

        return last;
    }

    /// <summary>
    /// Reads all PTS values from TS data.
    /// </summary>
    public static long[] ReadAllPts(ReadOnlySpan<byte> data)
    {
        var result = new List<long>();
        for (int offset = 0; offset + PacketSize <= data.Length; offset += PacketSize)
        {
            long pts = TryReadPts(data, offset);
            if (pts >= 0)
            {
                result.Add(pts);
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Reads continuity counters for all packets, keyed by PID.
    /// </summary>
    public static List<(int Pid, int Cc)> ReadContinuityCounters(ReadOnlySpan<byte> data)
    {
        var result = new List<(int, int)>();
        for (int offset = 0; offset + PacketSize <= data.Length; offset += PacketSize)
        {
            if (data[offset] != SyncByte)
            {
                continue;
            }

            int pid = ((data[offset + 1] & 0x1F) << 8) | data[offset + 2];
            int cc = data[offset + 3] & 0x0F;
            result.Add((pid, cc));
        }

        return result;
    }

    private static long TryReadPts(ReadOnlySpan<byte> data, int offset)
    {
        if (data[offset] != SyncByte)
        {
            return -1;
        }

        if ((data[offset + 1] & 0x40) == 0)
        {
            return -1; // no PUSI
        }

        int adaptControl = (data[offset + 3] >> 4) & 0x03;
        int payloadOff = offset + 4;

        if ((adaptControl & 0x02) != 0)
        {
            int adaptLen = data[payloadOff];
            payloadOff += 1 + adaptLen;
        }

        if ((adaptControl & 0x01) == 0)
        {
            return -1;
        }

        int end = offset + PacketSize;
        if (payloadOff + 14 > end)
        {
            return -1;
        }

        if (data[payloadOff] != 0 || data[payloadOff + 1] != 0 || data[payloadOff + 2] != 1)
        {
            return -1;
        }

        byte flags = data[payloadOff + 7];
        if ((flags & 0x80) == 0)
        {
            return -1;
        }

        return DecodePts(data, payloadOff + 9);
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

    private static void EncodePts(byte[] data, int i, long pts, byte marker)
    {
        pts = ((pts % (1L << 33)) + (1L << 33)) % (1L << 33);
        data[i] = (byte)(((marker & 0x0F) << 4) | (int)((pts >> 29) & 0x0E) | 0x01);
        data[i + 1] = (byte)((pts >> 22) & 0xFF);
        data[i + 2] = (byte)(((pts >> 14) & 0xFE) | 0x01);
        data[i + 3] = (byte)((pts >> 7) & 0xFF);
        data[i + 4] = (byte)(((pts << 1) & 0xFE) | 0x01);
    }

    private static void EncodePcr(byte[] data, int i, long pcrBase)
    {
        pcrBase = ((pcrBase % (1L << 33)) + (1L << 33)) % (1L << 33);
        data[i] = (byte)(pcrBase >> 25);
        data[i + 1] = (byte)(pcrBase >> 17);
        data[i + 2] = (byte)(pcrBase >> 9);
        data[i + 3] = (byte)(pcrBase >> 1);
        data[i + 4] = (byte)(((int)(pcrBase & 1) << 7) | 0x7E);
        data[i + 5] = 0x00;
    }
}
