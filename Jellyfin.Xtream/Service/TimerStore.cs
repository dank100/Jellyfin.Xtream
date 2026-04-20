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
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Persists timer schedules to a JSON file.
/// </summary>
public class TimerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ILogger<TimerStore> _logger;
    private readonly string _dataPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="TimerStore"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{TimerStore}"/> interface.</param>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    public TimerStore(ILogger<TimerStore> logger, IApplicationPaths appPaths)
    {
        _logger = logger;
        _dataPath = Path.Combine(appPaths.PluginConfigurationsPath, "Jellyfin.Xtream");
        Directory.CreateDirectory(_dataPath);
    }

    private string TimersPath => Path.Combine(_dataPath, "timers.json");

    private string SeriesTimersPath => Path.Combine(_dataPath, "series_timers.json");

    /// <summary>
    /// Loads timers from disk.
    /// </summary>
    /// <returns>Dictionary of timer ID to TimerInfo.</returns>
    public Dictionary<string, TimerInfo> LoadTimers()
    {
        try
        {
            if (File.Exists(TimersPath))
            {
                string json = File.ReadAllText(TimersPath);
                var timers = JsonSerializer.Deserialize<List<TimerInfo>>(json, JsonOptions);
                if (timers != null)
                {
                    _logger.LogInformation("Loaded {Count} timer(s) from disk", timers.Count);
                    return timers.Where(t => t.Id != null).ToDictionary(t => t.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading timers from {Path}", TimersPath);
        }

        return new Dictionary<string, TimerInfo>();
    }

    /// <summary>
    /// Saves timers to disk.
    /// </summary>
    /// <param name="timers">The timers to save.</param>
    public void SaveTimers(IEnumerable<TimerInfo> timers)
    {
        try
        {
            string json = JsonSerializer.Serialize(timers.ToList(), JsonOptions);
            File.WriteAllText(TimersPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving timers to {Path}", TimersPath);
        }
    }

    /// <summary>
    /// Loads series timers from disk.
    /// </summary>
    /// <returns>Dictionary of series timer ID to SeriesTimerInfo.</returns>
    public Dictionary<string, SeriesTimerInfo> LoadSeriesTimers()
    {
        try
        {
            if (File.Exists(SeriesTimersPath))
            {
                string json = File.ReadAllText(SeriesTimersPath);
                var timers = JsonSerializer.Deserialize<List<SeriesTimerInfo>>(json, JsonOptions);
                if (timers != null)
                {
                    _logger.LogInformation("Loaded {Count} series timer(s) from disk", timers.Count);
                    return timers.Where(t => t.Id != null).ToDictionary(t => t.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading series timers from {Path}", SeriesTimersPath);
        }

        return new Dictionary<string, SeriesTimerInfo>();
    }

    /// <summary>
    /// Saves series timers to disk.
    /// </summary>
    /// <param name="seriesTimers">The series timers to save.</param>
    public void SaveSeriesTimers(IEnumerable<SeriesTimerInfo> seriesTimers)
    {
        try
        {
            string json = JsonSerializer.Serialize(seriesTimers.ToList(), JsonOptions);
            File.WriteAllText(SeriesTimersPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving series timers to {Path}", SeriesTimersPath);
        }
    }
}
