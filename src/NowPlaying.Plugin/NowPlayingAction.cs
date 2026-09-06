using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using NowPlaying.Media;

namespace NowPlaying.Plugin;

/// <summary>
/// The one action: a dial plus its touch-strip segment. StreamDeck-Tools
/// creates an instance on willAppear and disposes it on willDisappear, so
/// the constructor is "appear" and Dispose is "disappear".
///
/// Display: README section 6. Input: section 7. Recovery: section 8.
/// </summary>
[PluginActionId("com.jdlien.now-playing.dial")]
public sealed class NowPlayingAction : EncoderBase
{
    /// <summary>Minimum gap between skips. Extra rotate events inside it are dropped, never queued.</summary>
    private static readonly TimeSpan SkipInterval = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private readonly Action<NowPlayingSnapshot> _onSnapshot;
    private FeedbackFrame? _lastFrame;
    private DateTimeOffset _lastSkipAt = DateTimeOffset.MinValue;

    public NowPlayingAction(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
        Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] appear: context {connection.ContextId}");

        _onSnapshot = snapshot => Push(snapshot, full: false);
        MediaHub.SnapshotChanged += _onSnapshot;
        MediaHub.Attach();

        connection.OnSystemDidWakeUp += (_, _) =>
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, "[action] system woke up: full media refresh");
            _ = MediaHub.RefreshAsync(full: true);
        };
        connection.OnDeviceDidConnect += (_, _) =>
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, "[action] device connected: re-push");
            _ = MediaHub.RefreshAsync(full: false);
            Push(MediaHub.Current, full: true);
        };

        _ = ApplyLayoutAndPushAsync();
    }

    /// <summary>Path of the custom layout, relative to the plugin folder. Also named in the manifest.</summary>
    private const string LayoutPath = "layouts/now-playing.json";

    /// <summary>
    /// Re-apply the layout file, then push everything. Setting the layout
    /// explicitly means an edited layout takes effect on a plugin restart, and
    /// the full push follows because the strip may show anything from before.
    /// </summary>
    private async Task ApplyLayoutAndPushAsync()
    {
        try
        {
            await Connection.SetFeedbackLayoutAsync(LayoutPath);
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] setFeedbackLayout failed: {ex.Message}");
        }

        Push(MediaHub.Current, full: true);
    }

    // -- input --------------------------------------------------------------

    /// <summary>
    /// One skip per event in the sign direction regardless of the tick count,
    /// which can exceed one on a fast spin. Rotation while pressed is
    /// reserved and ignored.
    /// </summary>
    public override void DialRotate(DialRotatePayload payload)
    {
        if (payload.Ticks == 0 || payload.IsDialPressed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (now - _lastSkipAt < SkipInterval)
            {
                return;
            }

            _lastSkipAt = now;
        }

        if (payload.Ticks > 0)
        {
            Fire("next", MediaHub.NextAsync());
        }
        else
        {
            Fire("previous", MediaHub.PreviousAsync());
        }
    }

    /// <summary>Toggle once per press. DialUp is deliberately a no-op.</summary>
    public override void DialDown(DialPayload payload) => Fire("toggle (dial)", MediaHub.TogglePlayPauseAsync());

    public override void DialUp(DialPayload payload)
    {
    }

    /// <summary>A short tap toggles. A hold is a distinct gesture and is ignored for now.</summary>
    public override void TouchPress(TouchpadPressPayload payload)
    {
        if (payload.IsLongPress)
        {
            return;
        }

        Fire("toggle (touch)", MediaHub.TogglePlayPauseAsync());
    }

    private static void Fire(string name, Task<bool> command) =>
        _ = command.ContinueWith(
            task =>
            {
                if (task.IsFaulted)
                {
                    Logger.Instance.LogMessage(TracingLevel.ERROR, $"[action] {name} failed: {task.Exception?.GetBaseException().Message}");
                }
                else
                {
                    Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] {name}: {(task.Result ? "accepted" : "rejected")}");
                }
            },
            TaskScheduler.Default);

    // -- display --------------------------------------------------------------

    /// <summary>
    /// StreamDeck-Tools calls this about once a second. While playing, the
    /// extrapolated position moves the bar; otherwise the diff is empty and
    /// nothing is sent.
    /// </summary>
    public override void OnTick() => Push(MediaHub.Current, full: false);

    private void Push(NowPlayingSnapshot snapshot, bool full)
    {
        Dictionary<string, object> payload;
        lock (_gate)
        {
            var frame = FeedbackRenderer.Render(snapshot, DateTimeOffset.UtcNow);
            payload = FeedbackRenderer.Diff(full ? null : _lastFrame, frame);
            _lastFrame = frame;
        }

        if (payload.Count == 0)
        {
            return;
        }

        _ = SendAsync(payload);
    }

    private async Task SendAsync(Dictionary<string, object> payload)
    {
        try
        {
            await Connection.SetFeedbackAsync(FeedbackJson.ToJObject(payload));
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] setFeedback failed: {ex.Message}");
        }
    }

    // -- settings and lifetime ------------------------------------------------

    public override void ReceivedSettings(ReceivedSettingsPayload payload)
    {
    }

    public override void ReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload)
    {
    }

    public override void Dispose()
    {
        MediaHub.SnapshotChanged -= _onSnapshot;
        MediaHub.Detach();
        Logger.Instance.LogMessage(TracingLevel.INFO, "[action] disappear");
    }
}
