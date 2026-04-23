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
/// A read-only stream that tails a growing file. Blocks when caught up with the writer
/// and returns 0 only when the file is no longer growing (recording finished).
/// Each consumer should get their own instance.
/// </summary>
public class TailingFileStream : Stream
{
    private readonly FileStream _fs;
    private readonly Func<bool> _isStillGrowing;

    /// <summary>
    /// Initializes a new instance of the <see cref="TailingFileStream"/> class.
    /// </summary>
    /// <param name="filePath">Path to the growing file.</param>
    /// <param name="isStillGrowing">A delegate that returns true while the file is still being written to.</param>
    public TailingFileStream(string filePath, Func<bool> isStillGrowing)
    {
#pragma warning disable CA3003
        _fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);
#pragma warning restore CA3003
        _isStillGrowing = isStillGrowing;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

#pragma warning disable CA1065
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => _fs.Position;
        set => throw new NotSupportedException();
    }
#pragma warning restore CA1065

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            int bytesRead = _fs.Read(buffer, offset, count);
            if (bytesRead > 0)
            {
                return bytesRead;
            }

            if (!_isStillGrowing())
            {
                // Recording finished — drain any last bytes then signal EOF
                bytesRead = _fs.Read(buffer, offset, count);
                return bytesRead;
            }

            // File writer hasn't flushed yet — wait briefly
            Thread.Sleep(250);
        }
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Flush()
    {
        // Do nothing
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fs.Dispose();
        }

        base.Dispose(disposing);
    }
}
