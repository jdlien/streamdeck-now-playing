using System.Reflection;
using BarRaider.SdTools;
using Newtonsoft.Json.Linq;
using NowPlaying.Device;
using NowPlaying.Plugin;

namespace NowPlaying.Media.Tests;

public class DisplayRenameTests
{
    [Fact]
    public void PickingAnotherMonitorIsNotARename()
    {
        // The inspector sends the whole settings object, so choosing the XDR
        // from the list arrives carrying the name box of the screen just left.
        // This is the update that used to rename the XDR to the 2022 panel's name.
        Assert.False(DisplayRename.IsEdit(
            "Studio Display (6578)", "Studio Display 2022",
            "Studio Display XDR", "Studio Display 2022"));
    }

    [Fact]
    public void TypingInTheBoxIsARename() =>
        Assert.True(DisplayRename.IsEdit("Studio Display XDR", "", "Studio Display XDR", "Centre"));

    [Fact]
    public void ClearingTheBoxIsARename() =>
        Assert.True(DisplayRename.IsEdit("Studio Display XDR", "Studio Display 2022", "Studio Display XDR", ""));

    [Fact]
    public void TheSettingsADialAppearsWithAreNeverARename() =>
        Assert.False(DisplayRename.IsEdit("Studio Display XDR", null, "Studio Display XDR", "Studio Display 2022"));

    [Fact]
    public void AnEchoOfTheSameNameIsNotARename() =>
        Assert.False(DisplayRename.IsEdit("Studio Display XDR", "Centre", "Studio Display XDR", "Centre "));

    [Fact]
    public void TheBindingComparesLikeTheResolutionDoes() =>
        Assert.True(DisplayRename.IsEdit("studio display xdr", "", "Studio Display XDR", "Centre"));
}

public class SharedNameTests
{
    [Fact]
    public void ADisplayKeepsItsSerialWhenTheOtherDropsOut()
    {
        var sharedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DisplayNameNormalizer.Disambiguate(
            [("Studio Display XDR", 124421315u), ("Studio Display", 763926578u), ("Studio Display", 1171739599u)], sharedNames);

        var names = DisplayNameNormalizer.Disambiguate(
            [("Studio Display XDR", 124421315u), ("Studio Display", 763926578u)], sharedNames);

        Assert.Equal(["Studio Display XDR", "Studio Display (6578)"], names);
    }

    [Fact]
    public void ANameNeverSeenTwiceStaysPlain()
    {
        var sharedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(["Studio Display"], DisplayNameNormalizer.Disambiguate([("Studio Display", 763926578u)], sharedNames));
        Assert.Empty(sharedNames);
    }
}

[Collection(GlobalSettingsCollection.Name)]
public class GlobalSettingsStoreTests : IDisposable
{
    public GlobalSettingsStoreTests() => GlobalSettingsStore.Reset();

    public void Dispose()
    {
        GlobalSettingsStore.Reset();
        DisplayNames.Apply(null);
    }

    [Fact]
    public async Task ARenameBeforeTheFirstLoadDoesNotWipeTheOtherNames()
    {
        var connection = RecordingConnection.Create(out var recorder);

        // A rename that lands while the actions are still appearing...
        await DisplayNames.SetAsync(connection, "Studio Display XDR", "Centre");
        Assert.Empty(recorder.Saved); // ...is held, not written over the app's copy

        // ...and is replayed onto what the app actually has.
        GlobalSettingsStore.Apply(new JObject
        {
            ["preferredApp"] = "com.apple.Music",
            [DisplayNames.GlobalKey] = new JObject
            {
                ["Studio Display (6578)"] = "Right",
                ["Studio Display (9599)"] = "Left",
            },
        });

        var saved = Assert.Single(recorder.Saved);
        Assert.Equal("com.apple.Music", saved["preferredApp"]?.ToString());
        var names = (JObject)saved[DisplayNames.GlobalKey]!;
        Assert.Equal("Right", names["Studio Display (6578)"]?.ToString());
        Assert.Equal("Left", names["Studio Display (9599)"]?.ToString());
        Assert.Equal("Centre", names["Studio Display XDR"]?.ToString());
        Assert.Equal("Centre", DisplayNames.CustomFor("Studio Display XDR"));
    }

    [Fact]
    public async Task AStaleCopyOfTheSettingsDoesNotUndoARecentChange()
    {
        var connection = RecordingConnection.Create(out var recorder);
        var original = new JObject
        {
            [DisplayNames.GlobalKey] = new JObject { ["Studio Display (6578)"] = "Right" },
        };

        await DisplayNames.SetAsync(connection, "Studio Display XDR", "Centre");
        GlobalSettingsStore.Apply(original);   // replays the held rename

        // Every action receives every answer: here is the same pre-rename copy again.
        GlobalSettingsStore.Apply(original);
        Assert.Equal("Centre", DisplayNames.CustomFor("Studio Display XDR"));

        // And a later save of another key does not write the undo back.
        await GlobalSettingsStore.SaveAsync(connection, GlobalSettingsStore.BrightnessKey, 40);
        var names = (JObject)recorder.Saved[^1][DisplayNames.GlobalKey]!;
        Assert.Equal("Centre", names["Studio Display XDR"]?.ToString());
        Assert.Equal("Right", names["Studio Display (6578)"]?.ToString());
    }

    [Fact]
    public async Task ARenameAfterLoadChangesOnlyItsOwnScreen()
    {
        var connection = RecordingConnection.Create(out var recorder);
        GlobalSettingsStore.Apply(new JObject
        {
            [DisplayNames.GlobalKey] = new JObject { ["Studio Display (6578)"] = "Right" },
        });

        await DisplayNames.SetAsync(connection, "Studio Display XDR", "Centre");
        await DisplayNames.SetAsync(connection, "Studio Display XDR", "");

        var names = (JObject)recorder.Saved[^1][DisplayNames.GlobalKey]!;
        Assert.Equal("Right", names["Studio Display (6578)"]?.ToString());
        Assert.Null(names["Studio Display XDR"]);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalSettingsCollection
{
    public const string Name = "Global settings";
}

/// <summary>An <see cref="ISDConnection"/> that only records global-settings writes.</summary>
public class RecordingConnection : DispatchProxy
{
    public List<JObject> Saved { get; } = [];

    public static ISDConnection Create(out RecordingConnection recorder)
    {
        var proxy = Create<ISDConnection, RecordingConnection>();
        recorder = (RecordingConnection)(object)proxy;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(ISDConnection.SetGlobalSettingsAsync))
        {
            Saved.Add((JObject)((JObject)args![0]!).DeepClone());
        }

        return targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
    }
}
