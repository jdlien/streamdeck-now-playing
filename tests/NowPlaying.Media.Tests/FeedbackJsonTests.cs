using Newtonsoft.Json;
using NowPlaying.Media;
using NowPlaying.Plugin;

namespace NowPlaying.Media.Tests;

public class FeedbackJsonTests
{
    [Fact]
    public void AFullPayloadSerialisesToTheShapeSetFeedbackExpects()
    {
        var snapshot = new NowPlayingSnapshot(
            "app", PlaybackState.Paused, "Warriors of the Wasteland", "Michael Oakley — Prologue",
            TimeSpan.FromSeconds(187), null, TimeSpan.FromSeconds(235), true, true, true);
        var frame = FeedbackRenderer.Render(snapshot, DateTimeOffset.UnixEpoch);

        var json = FeedbackJson.ToJObject(FeedbackRenderer.Diff(null, frame)).ToString(Formatting.None);

        Assert.Equal(
            "{\"icon\":{\"enabled\":true,\"value\":\"imgs/icons/pause.svg\"},"
            + "\"track\":\"Warriors of the Wasteland\","
            + "\"artist\":\"Michael Oakley — Prologue\","
            + "\"progress\":{\"enabled\":true,\"value\":796},"
            + "\"elapsed\":\"3:07\",\"total\":\"3:55\"}",
            json);
    }

    [Fact]
    public void NoMediaHidesItemsWithBooleansAndEmptyStrings()
    {
        var json = FeedbackJson.ToJObject(FeedbackRenderer.Diff(null, FeedbackRenderer.Render(NowPlayingSnapshot.Empty, DateTimeOffset.UnixEpoch)))
            .ToString(Formatting.None);
        Assert.Equal(
            "{\"icon\":{\"enabled\":false},\"track\":\"No media\",\"artist\":\"\","
            + "\"progress\":{\"enabled\":false,\"value\":0},\"elapsed\":\"\",\"total\":\"\"}",
            json);
    }

    [Fact]
    public void UnsupportedValuesAreRejectedLoudly()
    {
        Assert.Throws<NotSupportedException>(() => FeedbackJson.ToJObject(new Dictionary<string, object> { ["x"] = new object() }));
    }
}
