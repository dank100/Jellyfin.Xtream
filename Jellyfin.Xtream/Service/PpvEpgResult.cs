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

namespace Jellyfin.Xtream.Service;

/// <summary>
/// The result of a successful PPV EPG time parse.
/// </summary>
/// <param name="Start">The event start time in UTC.</param>
/// <param name="End">The event end time in UTC.</param>
/// <param name="CleanTitle">The channel name with the embedded time token removed.</param>
public sealed record PpvEpgResult(DateTime Start, DateTime End, string CleanTitle);
