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

using System.Linq;
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
/// <see cref="MediaSourceInfo"/> that avoids unnecessary transcoding
/// on all major Jellyfin clients (Android TV, iOS/Swiftfin, web).
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

        // MultiplexedRestream only uses the multiplexer in Open/Close,
        // which we don't call in these tests, so null is safe.
        return new Service.MultiplexedRestream(
            appHost.Object,
            logger.Object,
            null!,
            streamId);
    }

    /// <summary>
    /// Simulates Jellyfin's LiveTvMediaSourceProvider.Normalize() method which
    /// unconditionally sets IsInterlaced=true and NalLengthSize="0" for all video
    /// streams from non-default LiveTV services. This is the exact behavior from
    /// Jellyfin 10.11.x (src/Jellyfin.LiveTv/LiveTvMediaSourceProvider.cs:249-253).
    /// </summary>
    private static void SimulateJellyfinNormalize(MediaSourceInfo mediaSource)
    {
        // Jellyfin forces SupportsTranscoding for non-default services
        mediaSource.SupportsTranscoding = true;

        foreach (var stream in mediaSource.MediaStreams)
        {
            if (stream.Type == MediaStreamType.Video && string.IsNullOrWhiteSpace(stream.NalLengthSize))
            {
                stream.NalLengthSize = "0";
            }

            if (stream.Type == MediaStreamType.Video)
            {
                stream.IsInterlaced = true;
            }
        }
    }

    [Fact]
    public void MediaSource_ProbingDisabled_AvoidsProbeCycle()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.False(source.SupportsProbing, "SupportsProbing should be false to skip the probe→close→reopen cycle");
    }

    [Fact]
    public void MediaSource_AnalyzeDuration_Is3000ms()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.Equal(3000, source.AnalyzeDurationMs);
    }

    [Fact]
    public void MediaSource_HasVideoStream_WithH264Codec()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;
        var video = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);

        Assert.NotNull(video);
        Assert.Equal("h264", video.Codec);
        Assert.Equal("High", video.Profile);
        Assert.True(video.Index >= 0, "Video stream Index must be >= 0 for Jellyfin to skip probing");
        Assert.Equal(1920, video.Width);
        Assert.Equal(1080, video.Height);
    }

    [Fact]
    public void MediaSource_HasAudioStream_WithAacCodec()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;
        var audio = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);

        Assert.NotNull(audio);
        Assert.Equal("aac", audio.Codec);
        Assert.Equal("LC", audio.Profile);
        Assert.True(audio.Index >= 0, "Audio stream Index must be >= 0 for Jellyfin to skip probing");
        Assert.Equal(2, audio.Channels);
        Assert.Equal(48000, audio.SampleRate);
    }

    [Fact]
    public void MediaSource_SupportsDirectPlay()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;

        Assert.True(source.SupportsDirectPlay, "Direct play must be enabled to avoid transcoding");
    }

    [Fact]
    public void MediaSource_VideoIsProgressive_NotInterlaced()
    {
        var restream = CreateRestream();
        var source = restream.MediaSource;
        var video = source.MediaStreams.First(s => s.Type == MediaStreamType.Video);

        Assert.False(video.IsInterlaced, "Video must not be interlaced — interlaced triggers yadif deinterlacer and forces transcoding");
    }

    /// <summary>
    /// Jellyfin's LiveTvMediaSourceProvider.Normalize() unconditionally sets
    /// IsInterlaced=true for all video streams from non-default LiveTV services.
    /// Our MediaSource getter must undo this so that clients don't add a yadif
    /// deinterlacer (which forces full video transcoding on iOS/Swiftfin).
    /// This test simulates the exact Jellyfin Normalize behavior and verifies
    /// that reading MediaSource afterwards resets IsInterlaced to false.
    /// </summary>
    [Fact]
    public void MediaSource_ResetsInterlaced_AfterJellyfinNormalize()
    {
        var restream = CreateRestream();

        // Step 1: Jellyfin reads MediaSource (e.g. in GetChannelStream)
        var source = restream.MediaSource;
        var video = source.MediaStreams.First(s => s.Type == MediaStreamType.Video);
        Assert.False(video.IsInterlaced, "Before Normalize: should be progressive");

        // Step 2: Jellyfin's Normalize() runs and forces IsInterlaced=true
        SimulateJellyfinNormalize(source);
        Assert.True(video.IsInterlaced, "After Normalize: Jellyfin forced interlaced=true");

        // Step 3: Jellyfin reads MediaSource again (in OpenLiveStreamInternal)
        // Our getter must reset IsInterlaced back to false
        var sourceAfter = restream.MediaSource;
        var videoAfter = sourceAfter.MediaStreams.First(s => s.Type == MediaStreamType.Video);
        Assert.False(videoAfter.IsInterlaced, "After re-read: getter must reset interlaced to false to prevent transcoding");
    }

    /// <summary>
    /// Multiple consecutive reads of MediaSource should always return progressive video.
    /// This guards against race conditions or state corruption.
    /// </summary>
    [Fact]
    public void MediaSource_AlwaysProgressive_AcrossMultipleReads()
    {
        var restream = CreateRestream();

        for (int i = 0; i < 5; i++)
        {
            // Simulate Normalize tampering before each read
            SimulateJellyfinNormalize(restream.MediaSource);

            var source = restream.MediaSource;
            var video = source.MediaStreams.First(s => s.Type == MediaStreamType.Video);
            Assert.False(video.IsInterlaced, $"Read {i}: video should always be progressive");
        }
    }

    /// <summary>
    /// Verifies the complete MediaSourceInfo configuration that clients use
    /// to decide between direct play vs transcode. All properties must be set
    /// correctly to avoid transcoding on Android TV, iOS, and web clients.
    /// </summary>
    [Fact]
    public void MediaSource_FullConfiguration_EnablesStreamCopy()
    {
        var restream = CreateRestream(streamId: 42);

        // Simulate the full Jellyfin flow: Normalize → re-read
        SimulateJellyfinNormalize(restream.MediaSource);
        var source = restream.MediaSource;

        // Core properties for avoiding transcoding
        Assert.False(source.SupportsProbing, "Probing must be disabled");
        Assert.Equal(3000, source.AnalyzeDurationMs);
        Assert.True(source.SupportsDirectPlay);
        Assert.True(source.IsInfiniteStream);
        Assert.Equal("ts", source.Container);

        // Video stream — must enable stream copy
        var video = source.MediaStreams.First(s => s.Type == MediaStreamType.Video);
        Assert.Equal("h264", video.Codec);
        Assert.False(video.IsInterlaced, "Interlaced causes yadif → transcode");
        Assert.Equal(0, video.Index);
        Assert.NotNull(video.Width);
        Assert.NotNull(video.Height);
        Assert.NotNull(video.BitRate);

        // Audio stream — must enable stream copy
        var audio = source.MediaStreams.First(s => s.Type == MediaStreamType.Audio);
        Assert.Equal("aac", audio.Codec);
        Assert.Equal(1, audio.Index);
        Assert.NotNull(audio.Channels);
        Assert.NotNull(audio.SampleRate);

        // Path should point to our HLS endpoint
        Assert.Contains("/Xtream/Multiplex/42/playlist.m3u8", source.Path);
    }
}
