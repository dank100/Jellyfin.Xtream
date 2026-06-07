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
using System.IO;
using System.Threading;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Stream which reads from a <see cref="WrappedBufferStream"/>, seeking to a clean
/// MPEG-TS keyframe boundary to ensure proper A/V sync on playback start.
/// </summary>
public class WrappedBufferReadStream : Stream
{
    private const int TsPacketSize = 188;
    private const byte SyncByte = 0x47;

    private readonly WrappedBufferStream _sourceBuffer;

    private readonly long _initialReadHead;

    /// <summary>
    /// Initializes a new instance of the <see cref="WrappedBufferReadStream"/> class.
    /// </summary>
    /// <param name="sourceBuffer">The source buffer to read from.</param>
    /// <param name="seekKeyframe">Whether to seek forward to the first MPEG-TS keyframe (RAI flag).</param>
    public WrappedBufferReadStream(WrappedBufferStream sourceBuffer, bool seekKeyframe = false)
    {
        _sourceBuffer = sourceBuffer;
        _initialReadHead = Math.Max(0, sourceBuffer.TotalBytesWritten - (sourceBuffer.BufferSize / 2));

        if (seekKeyframe)
        {
            long keyframePos = FindCleanStartPosition(_initialReadHead, sourceBuffer);
            ReadHead = keyframePos >= 0 ? keyframePos : _initialReadHead;
        }
        else
        {
            ReadHead = _initialReadHead;
        }
    }

    /// <summary>
    /// Gets the virtual position in the source buffer.
    /// </summary>
    public long ReadHead { get; private set; }

    /// <summary>
    /// Gets the number of bytes that have been written to this stream.
    /// </summary>
    public long TotalBytesRead { get => ReadHead - _initialReadHead; }

    /// <inheritdoc />
    public override long Position
    {
        get => ReadHead % _sourceBuffer.BufferSize; set { }
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

#pragma warning disable CA1065
    /// <inheritdoc />
    public override long Length { get => throw new NotImplementedException(); }
#pragma warning restore CA1065

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        long gap = _sourceBuffer.TotalBytesWritten - ReadHead;

        // Wait for new data, but return EOF if the source has completed
        while (gap == 0)
        {
            if (_sourceBuffer.IsCompleted)
            {
                return 0; // EOF — upstream closed and won't reconnect
            }

            Thread.Sleep(1);
            gap = _sourceBuffer.TotalBytesWritten - ReadHead;
        }

        if (gap > _sourceBuffer.BufferSize)
        {
            // TODO: design good handling method.
            // Options:
            // - throw exception
            // - skip to buffer.Position+1 to only read 'up-to-date' bytes.
            throw new IOException("Reader cannot keep up");
        }

        // The number of bytes that can be copied.
        long canCopy = Math.Min(count, gap);
        long read = 0;

        // Copy inside a loop to simplify wrapping logic.
        while (read < canCopy)
        {
            // The amount of bytes that we can directly write from the current position without wrapping.
            long readable = Math.Min(canCopy - read, _sourceBuffer.BufferSize - Position);

            // Copy the data.
            Array.Copy(_sourceBuffer.Buffer, Position, buffer, offset + read, readable);
            read += readable;
            ReadHead += readable;
        }

        return (int)read;
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // Do nothing
    }

    /// <summary>
    /// Reads a byte from the circular buffer at the given virtual position.
    /// </summary>
    private static byte ReadByte(WrappedBufferStream source, long pos)
    {
        return source.Buffer[(int)(pos % source.BufferSize)];
    }

    /// <summary>
    /// Reads a 16-bit unsigned integer (big-endian) from the circular buffer.
    /// </summary>
    private static ushort ReadUInt16BE(WrappedBufferStream source, long pos)
    {
        return (ushort)((ReadByte(source, pos) << 8) | ReadByte(source, pos + 1));
    }

    /// <summary>
    /// Extracts the 13-bit PID from TS header bytes 1-2.
    /// </summary>
    private static int GetPid(WrappedBufferStream source, long packetPos)
    {
        return ReadUInt16BE(source, packetPos + 1) & 0x1FFF;
    }

    /// <summary>
    /// Gets the payload start offset within a TS packet, accounting for the
    /// adaptation field. Returns -1 if the packet has no payload.
    /// </summary>
    private static int GetPayloadOffset(WrappedBufferStream source, long packetPos)
    {
        byte flags = ReadByte(source, packetPos + 3);
        int adaptationFieldControl = (flags >> 4) & 0x03;

        // 0b01 = payload only, 0b11 = adaptation + payload
        if (adaptationFieldControl == 0b01)
        {
            return 4;
        }

        if (adaptationFieldControl == 0b11)
        {
            int adaptationLength = ReadByte(source, packetPos + 4);
            return 5 + adaptationLength;
        }

        // 0b10 = adaptation only (no payload), 0b00 = reserved
        return -1;
    }

