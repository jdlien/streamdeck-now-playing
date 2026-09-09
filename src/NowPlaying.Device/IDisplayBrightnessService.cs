namespace NowPlaying.Device;

/// <summary>
/// Brightness of every monitor the platform can address, keyed by name. Each
/// dial binds to a monitor (or to "automatic", the primary) and reads and
/// writes its own.
///
/// Implementations are per platform and slow in different ways: Windows speaks
/// DDC/CI through dxva2, macOS needs DDC over IOAVService for third-party
/// monitors and DisplayServices for Apple ones (macos-port-plan D5). Every
/// implementation owes callers the same contract: never block an input path,
/// and report a monitor as unavailable rather than guessing at its level.
/// </summary>
public interface IDisplayBrightnessService : IDisposable
{
    /// <summary>One monitor's snapshot changed.</summary>
    event Action<string, DisplayBrightnessSnapshot>? Changed;

    /// <summary>The set of monitors changed; bindings should be re-resolved.</summary>
    event Action? MonitorsChanged;

    /// <summary>Names of the monitors that answer, primary first.</summary>
    IReadOnlyList<string> MonitorNames { get; }

    /// <summary>Begin enumerating and tracking monitors. Later calls are no-ops.</summary>
    void Start();

    /// <summary>Re-enumerate after a display change or a wake.</summary>
    void Refresh();

    /// <summary>The monitor a binding refers to right now, or null when it is not available.</summary>
    string? Resolve(string? name);

    /// <summary>The named monitor's snapshot, or <see cref="DisplayBrightnessSnapshot.Unavailable"/>.</summary>
    DisplayBrightnessSnapshot Get(string? name);

    /// <summary>The monitor after (+1) or before (-1) this one, wrapping. Null when there is nothing to move to.</summary>
    string? Neighbor(string? name, int direction);

    /// <summary>Move the named monitor's level by <paramref name="delta"/> percent.</summary>
    bool Adjust(string? name, int delta);

    /// <summary>Flip the named monitor's dim toggle.</summary>
    bool Toggle(string? name);
}
