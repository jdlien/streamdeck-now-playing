using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;

namespace NowPlaying.Plugin;

/// <summary>
/// Runs plugin code that must not let an exception out.
///
/// StreamDeck-Tools dispatches every event -- each dial turn, press, tick and
/// settings change -- from an <c>async void</c> method with no catch, so an
/// exception thrown by any handler is rethrown on the thread pool and ends the
/// process. The Stream Deck app relaunches it, but every dial goes blank and
/// loses its state meanwhile, and a few of those in quick succession and the
/// app disables the plugin as unstable until it is restarted. One bad frame is
/// not worth any of that: it is logged and the event is dropped.
/// </summary>
internal static class Guard
{
    public static void Run(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.ERROR, $"[guard] {what} threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Raise an event one subscriber at a time, so one that throws neither
    /// stops the rest from hearing it nor escapes into whoever raised it.
    ///
    /// The second half matters more than it sounds. The hubs' events are raised
    /// from the services' own worker loops -- a display's brightness worker, the
    /// media supervisor, a Core Audio callback -- and an exception escaping into
    /// one of those ends the loop for good. A display whose worker has died
    /// still takes requests and silently applies none of them, until a wake
    /// rebuilds it: a dial that "froze" and came back after a replug.
    /// </summary>
    public static void Raise<T>(Action<T>? handler, T argument, string name)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList())
        {
            Run(name, () => ((Action<T>)subscriber)(argument));
        }
    }

    /// <inheritdoc cref="Raise{T}(Action{T}?, T, string)"/>
    public static void Raise<T1, T2>(Action<T1, T2>? handler, T1 first, T2 second, string name)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList())
        {
            Run(name, () => ((Action<T1, T2>)subscriber)(first, second));
        }
    }

    /// <inheritdoc cref="Raise{T}(Action{T}?, T, string)"/>
    public static void Raise(Action? handler, string name)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList())
        {
            Run(name, (Action)subscriber);
        }
    }
}

/// <summary>
/// An encoder action whose every entry point is guarded; see <see cref="Guard"/>.
/// Actions override the <c>On…</c> methods. The StreamDeck-Tools entry points
/// are sealed so that no handler, now or later, can be added unguarded.
/// </summary>
public abstract class SafeEncoderBase : EncoderBase
{
    protected SafeEncoderBase(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
    }

    public sealed override void DialRotate(DialRotatePayload payload) => Guard.Run($"{GetType().Name}.DialRotate", () => OnDialRotate(payload));

    public sealed override void DialDown(DialPayload payload) => Guard.Run($"{GetType().Name}.DialDown", () => OnDialDown(payload));

    public sealed override void DialUp(DialPayload payload) => Guard.Run($"{GetType().Name}.DialUp", () => OnDialUp(payload));

    public sealed override void TouchPress(TouchpadPressPayload payload) => Guard.Run($"{GetType().Name}.TouchPress", () => OnTouchPress(payload));

    public sealed override void OnTick() => Guard.Run($"{GetType().Name}.OnTick", OnTickGuarded);

    public sealed override void ReceivedSettings(ReceivedSettingsPayload payload) => Guard.Run($"{GetType().Name}.ReceivedSettings", () => OnReceivedSettings(payload));

    public sealed override void ReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload) => Guard.Run($"{GetType().Name}.ReceivedGlobalSettings", () => OnReceivedGlobalSettings(payload));

    public sealed override void Dispose() => Guard.Run($"{GetType().Name}.Dispose", OnDispose);

    protected virtual void OnDialRotate(DialRotatePayload payload)
    {
    }

    protected virtual void OnDialDown(DialPayload payload)
    {
    }

    protected virtual void OnDialUp(DialPayload payload)
    {
    }

    protected virtual void OnTouchPress(TouchpadPressPayload payload)
    {
    }

    protected virtual void OnTickGuarded()
    {
    }

    protected virtual void OnReceivedSettings(ReceivedSettingsPayload payload)
    {
    }

    protected virtual void OnReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload)
    {
    }

    protected virtual void OnDispose()
    {
    }
}

/// <summary>A key action whose every entry point is guarded; see <see cref="SafeEncoderBase"/>.</summary>
public abstract class SafeKeypadBase : KeypadBase
{
    protected SafeKeypadBase(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
    }

    public sealed override void KeyPressed(KeyPayload payload) => Guard.Run($"{GetType().Name}.KeyPressed", () => OnKeyPressed(payload));

    public sealed override void KeyReleased(KeyPayload payload) => Guard.Run($"{GetType().Name}.KeyReleased", () => OnKeyReleased(payload));

    public sealed override void OnTick() => Guard.Run($"{GetType().Name}.OnTick", OnTickGuarded);

    public sealed override void ReceivedSettings(ReceivedSettingsPayload payload) => Guard.Run($"{GetType().Name}.ReceivedSettings", () => OnReceivedSettings(payload));

    public sealed override void ReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload) => Guard.Run($"{GetType().Name}.ReceivedGlobalSettings", () => OnReceivedGlobalSettings(payload));

    public sealed override void Dispose() => Guard.Run($"{GetType().Name}.Dispose", OnDispose);

    protected virtual void OnKeyPressed(KeyPayload payload)
    {
    }

    protected virtual void OnKeyReleased(KeyPayload payload)
    {
    }

    protected virtual void OnTickGuarded()
    {
    }

    protected virtual void OnReceivedSettings(ReceivedSettingsPayload payload)
    {
    }

    protected virtual void OnReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload)
    {
    }

    protected virtual void OnDispose()
    {
    }
}
