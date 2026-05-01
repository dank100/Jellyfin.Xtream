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
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests that <see cref="Service.MultiplexedRestream"/> produces a
/// correct <see cref="MediaSourceInfo"/> and implements the standard
/// LiveTV stream path for proper AAC bitstream filter handling.
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
        appHost.Setup(a => a.GetApiUrlForLocalAccess())
               .Returns("http://localhost:8096");

        var logger = new Mock<ILogger>();

        return new Service.MultiplexedRestream(
            appHost.Object,
            logger.Object,
            null!,
            streamId);
    }

    [Fact]
    public void MediaSource_UsesStandardLiveTvPath()
    {
        var restream = CreateRestream(streamId: 42);
        var source = restream.MediaSource;

        Assert.Contains("/LiveTv/LiveStreamFiles/", source.Path);
        Assert.EndsWith("/stream.ts", source.Path);
        Assert.Contains("/LiveTv/LiveStreamFiles/", source.EncoderPath);
        Assert.EndsWith("/stream.ts", source.EncoderPath);
    }

    [Fact]
    public void MediaSource_ContainerIsTs()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.Equal("ts", source.Container);
    }

    [Fact]
    public void MediaSource_ProbingEnabled()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.True(source.SupportsProbing, "SupportsProbing must be true so Jellyfin discovers actual codecs");
    }

    [Fact]
    public void MediaSource_AllPlaybackPathsEnabled()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.True(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
    }

    [Fact]
    public void MediaSource_IsInfiniteStream()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.True(source.IsInfiniteStream);
    }

    [Fact]
    public void MediaSource_ProtocolIsHttp()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.Equal(MediaProtocol.Http, source.Protocol);
    }

    [Fact]
    public void MediaSource_IdContainsStreamId()
    {
        var restream = CreateRestream(streamId: 777);
        var source = restream.MediaSource;

        Assert.Equal("multiplex_777", source.Id);
    }

    [Fact]
    public void MediaSource_IsNotRemote()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.False(source.IsRemote);
    }

    [Fact]
    public void ImplementsIDirectStreamProvider()
    {
        var restream = CreateRestream();

        Assert.IsAssignableFrom<IDirectStreamProvider>(restream);
    }

    [Fact]
    public void TunerHostId_IsCorrect()
    {
        var restream = CreateRestream();

        Assert.Equal("Xtream-Multiplex", restream.TunerHostId);
    }

    [Fact]
    public void EnableStreamSharing_IsTrue()
    {
        var restream = CreateRestream();

        Assert.True(restream.EnableStreamSharing);
    }

    [Fact]
    public void GetStream_WithNoBuffer_ReturnsNullStream()
    {
        var restream = CreateRestream();

        // Multiplexer is null in test, GetBuffer returns null
        var stream = restream.GetStream();
        Assert.Same(System.IO.Stream.Null, stream);
    }
}
