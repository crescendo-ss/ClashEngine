using System;
using SS.Core;

namespace ClashEngine.Adapter;

/// <summary>
/// Sink for server-originated arena chat lines that ClashEngine broadcasts to a match's
/// audience (countdown ticks, "GO!", the ship-lock notice, team-collapse/recover, the
/// end-of-match summary, scoreboard and kill-feed). Implemented by the replay recorder so
/// these lines land in the per-match <c>.replay</c> file; <see cref="MatchAudience"/> drives
/// it from the single broadcast choke point so each line is recorded exactly once and is
/// inherently scoped to the right match (no cross-match bleed in multi-match arenas).
/// </summary>
public interface IMatchChatRecorder
{
    /// <summary>Record one arena chat line against <paramref name="matchId"/>'s recording
    /// session. A no-op if no active session exists for the match.</summary>
    void RecordArenaMessage(Guid matchId, ReadOnlySpan<char> message, ChatSound sound);
}