    /// <summary>
    /// Checks whether the Random Access Indicator (RAI) flag is set in the
    /// adaptation field of the TS packet at the given position.
    /// </summary>
    private static bool HasRandomAccessIndicator(WrappedBufferStream source, long packetPos)
    {
        byte flags = ReadByte(source, packetPos + 3);
        int adaptationFieldControl = (flags >> 4) & 0x03;

        // Adaptation field must be present (0b10 or 0b11)
        if (adaptationFieldControl < 0b10)
        {
            return false;
        }

        int adaptationLength = ReadByte(source, packetPos + 4);
        if (adaptationLength < 1)
        {
            return false;
        }

        byte adaptationFlags = ReadByte(source, packetPos + 5);
        return (adaptationFlags & 0x40) != 0; // Bit 6 = random_access_indicator
    }

    /// <summary>
    /// Checks whether the Payload Unit Start Indicator (PUSI) is set.
    /// </summary>
    private static bool HasPayloadUnitStart(WrappedBufferStream source, long packetPos)
    {
        return (ReadByte(source, packetPos + 1) & 0x40) != 0;
    }

    /// <summary>
    /// Finds the video PID by parsing PAT and PMT from the buffer.
    /// Returns -1 if not found. Sets <paramref name="codecType"/> to the PMT stream type
    /// (0x1B for H.264, 0x24 for HEVC, -1 if unknown).
    /// </summary>
    private static int FindVideoPid(long startPos, long endPos, WrappedBufferStream source, out int codecType)
    {
        int pmtPid = -1;
        int videoPid = -1;
        codecType = -1;

        long pos = startPos;
        while (pos + TsPacketSize <= endPos)
        {
            if (ReadByte(source, pos) != SyncByte)
            {
                pos++;
                continue;
            }

            int pid = GetPid(source, pos);

            // PAT is always PID 0
            if (pid == 0 && HasPayloadUnitStart(source, pos))
            {
                int payloadOff = GetPayloadOffset(source, pos);
                if (payloadOff >= 0 && payloadOff < TsPacketSize - 12)
                {
                    // Skip pointer field in PAT
                    int pointer = ReadByte(source, pos + payloadOff);
                    int tableStart = payloadOff + 1 + pointer;

                    // PAT table: table_id(1) + flags(2) + transport_stream_id(2) + version(1) + section(1) + last_section(1)
                    // Then 4 bytes per program: program_number(2) + PMT_PID(2)
                    if (tableStart + 8 < TsPacketSize)
                    {
                        int sectionLength = ReadUInt16BE(source, pos + tableStart + 1) & 0x0FFF;
                        int programStart = tableStart + 8;
                        int programEnd = tableStart + 3 + sectionLength - 4; // Exclude CRC

                        for (int i = programStart; i + 3 < TsPacketSize && i + 3 <= programEnd; i += 4)
                        {
                            int programNumber = ReadUInt16BE(source, pos + i);
                            int programPid = ReadUInt16BE(source, pos + i + 2) & 0x1FFF;
                            if (programNumber != 0) // Skip NIT
                            {
                                pmtPid = programPid;
                                break;
                            }
                        }
                    }
                }
            }

            // PMT
            if (pmtPid >= 0 && pid == pmtPid && HasPayloadUnitStart(source, pos))
            {
                int payloadOff = GetPayloadOffset(source, pos);
                if (payloadOff >= 0 && payloadOff < TsPacketSize - 16)
                {
                    int pointer = ReadByte(source, pos + payloadOff);
                    int tableStart = payloadOff + 1 + pointer;

                    // PMT: table_id(1) + section_length(2) + program_number(2) + version(1) + section(1) + last_section(1) + PCR_PID(2) + program_info_length(2)
                    if (tableStart + 12 < TsPacketSize)
                    {
                        int sectionLength = ReadUInt16BE(source, pos + tableStart + 1) & 0x0FFF;
                        int programInfoLength = ReadUInt16BE(source, pos + tableStart + 10) & 0x0FFF;
                        int streamStart = tableStart + 12 + programInfoLength;
                        int streamEnd = tableStart + 3 + sectionLength - 4; // Exclude CRC

                        for (int i = streamStart; i + 4 < TsPacketSize && i + 4 <= streamEnd; i++)
                        {
                            int streamType = ReadByte(source, pos + i);
                            int elementaryPid = ReadUInt16BE(source, pos + i + 1) & 0x1FFF;
                            int esInfoLength = ReadUInt16BE(source, pos + i + 3) & 0x0FFF;

                            // H.264 = 0x1B, H.265 = 0x24
                            if (streamType == 0x1B || streamType == 0x24)
                            {
                                videoPid = elementaryPid;
                                codecType = streamType;
                                return videoPid;
                            }

                            i += 4 + esInfoLength; // Skip to next stream entry
                        }
                    }
                }
            }

            pos += TsPacketSize;
        }

        return videoPid;
    }

