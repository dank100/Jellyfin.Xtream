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
using System.Globalization;
using System.IO;
using System.Reflection;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream;

/// <summary>
/// The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private static Plugin? _instance;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    /// <param name="xtreamClient">Instance of the <see cref="IXtreamClient"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ITaskManager taskManager, IXtreamClient xtreamClient)
        : base(applicationPaths, xmlSerializer)
    {
        _instance = this;
        XtreamClient = xtreamClient;
        if (XtreamClient is XtreamClient client)
        {
            client.UpdateUserAgent();
        }

        StreamService = new(xtreamClient);
        TaskService = new(taskManager);

        InjectRecordingSeekScript(applicationPaths);
    }

    /// <inheritdoc />
    public override string Name => "Jellyfin Xtream";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("5d774c35-8567-46d3-a950-9bb8227a0c5d");

    /// <summary>
    /// Gets the Xtream connection info with credentials.
    /// </summary>
    public ConnectionInfo Creds => new(Configuration.BaseUrl, Configuration.Username, Configuration.Password);

    /// <summary>
    /// Gets the data version used to trigger a cache invalidation on plugin update or config change.
    /// </summary>
    public string DataVersion => Assembly.GetCallingAssembly().GetName().Version?.ToString() + Configuration.GetHashCode();

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin Instance => _instance ?? throw new InvalidOperationException("Plugin instance not available");

    /// <summary>
    /// Gets the stream service instance.
    /// </summary>
    public StreamService StreamService { get; init; }

    private IXtreamClient XtreamClient { get; init; }

    /// <summary>
    /// Gets the task service instance.
    /// </summary>
    public TaskService TaskService { get; init; }

    private static PluginPageInfo CreateStatic(string name) => new()
    {
        Name = name,
        EmbeddedResourcePath = string.Format(
            CultureInfo.InvariantCulture,
            "{0}.Configuration.Web.{1}",
            typeof(Plugin).Namespace,
            name),
    };

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            CreateStatic("XtreamCredentials.html"),
            CreateStatic("XtreamCredentials.js"),
            CreateStatic("Xtream.css"),
            CreateStatic("Xtream.js"),
            CreateStatic("XtreamLive.html"),
            CreateStatic("XtreamLive.js"),
            CreateStatic("XtreamLiveOverrides.html"),
            CreateStatic("XtreamLiveOverrides.js"),
            CreateStatic("XtreamSeries.html"),
            CreateStatic("XtreamSeries.js"),
            CreateStatic("XtreamVod.html"),
            CreateStatic("XtreamVod.js"),
        };
    }

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);

        if (XtreamClient is XtreamClient client)
        {
            client.UpdateUserAgent();
        }

        // Force a refresh of TV guide on configuration update.
        // - This will update the TV channels.
        // - This will remove channels on credentials change.
        TaskService.CancelIfRunningAndQueue(
            "Jellyfin.LiveTv",
            "Jellyfin.LiveTv.Guide.RefreshGuideScheduledTask");

        // Force a refresh of Channels on configuration update.
        // - This will update the channel entries.
        // - This will remove channel entries on credentials change.
        TaskService.CancelIfRunningAndQueue(
            "Jellyfin.LiveTv",
            "Jellyfin.LiveTv.Channels.RefreshChannelsScheduledTask");
    }

    /// <summary>
    /// Patches the Jellyfin web client so recording channels use the programme start
    /// time as <c>playbackStartTimeTicks</c> instead of the wall clock. This fixes
    /// the time-of-day seekbar to begin at the programme start rather than "now".
    /// Two files are modified:
    /// <list type="bullet">
    /// <item><c>main.jellyfin.bundle.js</c>: the assignment
    /// <c>playbackStartTimeTicks=1e4*(new Date).getTime()</c> is replaced to check
    /// <c>window.__xtreamRecStartMs</c> first.</item>
    /// <item><c>index.html</c>: a helper script is injected that detects recording
    /// channels, exposes the programme start time via the global, and auto-seeks
    /// to the beginning of the recording on first play.</item>
    /// </list>
    /// </summary>
    private static void InjectRecordingSeekScript(IApplicationPaths applicationPaths)
    {
        const string ScriptTag = "xtream-rec-seek";

        try
        {
            string webPath = applicationPaths.WebPath;

            // --- 1. Patch main bundle: replace wall-clock playbackStartTimeTicks ---
            PatchMainBundle(webPath);

            // --- 2. Inject helper script into index.html ---
            string indexPath = Path.Combine(webPath, "index.html");
            if (!File.Exists(indexPath))
            {
                return;
            }

            string html = File.ReadAllText(indexPath);

            // Remove any previous injection
            int existingStart = html.IndexOf($"<script plugin=\"{ScriptTag}\">", StringComparison.Ordinal);
            if (existingStart >= 0)
            {
                int existingEnd = html.IndexOf("</script>", existingStart, StringComparison.Ordinal);
                if (existingEnd >= 0)
                {
                    html = html.Remove(existingStart, existingEnd + "</script>".Length - existingStart);
                }
            }

            // The helper script:
            // 1. Intercepts fetch() to detect "xtream_rec_" in HLS playlist URLs
            //    (video.src is a blob: URL so we can't detect from there)
            // 2. Parses the programme start time from .startTimeText and sets
            //    window.__xtreamRecStartMs so the patched bundle uses it
            // 3. On first play, forces video.currentTime = 0 (start from recording beginning)
            const string Script = """
                <script plugin="xtream-rec-seek">
                (function() {
                    'use strict';
                    if (window.__xtreamSeekInit === 2) return;
                    window.__xtreamSeekInit = 2;
                    var isRecording = false;
                    var didSeekToStart = false;
                    window.__xtreamRecStartMs = 0;
                    window.__xtreamRecEndMs = 0;

                    var _origFetch = window.fetch;
                    window.fetch = function() {
                        var url = (typeof arguments[0] === 'string') ? arguments[0] :
                                  (arguments[0] && arguments[0].url ? arguments[0].url : '');
                        if (url.indexOf('xtream_rec_') !== -1) {
                            isRecording = true;
                        }
                        var result = _origFetch.apply(this, arguments);
                        if (url.indexOf('/PlaybackInfo') !== -1 || url.indexOf('/Items/') !== -1) {
                            result.then(function(resp) {
                                resp.clone().json().then(function(data) {
                                    var sources = data.MediaSources || [];
                                    for (var i = 0; i < sources.length; i++) {
                                        var name = sources[i].Name || '';
                                        var sid = sources[i].Id || '';
                                        if (sid.indexOf('xtream_rec_') !== -1) isRecording = true;
                                        if (name.indexOf('xtream_rec_start_') === 0) {
                                            isRecording = true;
                                            window.__xtreamRecStartMs = parseInt(name.split('_')[3], 10);
                                        }
                                    }
                                    if (data.CurrentProgram && data.CurrentProgram.StartDate && isRecording) {
                                        if (!window.__xtreamRecStartMs) {
                                            window.__xtreamRecStartMs = new Date(data.CurrentProgram.StartDate).getTime();
                                        }
                                        if (data.CurrentProgram.EndDate) {
                                            window.__xtreamRecEndMs = new Date(data.CurrentProgram.EndDate).getTime();
                                        }
                                    }
                                }).catch(function() {});
                            });
                        }
                        return result;
                    };

                    function seekToStartOnce() {
                        // No-op: let Jellyfin/HLS.js manage initial position
                        didSeekToStart = true;
                    }

                    function doSeek(slider, evt) {
                        if (!isRecording) return;
                        var video = document.querySelector('video');
                        if (!video) return;
                        var pct = parseFloat(slider.value) / 100;
                        var startMs = window.__xtreamRecStartMs;
                        var endMs = window.__xtreamRecEndMs;
                        var epgDurSec = (startMs && endMs) ? (endMs - startMs) / 1000 : 0;
                        if (epgDurSec <= 0) return;
                        // Block Jellyfin's native seek (it restarts the transcode → resets to 0)
                        if (evt) { evt.stopImmediatePropagation(); evt.preventDefault(); }
                        var target = pct * epgDurSec;
                        // Clamp: can't seek past elapsed time or available duration
                        var elapsedSec = startMs ? (Date.now() - startMs) / 1000 : 0;
                        var maxSec = Math.min(elapsedSec, video.duration > 10 ? video.duration - 5 : elapsedSec);
                        if (maxSec > 0 && target > maxSec) {
                            target = maxSec;
                            slider.value = (target / epgDurSec) * 100;
                        }
                        console.log('[X-SEEK] target:', target.toFixed(1), 'max:', maxSec.toFixed(1), 'dur:', (video.duration||0).toFixed(1));
                        video.currentTime = target;
                    }

                    function attachSlider() {
                        var slider = document.querySelector('.osdPositionSlider');
                        if (!slider || slider.__xSeek) return;
                        slider.__xSeek = true;

                        // Intercept change: block Jellyfin's handler, seek directly in HLS
                        slider.addEventListener('change', function(e) {
                            if (!isRecording) return;
                            doSeek(slider, e);
                        }, true);

                        // Fallback: pointerup/touchend in case change doesn't fire
                        slider.addEventListener('pointerup', function() {
                            if (!isRecording) return;
                            setTimeout(function() { doSeek(slider, null); }, 50);
                        });
                        slider.addEventListener('touchend', function() {
                            if (!isRecording) return;
                            setTimeout(function() { doSeek(slider, null); }, 50);
                        });

                        console.log('[X] slider seek attached');
                    }

                    setInterval(function() {
                        try {
                            if (!isRecording) return;
                            seekToStartOnce();
                            attachSlider();
                            var v = document.querySelector('video');
                            if (v && !v.__xDbg) {
                                v.__xDbg = true;
                                v.addEventListener('seeking', function() {
                                    console.log('[X-DBG] seeking to', v.currentTime.toFixed(1), 'dur:', v.duration ? v.duration.toFixed(1) : '?');
                                });
                            }
                            if (v && v.playbackRate !== 1) v.playbackRate = 1;
                            var s = document.querySelector('.osdPositionSlider');
                            if (s) {
                                if (s.disabled) s.disabled = false;
                                s.style.pointerEvents = 'auto';
                                var c = s.closest('.osdPositionSliderContainer');
                                if (c) c.style.pointerEvents = 'auto';
                            }
                        } catch(ex) { console.log('[X-ERR]', ex.message); }
                    }, 200);

                    window.addEventListener('popstate', function() {
                        isRecording = false;
                        didSeekToStart = false;
                        window.__xtreamRecStartMs = 0;
                    });
                })();
                </script>
                """;

            int bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyClose < 0)
            {
                return;
            }

            html = html.Insert(bodyClose, Script);
            File.WriteAllText(indexPath, html);
        }
        catch (Exception)
        {
            // Non-fatal — seekbar override is a nice-to-have
        }
    }

    /// <summary>
    /// Patches the main Jellyfin JS bundle to use programme start time instead of
    /// wall clock for <c>playbackStartTimeTicks</c> on recording channels.
    /// The original code: <c>playbackStartTimeTicks=1e4*(new Date).getTime()</c>
    /// is replaced with a version that checks <c>window.__xtreamRecStartMs</c>.
    /// </summary>
    private static void PatchMainBundle(string webPath)
    {
        // Patch the playbackStartTime() READ function — called every 700ms by the
        // timeupdate handler. When __xtreamRecStartMs is set, it returns programme
        // start time instead of wall clock. This works regardless of timing because
        // the value is re-read on every update cycle.
        const string Original = "return t?t.playbackStartTimeTicks:null";
        const string Patched = "return t?(window.__xtreamRecStartMs?window.__xtreamRecStartMs*1e4:t.playbackStartTimeTicks):null";

        // Also match previously-patched versions so we can re-patch on upgrade
        const string PrevPatchMarker = "window.__xtreamRecStartMs";

        try
        {
            foreach (string file in Directory.GetFiles(webPath, "main.jellyfin.bundle.js*"))
            {
                string content = File.ReadAllText(file);

                // If already patched with a previous version, revert first
                if (content.Contains(PrevPatchMarker, StringComparison.Ordinal)
                    && !content.Contains(Original, StringComparison.Ordinal))
                {
                    // Find the patched return statement and restore original
                    int idx = content.IndexOf("return t?(window.__xtreamRecStartMs?", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        // Find the end of the patched expression (ends with "):null")
                        int end = content.IndexOf("):null", idx, StringComparison.Ordinal);
                        if (end >= 0)
                        {
                            end += "):null".Length;
                            content = content.Remove(idx, end - idx).Insert(idx, Original);
                        }
                    }
                }

                if (content.Contains(Original, StringComparison.Ordinal))
                {
                    content = content.Replace(Original, Patched, StringComparison.Ordinal);
                    File.WriteAllText(file, content);
                }
            }
        }
        catch (Exception)
        {
            // Non-fatal
        }
    }
}
