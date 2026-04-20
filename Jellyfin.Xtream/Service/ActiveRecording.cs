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
using System.Threading;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Represents an active (in-progress) recording.
/// </summary>
public sealed class ActiveRecording : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ActiveRecording"/> class.
    /// </summary>
    /// <param name="timer">The timer info for this recording.</param>
    public ActiveRecording(TimerInfo timer)
    {
        Timer = timer;
    }

    /// <summary>
    /// Gets the timer associated with this recording.
    /// </summary>
    public TimerInfo Timer { get; }

    /// <summary>
    /// Gets or sets the output file path.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Gets the cancellation token source for this recording.
    /// </summary>
    public CancellationTokenSource CancellationTokenSource => _cts;

    /// <summary>
    /// Gets the cancellation token for this recording.
    /// </summary>
    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>
    /// Cancels this recording.
    /// </summary>
    public void Cancel() => _cts.Cancel();

    /// <inheritdoc />
    public void Dispose() => _cts.Dispose();
}
