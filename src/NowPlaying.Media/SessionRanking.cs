namespace NowPlaying.Media;

/// <summary>The cheap facts about a session that ranking needs: no metadata.</summary>
public sealed record SessionFacts(string AppId, SessionStatus Status, bool IsCurrent);

/// <summary>
/// Which session gets the display (README section 5.3). Rank rather than
/// trust the manager's "current" session: Windows reports an app that is
/// merely open with nothing loaded as current while another app holds the
/// track. This rule and its regression case come from the ak820-pro agent.
/// </summary>
public static class SessionRanking
{
    /// <summary>
    /// Lower is better; null means not a candidate. Playing beats Paused
    /// beats Stopped. Closed, Opened, Changing, and unreadable sessions are
    /// never shown.
    /// </summary>
    public static int? Rank(SessionStatus status) => status switch
    {
        SessionStatus.Playing => 0,
        SessionStatus.Paused => 1,
        SessionStatus.Stopped => 2,
        _ => null,
    };

    /// <summary>
    /// The session to show, or null when nothing is worth showing. A preferred
    /// app wins outright when it is a candidate; then rank; then the manager's
    /// current session; then the manager's own order (first wins).
    /// </summary>
    public static SessionFacts? Choose(IEnumerable<SessionFacts> sessions, string? preferredAppId = null)
    {
        SessionFacts? best = null;
        var bestKey = (int.MaxValue, int.MaxValue, int.MaxValue);

        foreach (var session in sessions)
        {
            if (Rank(session.Status) is not { } rank)
            {
                continue;
            }

            var key = (
                preferredAppId is not null && session.AppId == preferredAppId ? 0 : 1,
                rank,
                session.IsCurrent ? 0 : 1);

            // Strictly less: equal keys keep the earlier session.
            if (best is null || key.CompareTo(bestKey) < 0)
            {
                best = session;
                bestKey = key;
            }
        }

        return best;
    }
}
