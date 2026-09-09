using Newtonsoft.Json.Linq;
using NowPlaying.Device;
using NowPlaying.Plugin;

namespace NowPlaying.Media.Tests;

public class DisplayNameNormalizerTests
{
    [Theory]
    // The case that prompted this: a 2022 Studio Display's own EDID string.
    [InlineData("StudioDisplay", "Studio Display")]
    [InlineData("ProDisplayXDR", "Pro Display XDR")]
    public void RunTogetherNamesAreSplit(string input, string expected) =>
        Assert.Equal(expected, DisplayNameNormalizer.Normalize(input));

    [Theory]
    // Already spaced: the manufacturer's own formatting wins.
    [InlineData("Studio Display XDR")]
    [InlineData("BenQ MA270S")]
    [InlineData("DELL U2723QE")]
    public void SpacedNamesAreLeftAlone(string input) =>
        Assert.Equal(input, DisplayNameNormalizer.Normalize(input));

    [Theory]
    // A wrong split is worse than none, so short fragments block it.
    [InlineData("BenQ")]
    [InlineData("iMac")]
    [InlineData("MA270S")]
    [InlineData("LG")]
    public void NamesThatWouldSplitBadlyAreLeftAlone(string input) =>
        Assert.Equal(input, DisplayNameNormalizer.Normalize(input));

    [Fact]
    public void EmptyAndNullAreEmpty()
    {
        Assert.Equal("", DisplayNameNormalizer.Normalize(null));
        Assert.Equal("", DisplayNameNormalizer.Normalize("   "));
    }
}

public class DisplayNamesTests
{
    [Fact]
    public void WithNoOverridesThePlatformNameIsUsed()
    {
        DisplayNames.Apply(null);
        Assert.Equal("Studio Display XDR", DisplayNames.Resolve("Studio Display XDR"));
        Assert.Null(DisplayNames.CustomFor("Studio Display XDR"));
    }

    [Fact]
    public void AnOverrideReplacesTheNameShown()
    {
        DisplayNames.Apply(new JObject
        {
            [DisplayNames.GlobalKey] = new JObject { ["Studio Display XDR"] = "Centre" },
        });
        Assert.Equal("Centre", DisplayNames.Resolve("Studio Display XDR"));
        // Screens without an override are untouched.
        Assert.Equal("BenQ MA270S", DisplayNames.Resolve("BenQ MA270S"));
    }

    [Fact]
    public void BlankOverridesAreIgnoredRatherThanBlankingTheRow()
    {
        DisplayNames.Apply(new JObject
        {
            [DisplayNames.GlobalKey] = new JObject { ["BenQ MA270S"] = "   " },
        });
        Assert.Equal("BenQ MA270S", DisplayNames.Resolve("BenQ MA270S"));
    }
}
