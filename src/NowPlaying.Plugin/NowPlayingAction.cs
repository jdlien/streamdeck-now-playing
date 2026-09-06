using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;

namespace NowPlaying.Plugin;

/// <summary>
/// The one action: a dial plus its touch-strip segment (README sections 6
/// and 7). Skeleton only until milestones 3 and 4; every handler is a no-op.
/// </summary>
[PluginActionId("com.jdlien.now-playing.dial")]
public sealed class NowPlayingAction : EncoderBase
{
    public NowPlayingAction(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
        Logger.Instance.LogMessage(TracingLevel.INFO, $"NowPlayingAction created for context {connection.ContextId}");
    }

    /// <summary>One skip per event in the sign direction, rate limited; README section 7.</summary>
    public override void DialRotate(DialRotatePayload payload)
    {
    }

    /// <summary>Toggle play/pause once. DialUp is deliberately ignored.</summary>
    public override void DialDown(DialPayload payload)
    {
    }

    public override void DialUp(DialPayload payload)
    {
    }

    /// <summary>A short tap toggles play/pause; a hold is a separate gesture and is ignored.</summary>
    public override void TouchPress(TouchpadPressPayload payload)
    {
    }

    public override void OnTick()
    {
    }

    public override void ReceivedSettings(ReceivedSettingsPayload payload)
    {
    }

    public override void ReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload)
    {
    }

    public override void Dispose()
    {
        Logger.Instance.LogMessage(TracingLevel.INFO, "NowPlayingAction disposed");
    }
}