    /// <summary>
    /// Finds a clean MPEG-TS start position for playback by:
    /// 1. Parsing PAT/PMT to identify the video PID and codec type
    /// 2. Scanning for a PUSI packet whose access unit contains a true IDR/IRAP frame
    /// 3. Falling back to RAI if IDR scan yields nothing (unknown codecs)
    /// 4. Backing up to include the most recent PAT packet before the keyframe.
    /// </summary>
    private static long FindCleanStartPosition(long startPos, WrappedBufferStream source)
    {
        long snapshot = source.TotalBytesWritten;
        long available = snapshot - startPos;

        if (available < TsPacketSize * 4)
        {
            return -1;
        }

        // Align to TS packet boundary
        long alignedStart = AlignToSync(startPos, snapshot, source);
        if (alignedStart < 0)
        {
            return -1;
        }

        // Find video PID and codec type from PAT/PMT
        int videoPid = FindVideoPid(alignedStart, snapshot, source, out int codecType);

        // Primary: scan PUSI packets on the video PID for a true IDR/IRAP access unit
        long keyframePos = FindIdrPosition(alignedStart, snapshot, source, videoPid, codecType);

        if (keyframePos < 0)
        {
            // Fallback: trust the RAI flag (less reliable but handles unknown codecs)
            keyframePos = FindRaiPosition(alignedStart, snapshot, source, videoPid);
        }

        if (keyframePos < 0)
        {
            return -1;
        }

        // Back up to include the most recent PAT (PID 0) before the keyframe
        long patPos = FindLastPatBefore(alignedStart, keyframePos, source);
        return patPos >= 0 ? patPos : keyframePos;
    }

    /// <summary>
    /// Aligns to a TS packet boundary by finding two consecutive sync bytes 188 bytes apart.
    /// </summary>
    private static long AlignToSync(long startPos, long endPos, WrappedBufferStream source)
    {
        long limit = endPos - (TsPacketSize * 2);
        for (long pos = startPos; pos < limit; pos++)
        {
            if (ReadByte(source, pos) == SyncByte &&
                ReadByte(source, pos + TsPacketSize) == SyncByte)
            {
                return pos;
            }
        }

        return -1;
    }

    /// <summary>
    /// Scans for the first PUSI packet on the video PID whose access unit contains a
    /// true IDR (H.264 NAL type 5) or IRAP (HEVC NAL types 19-21) frame.
    /// This is more reliable than the RAI flag, which some encoders set on non-IDR I-frames.
    /// </summary>
    private static long FindIdrPosition(long startPos, long endPos, WrappedBufferStream source, int videoPid, int codecType)
    {
        long pos = startPos;
        while (pos + TsPacketSize <= endPos)
        {
            if (ReadByte(source, pos) != SyncByte)
            {
                break;
            }

            int pid = GetPid(source, pos);
            if ((videoPid < 0 || pid == videoPid) && HasPayloadUnitStart(source, pos))
            {
                if (AccessUnitHasIdr(source, pos, endPos, videoPid, codecType))
                {
                    return pos;
                }
            }

            pos += TsPacketSize;
        }

        return -1;
    }

    /// <summary>
    /// Scans the access unit starting at <paramref name="puisPos"/> for an IDR/IRAP NAL unit.
    /// Reads from the first PUSI packet through all continuation packets until the next PUSI
    /// on the same PID, up to a safety limit of 32 packets.
    /// Parses and skips the PES header on the first packet to avoid false positives.
    /// </summary>
    private static bool AccessUnitHasIdr(WrappedBufferStream source, long puisPos, long endPos, int videoPid, int codecType)
    {
        long firstNalStart = GetNalStartInPuisPacket(source, puisPos);

        long pos = puisPos;
        const int maxPackets = 32;

        for (int p = 0; p < maxPackets && pos + TsPacketSize <= endPos; p++)
        {
            if (ReadByte(source, pos) != SyncByte)
            {
                break;
            }

            int pid = GetPid(source, pos);

            // Stop at the next access unit on the video PID
            if (p > 0 && (videoPid < 0 ? pid == GetPid(source, puisPos) : pid == videoPid) && HasPayloadUnitStart(source, pos))
            {
                break;
            }

            if (videoPid >= 0 && pid != videoPid)
            {
                pos += TsPacketSize;
                continue;
            }

            long scanFrom;
            if (p == 0)
            {
                // Use the post-PES-header offset if valid, otherwise fall back to TS payload start
                if (firstNalStart >= puisPos + 4 && firstNalStart < puisPos + TsPacketSize)
                {
                    scanFrom = firstNalStart;
                }
                else
                {
                    int fb = GetPayloadOffset(source, pos);
                    scanFrom = pos + (fb >= 0 ? fb : 4);
                }
            }
            else
            {
                int payloadOff = GetPayloadOffset(source, pos);
                scanFrom = pos + (payloadOff >= 0 ? payloadOff : 4);
            }

            long pktEnd = pos + TsPacketSize;
            if (scanFrom >= pktEnd)
            {
                pos += TsPacketSize;
                continue;
            }

            for (long j = scanFrom; j + 3 < pktEnd; j++)
            {
                if (ReadByte(source, j) == 0x00 &&
                    ReadByte(source, j + 1) == 0x00 &&
                    ReadByte(source, j + 2) == 0x01 &&
                    IsIdrNal(ReadByte(source, j + 3), codecType))
                {
                    return true;
                }
            }

            pos += TsPacketSize;
        }

        return false;
    }

