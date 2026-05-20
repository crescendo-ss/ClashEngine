using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Produces a <see cref="MatchPayload"/>-shaped envelope for an in-progress match so
/// <see cref="ScoreboardFormatter"/> can render the same table format that's broadcast at
/// match end. Pure -- no I/O, no clock dependency.
/// </summary>
/// <remarks>
/// <para>The synthesized payload describes a snapshot at "now": <c>EndedAt</c> equals the
/// passed-in time, <c>FinalState</c> reads <c>"InProgress"</c>, and <c>Teams</c> is ranked by
/// current kill count (tie-broken by team index). The recorder is read live; counters keep
/// ticking after this call.</para>
/// <para>Lives that are still open are reported with no <c>EndTick</c>/<c>EndReason</c> --
/// callers shouldn't depend on them being closed.</para>
/// </remarks>
public static class LiveScoreboardBuilder
{
    /// <summary>
    /// Build a live envelope. <paramref name="match"/> may be in any state; the formatter only
    /// consumes participant stats and a team ranking. <paramref name="ratingsAtStart"/> mirrors
    /// what <see cref="MatchExporter.Build"/> takes -- pre-match ordinals, frozen at start.
    /// </summary>
    public static MatchPayload Build(
        Guid matchId,
        string? queueName,
        string? queueLabel,
        string gameType,
        string? arena,
        IReadOnlyList<IReadOnlyList<PlayerKey>> teams,
        DateTimeOffset? startedAt,
        StatsRecorder recorder,
        ActiveMatch match,
        DateTimeOffset now,
        IReadOnlyDictionary<PlayerKey, RatingPayload>? ratingsAtStart = null)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(match);

        // Live ranking: by kill count desc, ties broken by stable team index.
        var liveTeams = new List<(int Index, int Score)>(teams.Count);
        for (int t = 0; t < teams.Count; t++) liveTeams.Add((t, match.KillsByTeam[t]));
        liveTeams.Sort((a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Index.CompareTo(b.Index);
        });

        var rankedTeams = new RankedTeam[teams.Count];
        for (int i = 0; i < liveTeams.Count; i++)
        {
            var (idx, score) = liveTeams[i];
            rankedTeams[i] = new RankedTeam(Rank: i + 1, Players: teams[idx], Score: score);
        }

        // Synthesize an outcome to feed MatchExporter; FinalState is overwritten below to
        // distinguish live from finalized payloads.
        var outcome = new MatchOutcome(
            MatchId: matchId,
            GameType: match.GameType,
            RankedTeams: rankedTeams,
            AbandonedBy: Array.Empty<PlayerKey>(),
            FinalState: MatchState.Live,
            EndedAt: now);

        var payload = MatchExporter.Build(
            matchId: matchId,
            queueName: queueName,
            queueLabel: queueLabel,
            gameType: gameType,
            arena: arena,
            teams: teams,
            startedAt: startedAt,
            recorder: recorder,
            outcome: outcome,
            ratingsAtStart: ratingsAtStart,
            recordingPath: null);

        // Override FinalState so consumers (logs, formatter titles) can distinguish live vs final.
        return payload with { FinalState = "InProgress" };
    }
}
