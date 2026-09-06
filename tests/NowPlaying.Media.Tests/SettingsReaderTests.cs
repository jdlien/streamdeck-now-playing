using Newtonsoft.Json.Linq;
using NowPlaying.Plugin;

namespace NowPlaying.Media.Tests;

public class SettingsReaderTests
{
    [Fact]
    public void MissingSettingsFallBack()
    {
        Assert.Equal("toggle", SettingsReader.GetString(null, "pressAction", "toggle"));
        Assert.Equal("toggle", SettingsReader.GetString(new JObject(), "pressAction", "toggle"));
        Assert.True(SettingsReader.GetBool(null, "showArt", true));
        Assert.True(SettingsReader.GetBool(new JObject(), "showArt", true));
    }

    [Fact]
    public void ValuesOutsideTheAllowedSetFallBack()
    {
        var settings = new JObject { ["pressAction"] = "launch-missiles" };
        Assert.Equal("toggle", SettingsReader.GetString(settings, "pressAction", "toggle", "toggle", "next"));
        Assert.Equal("next", SettingsReader.GetString(new JObject { ["pressAction"] = "next" }, "pressAction", "toggle", "toggle", "next"));
    }

    [Fact]
    public void BooleansAcceptJsonBooleansAndStrings()
    {
        Assert.False(SettingsReader.GetBool(new JObject { ["showArt"] = false }, "showArt", true));
        Assert.False(SettingsReader.GetBool(new JObject { ["showArt"] = "false" }, "showArt", true));
        Assert.True(SettingsReader.GetBool(new JObject { ["showArt"] = "nonsense" }, "showArt", true));
        Assert.True(SettingsReader.GetBool(new JObject { ["showArt"] = 1 }, "showArt", true));
    }

    [Fact]
    public void MissingKeysAreDetectedForWriteBack()
    {
        Assert.True(SettingsReader.IsMissingAny(null, "a"));
        Assert.True(SettingsReader.IsMissingAny(new JObject { ["a"] = 1 }, "a", "b"));
        Assert.False(SettingsReader.IsMissingAny(new JObject { ["a"] = 1, ["b"] = false }, "a", "b"));
    }
}
