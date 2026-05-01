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
using System.Threading;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A read-only stream that concatenates MPEG-TS segment files from a
/// <see cref="ChannelBuffer"/> into a continuous byte stream for Jellyfin's
/// LiveStreamFiles endpoint.
/// </summary>
public sealed class MultiplexedSegmentStream : Stream
{
    private readonly ChannelBuffer _buffer;
    private readonly CancellationToken _cancellationToken;
    private FileStream? _currentFile;
    private int _nextSegmentIndex;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiplexedSegmentStream"/> class.
    /// </summary>
    /// <param name="buffer">The channel buffer to read segments from.</param>
    /// <param name="startIndex">The segment index to start reading from.</param>
    /// <param name="cancellationToken">Token to cancel blocking reads.</param>
    public MultiplexedSegmentStream(ChannelBuffer buffer, int startIndex, CancellationToken cancellationToken)
    {
        _buffer = buffer;
        _nextSegmentIndex = startIndex;
        _cancellationToken = cancellationToken;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            // Try to read from current open file
            if (_currentFile is not null)
            {
                int bytesRead = _currentFile.Read(buffer, offset, count);
                if (bytesRead > 0)
                {
                    return bytesRead;
                }

                // Current segment exhausted
                _currentFile.Dispose();
                _currentFile = null;
                _nextSegmentIndex++;
            }

            // Try to open next segment
            if (!TryOpenNextSegment())
            {
                // No segment available yet — wait for new ones
                Thread.Sleep(200);
            }
        }

        return 0;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _currentFile?.Dispose();
                _currentFile = null;
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }

    private bool TryOpenNextSegment()
    {
        IReadOnlyList<SegmentInfo> segments = _buffer.GetSegments();
        if (_nextSegmentIndex >= segments.Count)
        {
            return false;
        }

        var segment = segments[_nextSegmentIndex];
        string path = Path.Combine(_buffer.SegmentDir, segment.Filename);

        if (!File.Exists(path))
        {
            // Segment was pruned — skip to next available
            _nextSegmentIndex++;
            return _nextSegmentIndex < segments.Count && TryOpenNextSegment();
        }

        _currentFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return true;
    }
}
