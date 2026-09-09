using System.Text;
using System.Text.Json;
using NowPlaying.Media;

namespace NowPlaying.Media.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions PrettyJsonOptions = new(JsonOptions)
    {
        WriteIndented = true,
    };

    private static async Task<int> Main(string[] args)
    {
        // Titles carry em dashes and worse; make sure the console can show them.
        Console.OutputEncoding = Encoding.UTF8;

        var command = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "probe";
        var asJson = args.Contains("--json");
        var ticks = args.Contains("--ticks");

        switch (command)
        {
            case "probe":
                return await ProbeAsync(asJson);
            case "watch":
                return await WatchAsync(asJson, ticks);
            case "volume":
                return await VolumeAsync();
            case "brightness":
                return Brightness(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")));
            case "display":
                return await DisplayAsync(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")));
            default:
                Console.Error.WriteLine("usage: nowplaying-cli [probe|watch|volume|brightness <0-100>|display [<0-100>]] [--json] [--ticks]");
                Console.Error.WriteLine("  probe   list every Windows media session and exit");
                Console.Error.WriteLine("  watch   print a line per snapshot change; reads next/prev/toggle/refresh/quit on stdin");
                Console.Error.WriteLine("  volume  print the default output device's volume; reads up/down/mute/quit on stdin");
                Console.Error.WriteLine("  brightness <0-100>  list Stream Deck + HID paths and set their screen brightness");
                Console.Error.WriteLine("  display [<0-100>]   list monitors with DDC/CI brightness; optionally set the primary monitor's");
                Console.Error.WriteLine("  --ticks in watch mode, also print the extrapolated position once a second while playing");
                return 2;
        }
    }

    // -- probe ------------------------------------------------------------

    private static async Task<int> ProbeAsync(bool asJson)
    {
        var (currentId, sessions) = await MediaSessionProbe.ReadAllAsync();

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { current = currentId, sessions }, PrettyJsonOptions));
            return 0;
        }

        Console.WriteLine($"current session: {currentId ?? "<none>"}");
        Console.WriteLine($"session count: {sessions.Count}");
        foreach (var s in sessions)
        {
            Console.WriteLine();
            Console.WriteLine($"app={s.AppId}{(s.IsCurrent ? "   <- current" : "")}");
            Console.WriteLine($"  status={s.Status} type={s.PlaybackType ?? "?"}");
            Console.WriteLine($"  title='{s.Title}' artist='{s.Artist}' album='{s.AlbumTitle}' thumbnail={s.HasThumbnail}");
            Console.WriteLine($"  controls: next={s.CanNext} prev={s.CanPrevious} toggle={s.CanToggle}");
            var updated = s.LastUpdated <= TimelineMath.WinRtEpoch ? "never" : s.LastUpdated.ToLocalTime().ToString("HH:mm:ss.fff");
            Console.WriteLine($"  timeline: position={s.Position} start={s.StartTime} end={s.EndTime} lastUpdated={updated}");
        }

        return 0;
    }

    // -- watch ------------------------------------------------------------

    private static async Task<int> WatchAsync(bool asJson, bool ticks)
    {
        await using var service = new MediaSessionService(new MediaSessionServiceOptions
        {
            Log = message => Console.Error.WriteLine($"[svc {DateTime.Now:HH:mm:ss.fff}] {message}"),
        });

        service.SnapshotChanged += snapshot => Print("snapshot", snapshot, asJson);
        await service.StartAsync();
        Print("snapshot", service.Current, asJson);

        using var tickTimer = ticks
            ? new Timer(
                _ =>
                {
                    var current = service.Current;
                    if (current.State == PlaybackState.Playing && current.Duration is not null)
                    {
                        Print("tick", current, asJson);
                    }
                },
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1))
            : null;

        Console.Error.WriteLine("commands: next | prev | toggle | refresh | quit");
        while (await Console.In.ReadLineAsync() is { } line)
        {
            var command = line.Trim().ToLowerInvariant();
            switch (command)
            {
                case "":
                    break;
                case "next":
                    Report(command, await service.NextAsync());
                    break;
                case "prev" or "previous":
                    Report(command, await service.PreviousAsync());
                    break;
                case "toggle":
                    Report(command, await service.TogglePlayPauseAsync());
                    break;
                case "refresh":
                    await service.RefreshAsync(full: true);
                    Report(command, true);
                    break;
                case "quit" or "q" or "exit":
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown command: {command}");
                    break;
            }
        }

        return 0;
    }

    // -- volume -----------------------------------------------------------

    private static async Task<int> VolumeAsync()
    {
        using var service = new NowPlaying.Audio.VolumeService(message => Console.Error.WriteLine($"[vol {DateTime.Now:HH:mm:ss.fff}] {message}"));
        service.Changed += PrintVolume;
        service.Start();
        PrintVolume(service.Current);

        Console.Error.WriteLine("commands: up | down | mute | quit   (up/down move 2%)");
        while (await Console.In.ReadLineAsync() is { } line)
        {
            var command = line.Trim().ToLowerInvariant();
            switch (command)
            {
                case "":
                    break;
                case "up":
                    Report(command, service.AdjustBy(0.02f));
                    break;
                case "down":
                    Report(command, service.AdjustBy(-0.02f));
                    break;
                case "mute":
                    Report(command, service.ToggleMute());
                    break;
                case "quit" or "q" or "exit":
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown command: {command}");
                    break;
            }
        }

        return 0;
    }

    // -- brightness -------------------------------------------------------

    private static int Brightness(string? percentText)
    {
        var paths = NowPlaying.Device.StreamDeckHid.FindDevicePaths();
        Console.WriteLine($"Stream Deck + HID interfaces: {paths.Count}");
        foreach (var path in paths)
        {
            Console.WriteLine($"  {path}");
        }

        if (percentText is null)
        {
            return paths.Count > 0 ? 0 : 1;
        }

        if (!int.TryParse(percentText, out var percent) || percent < 0 || percent > 100)
        {
            Console.Error.WriteLine("brightness must be 0..100");
            return 2;
        }

        var applied = NowPlaying.Device.StreamDeckHid.SetBrightnessAll(percent, message => Console.Error.WriteLine($"[hid] {message}"));
        Console.WriteLine($"brightness {percent}% applied to {applied} device(s)");
        return applied > 0 ? 0 : 1;
    }

    // -- display brightness -----------------------------------------------

    private static async Task<int> DisplayAsync(string? percentText)
    {
        var monitors = NowPlaying.Device.MonitorConfiguration.Enumerate(message => Console.Error.WriteLine($"[ddc] {message}"));
        Console.WriteLine($"monitors: {monitors.Count}");
        foreach (var m in monitors)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var (min, current, max) = NowPlaying.Device.MonitorConfiguration.ReadBrightness(m.Handle);
                var percent = NowPlaying.Device.BrightnessMath.ToPercent(min, current, max);
                Console.WriteLine($"  {m.GdiDeviceName}{(m.IsPrimary ? " (primary)" : "")}  '{m.Name}'  brightness {current} of {min}..{max} = {percent}%  ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {m.GdiDeviceName}{(m.IsPrimary ? " (primary)" : "")}  '{m.Name}'  no DDC/CI answer: {ex.Message}");
            }
        }

        NowPlaying.Device.MonitorConfiguration.Destroy(monitors);

        if (percentText is null)
        {
            return monitors.Count > 0 ? 0 : 1;
        }

        if (!int.TryParse(percentText, out var target) || target < 0 || target > 100)
        {
            Console.Error.WriteLine("display brightness must be 0..100");
            return 2;
        }

        using var service = new NowPlaying.Device.DisplayBrightnessService(message => Console.Error.WriteLine($"[ddc] {message}"));
        var done = new TaskCompletionSource<NowPlaying.Device.DisplayBrightnessSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, s) =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] display  {s.Level,3}%  {(s.Dimmed ? "dimmed" : "on    ")}  {s.Name} ({s.Index + 1}/{s.Count})");
            if (s.Available && s.Index == 0)
            {
                done.TrySetResult(s);
            }
        };
        service.Start();
        var bound = await done.Task.WaitAsync(TimeSpan.FromSeconds(12)); // a first bind can fail and re-bind after a backoff
        var delta = target - bound.Level;
        if (delta == 0)
        {
            Console.WriteLine($"already at {target}%; nothing written");
            return 0;
        }

        Console.WriteLine($"adjusting primary by {delta:+#;-#;0} to {target}%");
        service.Adjust(null, delta);
        await Task.Delay(2500); // let the write land and the bus settle before reading back
        var check = NowPlaying.Device.MonitorConfiguration.Enumerate();
        try
        {
            var (_, after, _) = NowPlaying.Device.MonitorConfiguration.ReadBrightness(check.First(m => m.IsPrimary).Handle);
            Console.WriteLine($"monitor now reports {after} units");
        }
        finally
        {
            NowPlaying.Device.MonitorConfiguration.Destroy(check);
        }

        return 0;
    }

    private static void PrintVolume(NowPlaying.Audio.VolumeSnapshot volume) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] volume  {volume.Percent,3}%  {(volume.Muted ? "muted " : "live  ")} {volume.DeviceName}");

    private static void Report(string command, bool accepted) =>
        Console.Error.WriteLine($"[cmd {DateTime.Now:HH:mm:ss.fff}] {command}: {(accepted ? "accepted" : "rejected")}");

    private static void Print(string kind, NowPlayingSnapshot snapshot, bool asJson)
    {
        var now = DateTimeOffset.UtcNow;
        var position = snapshot.EffectivePosition(now);

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                @event = kind,
                at = now.ToLocalTime(),
                appId = snapshot.AppId,
                state = snapshot.State.ToString(),
                title = snapshot.Title,
                artist = snapshot.Artist,
                position,
                duration = snapshot.Duration,
                canNext = snapshot.CanNext,
                canPrevious = snapshot.CanPrevious,
                canToggle = snapshot.CanToggle,
            }, JsonOptions));
            return;
        }

        var progress = position is { } p && snapshot.Duration is { } d ? $"{Clock(p)}/{Clock(d)}" : "--:--";
        var text = snapshot.State == PlaybackState.None
            ? "No media"
            : $"{snapshot.Artist} · {snapshot.Title}".Trim(' ', '·');
        Console.WriteLine($"[{now.ToLocalTime():HH:mm:ss}] {kind,-8} {snapshot.State,-8} {progress,-11} {text}  ({snapshot.AppId ?? "-"})");
    }

    private static string Clock(TimeSpan value) => $"{(int)value.TotalMinutes}:{value.Seconds:00}";
}
