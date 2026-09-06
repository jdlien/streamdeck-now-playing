namespace NowPlaying.Media;

public sealed class MediaSessionServiceOptions
{
    /// <summary>Bound on every awaited media call. A wedged media broker must time out, not hang the service.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>MediaPropertiesChanged fires in bursts per track change; reads are coalesced over this window.</summary>
    public TimeSpan MetadataDebounce { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Delay before the single retry of a failed metadata read.</summary>
    public TimeSpan MetadataRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the chosen session keeps its rank while reporting Changing, so a track change does not flash "No media".</summary>
    public TimeSpan ChangingGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Period of the cheap re-sync that backstops missed events. Reads status only, never metadata.</summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>SourceAppUserModelId of a player that should win whenever it has a candidate session.</summary>
    public string? PreferredAppId { get; init; }

    /// <summary>Diagnostic sink. Called from the service's own loop; keep it quick.</summary>
    public Action<string>? Log { get; init; }
}
