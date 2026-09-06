using NowPlaying.Media;

namespace NowPlaying.Media.Tests;

public class MetadataNormalizerTests
{
    /// <summary>Apple Music: Artist carries "Artist — Album", AlbumTitle is empty, AlbumArtist repeats Artist.</summary>
    [Fact]
    public void AppleMusicsPackedAlbumIsDropped()
    {
        Assert.Equal("Michael Oakley", MetadataNormalizer.Artist("Michael Oakley — Prologue", "Michael Oakley — Prologue", ""));
        Assert.Equal("Michael Oakley & Missing Words", MetadataNormalizer.Artist("Michael Oakley & Missing Words — Prologue", "", ""));
    }

    [Fact]
    public void APlayerThatReportsAnAlbumKeepsItsArtistIntact()
    {
        Assert.Equal("Some — Body", MetadataNormalizer.Artist("Some — Body", "", "Their Album"));
    }

    [Fact]
    public void OnlyTheFirstSpacedEmDashSplits()
    {
        Assert.Equal("A", MetadataNormalizer.Artist("A — B — C", "", ""));
        Assert.Equal("Sigur Rós", MetadataNormalizer.Artist("Sigur Rós", "", ""));
        // A plain hyphen is not a separator, and an unspaced em dash is part of the name.
        Assert.Equal("Jay-Z", MetadataNormalizer.Artist("Jay-Z", "", ""));
        Assert.Equal("A—B", MetadataNormalizer.Artist("A—B", "", ""));
    }

    [Fact]
    public void ADashAtTheEdgeDoesNotProduceAnEmptyArtist()
    {
        Assert.Equal(" — Prologue", MetadataNormalizer.Artist(" — Prologue", "", ""));
        Assert.Equal("Michael Oakley", MetadataNormalizer.Artist("Michael Oakley — ", "", ""));
    }

    [Fact]
    public void FallbacksStillApply()
    {
        Assert.Equal("Album Artist", MetadataNormalizer.Artist("", "Album Artist", ""));
        Assert.Equal("Only Album", MetadataNormalizer.Artist("", "", "Only Album"));
        Assert.Equal("", MetadataNormalizer.Artist("", "", ""));
    }
}
