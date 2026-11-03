using BarRaider.SdTools;

namespace NowPlaying.Plugin;

/// <summary>
/// Sends one action's feedback in order: one send in flight at a time, each
/// composed from the state as it is when that send starts, and a request that
/// arrives meanwhile folded into one more pass rather than queued.
///
/// What it replaces computed the diff against the last frame under the
/// action's lock, recorded the new frame as sent, and then sent it after the
/// lock was released. Two pushes close together -- the one-second tick and a
/// media event, a dial turn and its own echo -- could leave in the opposite
/// order to the one they were composed in. The deck then kept the older values
/// while the action believed it had the newer ones, and every later diff
/// skipped those fields as unchanged: a title or a piece of art stuck on the
/// strip until something forced a full repaint, such as a USB replug. A send
/// that failed did the same. Here, a failed or overdue send makes the next one
/// a full frame.
/// </summary>
internal sealed class FeedbackPump
{
    /// <summary>
    /// When a send is reported as overdue. The socket below has no deadline of
    /// its own and cannot be cancelled from here, so the send is still waited
    /// out after this -- starting another behind it would only queue it, and a
    /// hung socket would collect one stuck frame per request for as long as it
    /// stayed hung. Past this it is logged, and the next frame goes out whole.
    /// </summary>
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly string _name;
    private readonly Func<bool, Dictionary<string, object>> _compose;
    private readonly Func<Dictionary<string, object>, Task> _send;
    private readonly object _gate = new();
    private bool _running;
    private bool _again;
    private bool _full;
    private bool _retryScheduled;
    private bool _stopped;

    /// <param name="name">For the log.</param>
    /// <param name="compose">
    /// The changes to send, given whether a full frame is wanted. Called on
    /// one pass at a time, so it may record what it returned as sent.
    /// </param>
    /// <param name="send">Sends one payload.</param>
    public FeedbackPump(string name, Func<bool, Dictionary<string, object>> compose, Func<Dictionary<string, object>, Task> send)
    {
        _name = name;
        _compose = compose;
        _send = send;
    }

    /// <summary>Bring the strip up to date with the current state, as soon as any send in flight is done.</summary>
    /// <param name="full">Send everything rather than what changed.</param>
    public void Request(bool full = false)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _full |= full;
            if (_running)
            {
                _again = true;
                return;
            }

            _running = true;
        }

        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            bool full;
            lock (_gate)
            {
                _again = false;
                full = _full;
                _full = false;
            }

            var failed = false;
            var overdue = false;
            try
            {
                var payload = _compose(full);
                if (payload.Count > 0)
                {
                    // Watched: past SendWatchdog.HardLimit the plugin exits for a new
                    // connection, so the wait-out below cannot last for ever.
                    var send = SendWatchdog.Watch(_send(payload), _name);
                    try
                    {
                        await send.WaitAsync(SendTimeout).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        overdue = true;
                        Logger.Instance.LogMessage(TracingLevel.WARN, $"[feedback] {_name}: send overdue; waiting it out, next frame goes out whole");
                        await send.ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                failed = true;
                Logger.Instance.LogMessage(TracingLevel.WARN, $"[feedback] {_name}: {ex.Message}; next frame goes out whole");
            }

            bool retry = false, again;
            lock (_gate)
            {
                if (overdue)
                {
                    _full = true;
                    _again = true; // whatever was asked for meanwhile, plus a whole frame
                }

                if (failed && !_stopped)
                {
                    _full = true;
                    if (!_retryScheduled)
                    {
                        _retryScheduled = true;
                        retry = true;
                    }
                }

                if (_stopped)
                {
                    _again = false;
                }

                again = _again;
                if (!again)
                {
                    _running = false;
                }
            }

            if (retry)
            {
                // Nothing else may ask for a frame for a long while -- a display
                // dial only draws when something changes -- so a failure gets one
                // retry of its own, paced so a dead socket is not spun on.
                _ = Task.Delay(RetryDelay).ContinueWith(_ =>
                {
                    lock (_gate)
                    {
                        _retryScheduled = false;
                    }

                    Request(full: true);
                }, TaskScheduler.Default);
            }

            if (!again)
            {
                return;
            }
        }
    }

    /// <summary>The action has gone: send nothing more, retries included.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _again = false;
        }
    }
}
