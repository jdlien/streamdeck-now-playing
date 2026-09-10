using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NowPlaying.Media;

/// <summary>
/// Reads a JSON boolean that may have arrived as a number. Objective-C boxes a
/// plain C expression as a number rather than a boolean, so 0 and 1 turn up
/// where true and false were meant. The helper is fixed, but tolerating both
/// keeps one careless @(...) from taking the whole media feature down.
/// </summary>
internal sealed class LenientBooleanConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number => reader.GetDouble() != 0,
            JsonTokenType.String => bool.TryParse(reader.GetString(), out var parsed) && parsed,
            _ => false,
        };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}

/// <summary>What the helper reports about the current session.</summary>
/// <param name="Bundle">Bundle id of the app that owns the session, or null when nothing does.</param>
/// <param name="Stale">
/// The provider named an owning app but would not give up its metadata: the
/// client and playing-state calls answer, the metadata call does not come back.
/// The router treats it as "ask that app directly". Music.app used to be a
/// standing example and no longer is; see the helper's header for why.
/// </param>
internal sealed record MediaRemoteState(
    [property: JsonPropertyName("bundle")] string? Bundle,
    [property: JsonPropertyName("stale"), JsonConverter(typeof(LenientBooleanConverter))] bool Stale,
    [property: JsonPropertyName("playing"), JsonConverter(typeof(LenientBooleanConverter))] bool Playing,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("artist")] string? Artist,
    [property: JsonPropertyName("album")] string? Album,
    [property: JsonPropertyName("duration")] double? Duration,
    [property: JsonPropertyName("elapsed")] double? Elapsed,
    [property: JsonPropertyName("elapsedAt")] double? ElapsedAt,
    [property: JsonPropertyName("rate")] double? Rate,
    [property: JsonPropertyName("artwork")] string? ArtworkKey);

/// <summary>
/// Supervises the MediaRemote helper.
///
/// The helper is a dylib loaded into <c>/usr/bin/perl</c>, because macOS 15.4
/// stopped answering MediaRemote for anything but an Apple platform binary. That
/// makes a child process unavoidable, which is the one place this plugin accepts
/// IPC (macos-port-plan D3): loading the dylib is enough to start it, so the host
/// needs no cooperation and no perl is written beyond the one-line loader.
///
/// The process is restarted if it dies, with a backoff, because the whole media
/// feature depends on it.
/// </summary>
internal sealed class MediaRemoteHost : IAsyncDisposable
{
    private const string PerlPath = "/usr/bin/perl";
    private const string HelperName = "nowplaying-mediaremote.dylib";

    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRestartDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the helper may say nothing before it is presumed wedged. It
    /// ticks every 15 s, so four missed ticks. Generous on purpose: killing a
    /// healthy helper costs a reconnect and a re-read, and the fallback to
    /// Music covers the gap either way.
    /// </summary>
    internal static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(10);

    /// <summary>A run shorter than this is a failing run; do not clear the backoff.</summary>
    private static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(2);

    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();

    private Process? _process;
    private Task? _supervisor;
    private TimeSpan _restartDelay = RestartDelay;
    private long _lastMessageTicks = DateTime.UtcNow.Ticks;

    public MediaRemoteHost(Action<string>? log = null) => _log = log;

    /// <summary>The helper published a new state.</summary>
    public event Action<MediaRemoteState>? StateChanged;

    /// <summary>Artwork arrived, keyed by the content hash the state carried.</summary>
    public event Action<string, byte[]>? ArtworkReceived;

