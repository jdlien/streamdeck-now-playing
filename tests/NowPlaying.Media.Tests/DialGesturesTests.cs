using NowPlaying.Plugin;

namespace NowPlaying.Media.Tests;

public class DialGesturesTests
{
    [Fact]
    public void TrackModeIsUnchangedFromV1()
    {
        Assert.Equal(DialTurn.SkipTrack, DialGestures.OnRotate(DialGestures.TrackMode, pressed: false));
        // Reserved, and left reserved: README section 7.
        Assert.Equal(DialTurn.Ignore, DialGestures.OnRotate(DialGestures.TrackMode, pressed: true));
        Assert.True(DialGestures.TogglesOnPress(DialGestures.TrackMode));
        Assert.False(DialGestures.TogglesOnRelease(DialGestures.TrackMode, rotatedWhilePressed: false));
    }

    [Fact]
    public void VolumeModeSwapsWhatTurningMeans()
    {
        Assert.Equal(DialTurn.Volume, DialGestures.OnRotate(DialGestures.VolumeMode, pressed: false));
        Assert.Equal(DialTurn.SkipTrack, DialGestures.OnRotate(DialGestures.VolumeMode, pressed: true));
    }

    [Fact]
    public void VolumeModeDefersTheToggleToRelease()
    {
        // Deciding on press would toggle playback on the way into a skip gesture.
        Assert.False(DialGestures.TogglesOnPress(DialGestures.VolumeMode));
        Assert.True(DialGestures.TogglesOnRelease(DialGestures.VolumeMode, rotatedWhilePressed: false));
    }

    [Fact]
    public void AHoldAndTurnDoesNotAlsoTogglePlayback()
    {
        Assert.False(DialGestures.TogglesOnRelease(DialGestures.VolumeMode, rotatedWhilePressed: true));
    }

    [Fact]
    public void AnUnknownModeBehavesLikeTheDefault()
    {
        // Settings can carry anything; falling back to the shipped behaviour beats throwing.
        Assert.Equal(DialTurn.SkipTrack, DialGestures.OnRotate("nonsense", pressed: false));
        Assert.True(DialGestures.TogglesOnPress("nonsense"));
    }
}