    /// <summary>
    /// Parses the PES header in the given PUSI packet and returns the virtual position
    /// where the NAL unit bitstream begins. Returns -1 if the PES header is invalid or
    /// overflows the packet boundary.
    /// </summary>
    private static long GetNalStartInPuisPacket(WrappedBufferStream source, long puisPos)
    {
        int payloadOff = GetPayloadOffset(source, puisPos);
        if (payloadOff < 0)
        {
            return -1;
        }

        long pesStart = puisPos + payloadOff;
        long pktEnd = puisPos + TsPacketSize;

        // Minimum PES header is 9 bytes: start_code(3) + stream_id(1) + length(2) + flags(2) + hdr_len(1)
        if (pesStart + 9 > pktEnd)
        {
            return -1;
        }

        // Validate PES start code: 00 00 01
        if (ReadByte(source, pesStart) != 0x00 ||
            ReadByte(source, pesStart + 1) != 0x00 ||
            ReadByte(source, pesStart + 2) != 0x01)
        {
            return -1;
        }

        // Validate stream_id for video (0xE0-0xEF)
        byte streamId = ReadByte(source, pesStart + 3);
        if ((streamId & 0xF0) != 0xE0)
        {
            return -1;
        }

        int pesHeaderDataLen = ReadByte(source, pesStart + 8);
        long nalStart = pesStart + 9 + pesHeaderDataLen;

        return nalStart < pktEnd ? nalStart : -1;
    }

    /// <summary>
    /// Returns true if the NAL unit identified by its first byte is an IDR (H.264) or
    /// IRAP (HEVC) frame that can serve as a clean random-access point.
    /// </summary>
    private static bool IsIdrNal(byte nalByte, int codecType)
    {
        if (codecType == 0x1B) // H.264
        {
            return (nalByte & 0x1F) == 5; // IDR slice (any nal_ref_idc)
        }

        if (codecType == 0x24) // HEVC: 2-byte NAL header, type in bits 9-15
        {
            int hevcType = (nalByte >> 1) & 0x3F;
            // 19=IDR_W_RADL, 20=IDR_N_LP, 21=CRA_NUT
            return hevcType >= 19 && hevcType <= 21;
        }

        // Unknown codec: fall back to H.264 heuristic
        return (nalByte & 0x1F) == 5;
    }

    /// <summary>
    /// Fallback: scans forward for a TS packet with the Random Access Indicator set on
    /// the target PID. Used for unknown codecs when IDR NAL scanning cannot be applied.
    /// If targetPid is -1, matches RAI on any non-PSI PID that also has PUSI set.
    /// </summary>
    private static long FindRaiPosition(long startPos, long endPos, WrappedBufferStream source, int targetPid)
    {
        long pos = startPos;
        while (pos + TsPacketSize <= endPos)
        {
            if (ReadByte(source, pos) != SyncByte)
            {
                break;
            }

            if (HasRandomAccessIndicator(source, pos))
            {
                int pid = GetPid(source, pos);
                if (targetPid >= 0)
                {
                    if (pid == targetPid)
                    {
                        return pos;
                    }
                }
                else if (HasPayloadUnitStart(source, pos) && pid > 0x1F)
                {
                    // Heuristic: non-PSI PID with PUSI + RAI is likely video
                    return pos;
                }
            }

            pos += TsPacketSize;
        }

        return -1;
    }

    /// <summary>
    /// Scans backward from the target position to find the most recent PAT packet (PID 0).
    /// </summary>
    private static long FindLastPatBefore(long startPos, long beforePos, WrappedBufferStream source)
    {
        // Scan backward in TS packet increments
        long pos = beforePos - TsPacketSize;
        while (pos >= startPos)
        {
            if (ReadByte(source, pos) == SyncByte && GetPid(source, pos) == 0)
            {
                return pos;
            }

            pos -= TsPacketSize;
        }

        return -1;
    }
}
