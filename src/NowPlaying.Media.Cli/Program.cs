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
            default:
                Console.Error.WriteLine("usage: nowplaying-cli [probe|watch|volume] [--json] [--ticks]");
                Console.Error.WriteLine("  probe   list every Windows media session and exit");
                Console.Error.WriteLine("  watch   print a line per snapshot change; reads next/prev/toggle/refresh/quit on stdin");
                Console.Error.WriteLine("  volume  print the default output device's volume; reads up/down/mute/quit on stdin");
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
