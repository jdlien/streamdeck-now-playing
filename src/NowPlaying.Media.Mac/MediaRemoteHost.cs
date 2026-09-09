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
/// The provider named an owning app but would not give up its metadata. Music.app
/// does this: the client and playing-state calls answer, the metadata call never
/// calls back at all. The router treats it as "ask that app directly".
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

    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();

    private Process? _process;
    private Task? _supervisor;
    private TimeSpan _restartDelay = RestartDelay;

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
            try
            {
                await RunOnceAsync(helper).ConfigureAwait(false);
                _restartDelay = RestartDelay;   // it ran; treat the next failure as fresh
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
        // Nothing else is asked of the host process.
        var loader = $"use DynaLoader; DynaLoader::dl_load_file('{helperPath.Replace("'", "\\'")}', 0x01);";
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

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
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
