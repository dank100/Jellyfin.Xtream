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

using System.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests that <see cref="Service.MultiplexedRestream"/> produces a
/// <see cref="MediaSourceInfo"/> configured for probing-based playback
/// with all delivery methods enabled.
/// </summary>
public class MultiplexedRestreamTests
{
    /// <summary>
    /// Creates a <see cref="Service.MultiplexedRestream"/> with mocked dependencies
    /// and returns the live stream instance for testing.
    /// </summary>
    private static Service.MultiplexedRestream CreateRestream(int streamId = 12345)
    {
        var appHost = new Mock<IServerApplicationHost>();
        appHost.Setup(a => a.GetSmartApiUrl(It.IsAny<IPAddress>()))
               .Returns("http://localhost:8096");

        var logger = new Mock<ILogger>();

        return new Service.MultiplexedRestream(
            appHost.Object,
            logger.Object,
            null!,
            streamId);
    }

    [Fact]
    public void MediaSource_ProbingDisabled()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.False(source.SupportsProbing, "SupportsProbing should be false to avoid probe→close→reopen losing codec info");
    }

    [Fact]
    public void MediaSource_AnalyzeDuration_Is500ms()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.Equal(500, source.AnalyzeDurationMs);
    }

    [Fact]
    public void MediaSource_SupportsDirectPlay()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.True(source.SupportsDirectPlay);
    }

    [Fact]
    public void MediaSource_DirectPlayOnly()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.True(source.SupportsDirectPlay, "DirectPlay must be enabled for native HLS playback");
        Assert.False(source.SupportsDirectStream, "DirectStream disabled — uses ffmpeg which breaks iPhone");
        Assert.False(source.SupportsTranscoding, "Transcoding/remux disabled to force DirectPlay of HLS URL");
    }

    [Fact]
    public void MediaSource_IsInfiniteStream()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.True(source.IsInfiniteStream);
    }

    [Fact]
    public void MediaSource_Container_IsHls()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.Equal("hls", source.Container);
    }

    [Fact]
    public void MediaSource_IsNotRemote()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.False(source.IsRemote);
    }

    [Fact]
    public void MediaSource_Protocol_IsHttp()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.Equal(MediaBrowser.Model.MediaInfo.MediaProtocol.Http, source.Protocol);
    }

    [Fact]
    public void MediaSource_Path_PointsToHlsPlaylist()
    {
        var restream = CreateRestream(streamId: 42);
        var source = restream.MediaSource;

        Assert.Contains("/Xtream/Multiplex/42/playlist.m3u8", source.Path);
    }

    [Fact]
    public void MediaSource_Id_ContainsStreamId()
    {
        var restream = CreateRestream(streamId: 42);
        var source = restream.MediaSource;

        Assert.Equal("multiplex_42", source.Id);
    }

    [Fact]
    public void MediaSource_EmptyMediaStreams()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.NotNull(source.MediaStreams);
        Assert.Empty(source.MediaStreams);
    }
}
