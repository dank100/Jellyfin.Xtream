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

using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Providers;
using Jellyfin.Xtream.Service;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Xtream;

/// <inheritdoc />
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IXtreamClient, XtreamClient>();
        serviceCollection.AddSingleton<TimerStore>();
        serviceCollection.AddSingleton<XmltvParser>();
        serviceCollection.AddSingleton<LiveTvService>();
        serviceCollection.AddSingleton<ILiveTvService>(sp => sp.GetRequiredService<LiveTvService>());
        serviceCollection.AddSingleton<IChannel, CatchupChannel>();
        serviceCollection.AddSingleton<IChannel, SeriesChannel>();
        serviceCollection.AddSingleton<IChannel, VodChannel>();
        serviceCollection.AddSingleton<IPreRefreshProvider, XtreamVodProvider>();
        serviceCollection.AddSingleton<RecordingEngine>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<RecordingEngine>());
        serviceCollection.AddSingleton<ConnectionMultiplexer>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<ConnectionMultiplexer>());
        serviceCollection.AddSingleton<IMediaSourceProvider, RecordingMediaSourceProvider>();

        // Register global MVC action filter to intercept DynamicHls requests for recordings.
        // Use ServiceFilter so the filter is resolved from DI on every request.
        serviceCollection.AddScoped<RecordingHlsActionFilter>();
        serviceCollection.Configure<MvcOptions>(options =>
        {
            options.Filters.Add(new ServiceFilterAttribute(typeof(RecordingHlsActionFilter)));
        });
    }
}
