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
/// Stream which writes to a self-overwriting internal buffer.
/// </summary>
public class WrappedBufferReadStream : Stream
{
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
            long keyframePos = FindKeyframePosition(_initialReadHead, sourceBuffer);
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

        // We cannot return with 0 bytes read, as that indicates the end of the stream has been reached
        while (gap == 0)
        {
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
    /// Scans the circular buffer for the first MPEG-TS packet with the Random Access Indicator (RAI)
    /// flag set, starting from <paramref name="startPos"/>. TS packets with RAI mark keyframes that
    /// contain SPS/PPS NAL units, ensuring decoders can start cleanly without "non-existing PPS" errors.
    /// </summary>
    /// <param name="startPos">The virtual byte position to start scanning from.</param>
    /// <param name="source">The source buffer to scan.</param>
    /// <returns>The virtual byte position of the first RAI-flagged TS packet, or -1 if not found.</returns>
    private static long FindKeyframePosition(long startPos, WrappedBufferStream source)
    {
        const int tsPacketSize = 188;
        const byte syncByte = 0x47;
        long available = source.TotalBytesWritten - startPos;

        // Need at least 6 bytes to check: sync + 2 header + flags + adaptation_length + adaptation_flags.
        if (available < tsPacketSize)
        {
            return -1;
        }

        // First, align to a TS sync byte by finding 0x47 followed by another 0x47 at +188.
        long pos = startPos;
        long end = source.TotalBytesWritten - tsPacketSize;
        long syncPos = -1;

        while (pos < end)
        {
            int idx = (int)(pos % source.BufferSize);
            if (source.Buffer[idx] == syncByte)
            {
                // Verify sync at +188 to confirm alignment.
                long nextPos = pos + tsPacketSize;
                if (nextPos < source.TotalBytesWritten)
                {
                    int nextIdx = (int)(nextPos % source.BufferSize);
                    if (source.Buffer[nextIdx] == syncByte)
                    {
                        syncPos = pos;
                        break;
                    }
                }
            }

            pos++;
        }

        if (syncPos < 0)
        {
            return -1;
        }

        // Scan TS packets for RAI flag.
        pos = syncPos;
        while (pos + 6 <= source.TotalBytesWritten)
        {
            int bufSize = source.BufferSize;
            int i0 = (int)(pos % bufSize);

            if (source.Buffer[i0] != syncByte)
            {
                break; // Lost sync.
            }

            // Byte 3: adaptation_field_control is bits 5-4.
            int i3 = (int)((pos + 3) % bufSize);
            byte flags3 = source.Buffer[i3];
            bool hasAdaptation = (flags3 & 0x20) != 0;

            if (hasAdaptation)
            {
                // Byte 4: adaptation_field_length.
                int i4 = (int)((pos + 4) % bufSize);
                byte adaptLen = source.Buffer[i4];

                if (adaptLen > 0)
                {
                    // Byte 5: adaptation flags, bit 6 = random_access_indicator.
                    int i5 = (int)((pos + 5) % bufSize);
                    if ((source.Buffer[i5] & 0x40) != 0)
                    {
                        return pos;
                    }
                }
            }

            pos += tsPacketSize;
        }

        return -1;
    }
}
