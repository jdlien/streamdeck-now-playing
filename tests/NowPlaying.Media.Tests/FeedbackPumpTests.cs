using NowPlaying.Plugin;

namespace NowPlaying.Media.Tests;

/// <summary>
/// The pump that keeps each dial's feedback in order. What it fixed: two pushes
/// composed in one order and sent in the other left the deck showing the older
/// values while the action believed it had sent the newer ones, so the stale
/// fields were never sent again.
/// </summary>
public class FeedbackPumpTests
{
    private static Dictionary<string, object> Frame(int value) => new() { ["value"] = value };

    [Fact]
    public async Task RequestsDuringASendFoldIntoOneMorePassWithTheLatestState()
    {
        var state = 1;
        var sent = new List<int>();
        var inFlight = 0;
        var maxInFlight = 0;
        var gate = new TaskCompletionSource();
        var firstSendStarted = new TaskCompletionSource();

        var pump = new FeedbackPump("test", _ => Frame(Volatile.Read(ref state)), async frame =>
        {
            var now = Interlocked.Increment(ref inFlight);
            maxInFlight = Math.Max(maxInFlight, now);
            lock (sent)
            {
                sent.Add((int)frame["value"]);
            }

            if (sent.Count == 1)
            {
                firstSendStarted.SetResult();
                await gate.Task;
            }

            Interlocked.Decrement(ref inFlight);
        });

        pump.Request();
        await firstSendStarted.Task;

        // Three changes land while the first send is stuck.
        for (var i = 2; i <= 4; i++)
        {
            Volatile.Write(ref state, i);
            pump.Request();
        }

        gate.SetResult();
        await WaitUntil(() => sent.Count >= 2);
        await Task.Delay(50);

        Assert.Equal([1, 4], sent); // the stale middle states are never sent
        Assert.Equal(1, maxInFlight);
    }

    [Fact]
    public async Task AFailedSendMakesTheNextFrameFull()
    {
        var fulls = new List<bool>();
        var attempts = 0;
        var pump = new FeedbackPump("test", full =>
        {
            lock (fulls)
            {
                fulls.Add(full);
            }

            return Frame(1);
        }, _ => Interlocked.Increment(ref attempts) == 1
            ? Task.FromException(new InvalidOperationException("socket closed"))
            : Task.CompletedTask);

        pump.Request();
        await WaitUntil(() => attempts >= 1);
        pump.Request(); // the next ordinary request after the failure

        await WaitUntil(() => fulls.Count >= 2);
        Assert.False(fulls[0]);
        Assert.True(fulls[1]);
    }

    [Fact]
    public async Task AnEmptyDiffSendsNothing()
    {
        var sends = 0;
        var pump = new FeedbackPump("test", _ => [], _ =>
        {
            Interlocked.Increment(ref sends);
            return Task.CompletedTask;
        });

        pump.Request();
        await Task.Delay(50);
        Assert.Equal(0, sends);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition never became true");
            }

            await Task.Delay(10);
        }
    }
}
