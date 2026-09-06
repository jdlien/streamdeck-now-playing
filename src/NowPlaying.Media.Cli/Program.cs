using System.Text;
using System.Text.Json;
using NowPlaying.Media;

namespace NowPlaying.Media.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static async Task<int> Main(string[] args)
    {
        // Titles carry em dashes and worse; make sure the console can show them.
        Console.OutputEncoding = Encoding.UTF8;

        var command = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "probe";
        var asJson = args.Contains("--json");

        switch (command)
        {
            case "probe":
                return await ProbeAsync(asJson);
            case "watch":
                Console.Error.WriteLine("watch is milestone 1 and is not implemented yet.");
                return 2;
            default:
                Console.Error.WriteLine("usage: nowplaying-cli [probe|watch] [--json]");
                return 2;
        }
    }

    private static async Task<int> ProbeAsync(bool asJson)
    {
        var (currentId, sessions) = await MediaSessionProbe.ReadAllAsync();

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { current = currentId, sessions }, JsonOptions));
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
            var updated = s.LastUpdated.Ticks == 0 ? "never" : s.LastUpdated.ToLocalTime().ToString("HH:mm:ss.fff");
            Console.WriteLine($"  timeline: position={s.Position} start={s.StartTime} end={s.EndTime} lastUpdated={updated}");
        }

        return 0;
    }
}