    /// <summary>Whether the helper is up. False while it restarts.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>
    /// Whether the helper is up <em>and answering</em>.
    ///
    /// Not the same question as <see cref="IsRunning"/>, and the difference is
    /// the whole point: MediaRemote can wedge the helper's queues while the
    /// process stays alive, connected and silent forever. Callers deciding
    /// whether MediaRemote can still be relied on want this one.
    /// </summary>
    public bool IsHealthy => IsAnswering(
        IsRunning,
        TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastMessageTicks)));

    /// <summary>The rule behind <see cref="IsHealthy"/>, without a clock.</summary>
    internal static bool IsAnswering(bool running, TimeSpan silence) =>
        running && silence < SilenceLimit;

    /// <summary>The helper next to this assembly, or null when it was not deployed.</summary>
    public static string? HelperPath
    {
        get
        {
            var directory = Path.GetDirectoryName(typeof(MediaRemoteHost).Assembly.Location);
            if (directory is null)
            {
                return null;
            }

            var path = Path.Combine(directory, HelperName);
            return File.Exists(path) ? path : null;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            _supervisor ??= Task.Run(SuperviseAsync);
        }
    }

    /// <summary>Send a command. Silently does nothing when the helper is down.</summary>
    public void Send(string command)
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
        }

        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            process.StandardInput.WriteLine(command);
            process.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"sending '{command}' failed: {ex.Message}");
        }
    }

    private async Task SuperviseAsync()
    {
        var helper = HelperPath;
        if (helper is null)
        {
            _log?.Invoke($"{HelperName} is missing; no system-wide media on this install");
            return;
        }

        while (!_cancellation.IsCancellationRequested)
        {
            var startedAt = DateTimeOffset.UtcNow;
            try
            {
                await RunOnceAsync(helper).ConfigureAwait(false);
                // Only a run that lasted counts as a good one. A helper that
                // wedges immediately would otherwise reset the backoff every
                // time the watchdog killed it and restart in a tight loop.
                if (DateTimeOffset.UtcNow - startedAt >= HealthyRun)
                {
                    _restartDelay = RestartDelay;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"helper failed: {ex.Message}");
                _restartDelay = TimeSpan.FromTicks(Math.Min(_restartDelay.Ticks * 2, MaxRestartDelay.Ticks));
            }

            if (_cancellation.IsCancellationRequested)
            {
                return;
            }

            _log?.Invoke($"helper exited; restarting in {_restartDelay.TotalSeconds:0}s");
            try
            {
                await Task.Delay(_restartDelay, _cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunOnceAsync(string helperPath)
    {
        // Perl's DynaLoader runs the dylib's constructor on load; 0x01 is RTLD_LAZY.
        //
        // The sleep is what keeps the host alive. The constructor returns
        // immediately and hands its work to a queue of its own, because dyld
        // holds the loader lock for the whole of an initializer and a
        // constructor that never returns deadlocks every dlopen in the process
        // -- including the one ImageIO does, lazily, the first time MediaRemote
        // decodes artwork. So the helper cannot block here, and perl blocks
        // instead. The loop is for the signal case: sleep returns early when one
        // arrives.
        var quoted = helperPath.Replace("'", "\\'");
        var loader = $"use DynaLoader; die \"load failed\\n\" unless " +
                     $"DynaLoader::dl_load_file('{quoted}', 0x01); sleep 3600 while 1;";
        var startInfo = new ProcessStartInfo(PerlPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(loader);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start the helper host");

        lock (_gate)
        {
            _process = process;
        }

        _ = Task.Run(async () =>
        {
            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error))
            {
                _log?.Invoke($"helper stderr: {error.Trim()}");
            }
        });

        Interlocked.Exchange(ref _lastMessageTicks, DateTime.UtcNow.Ticks);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
        var watchdog = Task.Run(() => WatchAsync(process, run.Token));

        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                Handle(line);
            }
        }
        finally
        {
            await run.CancelAsync().ConfigureAwait(false);
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            lock (_gate)
            {
                _process = null;
            }

            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone between the check and the kill.
                }
            }
        }
    }

    /// <summary>
    /// Kill a helper that has stopped answering.
    ///
    /// Exiting is not the failure this guards against -- the supervisor already
    /// restarts an exited helper. The failure is the process that stays up and
    /// goes quiet, which is what a deadlock inside MediaRemote looks like from
    /// out here. Killing it turns that into an exit, which is recoverable.
    /// </summary>
    private async Task WatchAsync(Process process, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HealthCheckInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (process.HasExited)
            {
                return;
            }

            var silence = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastMessageTicks));
            if (silence < SilenceLimit)
            {
                continue;
            }

            _log?.Invoke($"helper has said nothing for {silence.TotalSeconds:0}s; restarting it");
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }
    }

    private void Handle(string line)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(line).RootElement;
        }
        catch (JsonException)
        {
            _log?.Invoke($"helper sent unparseable output: {Truncate(line)}");
            return;
        }

        // Any line at all is proof the helper's queue is still draining.
        Interlocked.Exchange(ref _lastMessageTicks, DateTime.UtcNow.Ticks);

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "tick":
                break;   // liveness only; recorded above

            case "hello":
                _log?.Invoke($"helper up (pid {(root.TryGetProperty("pid", out var pid) ? pid.GetRawText() : "?")})");
                break;

            case "now":
                MediaRemoteState? state;
                try
                {
                    state = JsonSerializer.Deserialize<MediaRemoteState>(line);
                }
                catch (JsonException ex)
                {
                    // One bad message is not worth tearing the helper down and
                    // restarting it, which is what an exception here used to do.
                    _log?.Invoke($"helper sent a state this build cannot read: {ex.Message}");
                    break;
                }

                if (state is not null)
                {
                    StateChanged?.Invoke(state);
                }

                break;

            case "artwork":
                if (root.TryGetProperty("key", out var key) && root.TryGetProperty("data", out var data))
                {
                    try
                    {
                        ArtworkReceived?.Invoke(key.GetString() ?? "", Convert.FromBase64String(data.GetString() ?? ""));
                    }
                    catch (FormatException)
                    {
                        _log?.Invoke("helper sent artwork that was not valid base64");
                    }
                }

                break;

            case "fatal":
                _log?.Invoke($"helper fatal: {(root.TryGetProperty("error", out var e) ? e.GetString() : "unknown")}");
                break;
        }
    }

    private static string Truncate(string value) => value.Length <= 120 ? value : value[..120] + "…";

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        Send("quit");

        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is not null && !process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (_supervisor is not null)
        {
            try
            {
                await _supervisor.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cancellation.Dispose();
    }
}
