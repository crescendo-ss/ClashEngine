using System;
using System.Collections.Generic;
using System.Text;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Stats;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// Composes the in-arena kill-feed line(s) for a match kill and broadcasts them to the
/// match-scoped audience (participants + spectators with the match in focus). Reads attribution
/// from <see cref="StatsRecorder"/> (must be called <em>before</em> the recorder clears its
/// recovery state) and pulls scoreline / lives-remaining state from the engine's
/// <see cref="ActiveMatch"/>.
/// </summary>
/// <remarks>
/// Uses <see cref="MatchAudience"/> rather than <c>SendArenaMessage</c> so concurrent matches in
/// the same arena (via SS.Matchmaking.MatchFocus) don't bleed kill lines across matches.
/// </remarks>
public sealed class KillFeedReporter
{
    private readonly IChat _chat;
    private readonly MatchmakingEngine _engine;
    private readonly IClock _clock;
    private readonly PlayerKeyResolver? _resolver;
    private readonly MatchAudience? _audience;

    public KillFeedReporter(
        IChat chat,
        MatchmakingEngine engine,
        IClock clock,
        PlayerKeyResolver? resolver = null,
        MatchAudience? audience = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _resolver = resolver;
        _audience = audience;
    }

    /// <summary>
    /// Broadcast a kill-feed line for a kill that just landed in <paramref name="arena"/>.
    /// No-op if the victim is not part of an active match.
    /// </summary>
    public void Report(
        Arena? arena,
        Guid matchId,
        PlayerKey victim,
        PlayerKey killer,
        KillFeedSnapshot snapshot)
    {
        if (arena is null) return;
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_engine.ActiveMatches.TryGetValue(matchId, out var match)) return;

        // Resolve the participant set once -- every line below targets the same recipients.
        var participants = ResolveParticipants(match);

        // Compute team relationship up-front so Line 1 can flag team kills and Line 3 can
        // attribute the score correctly.
        int killerTeam = TeamOf(match, killer);
        int victimTeam = TeamOf(match, victim);
        bool isTeamkill = killerTeam >= 0 && killerTeam == victimTeam;

        // Line 1: "Victim kb Killer (dmg) [TEAMKILL] -- Add: A1 (d1), A2 (d2)". The "[TEAMKILL]"
        // marker matches SS.Matchmaking's TeamVersusStats convention so chat readers familiar
        // with that format recognize friendly-fire kills at a glance.
        var line1 = new StringBuilder();
        line1.Append(victim.Name).Append(" kb ").Append(killer.Name)
             .Append(" (").Append(snapshot.KillerDamage).Append(')');
        if (isTeamkill) line1.Append(" [TEAMKILL]");
        if (snapshot.Assisters.Count > 0)
        {
            line1.Append(" -- Add: ");
            for (int i = 0; i < snapshot.Assisters.Count; i++)
            {
                if (i > 0) line1.Append(", ");
                var a = snapshot.Assisters[i];
                line1.Append(a.Player.Name).Append(" (").Append(a.Damage).Append(')');
            }
        }
        Send(matchId, arena, participants, line1.ToString());

        // Line 2 (optional): lives status for the victim. KillFeedReporter is a pre-engine
        // reader, so LivesRemaining still holds the count BEFORE this death is processed:
        //   - lives > 0  -> the victim respawns; pre-decrement count happens to equal the
        //                   user-friendly "lives left after this death" (current life +
        //                   remaining respawns), so display verbatim.
        //   - lives == 0 -> the engine is about to set ExitedAt; this is the eliminating
        //                   death. Captains/TVM convention: "X is OUT".
        if (match.LivesPerPlayer.HasValue && match.LivesRemaining.TryGetValue(victim, out var lives))
        {
            if (lives > 0)
            {
                string suffix = lives == 1 ? "life" : "lives";
                Send(matchId, arena, participants, $"{victim.Name} has {lives} {suffix} remaining");
            }
            else if (!match.ExitedAt.ContainsKey(victim))
            {
                Send(matchId, arena, participants, $"{victim.Name} is OUT");
            }
        }

        // Line 3: "Score: a-b TEAM_NAME -- [m:ss]". The labeled team is whichever just scored
        // (so the same kill never reads as 0-0). For a normal kill that's the killer's team;
        // for a 2-team teamkill it's the opposing team (which gets the anti-grief point); for
        // 3+ team TKs no team scored, fall back to killer's team for a stable label.
        //
        // KillFeedReporter runs as a pre-engine reader (StatsListener.OnKill registers in
        // MatchKillRouter._preEngineReaders), so match.KillsByTeam still holds the PRE-kill
        // counts. We predict the post-engine score by adding +1 to whichever team is about to
        // score. The 2-team TK gating mirrors the engine fix in ActiveMatch.OnKill.
        bool scored = !isTeamkill || match.Teams.Count == 2;
        int scoringTeam = (!isTeamkill || match.Teams.Count != 2)
            ? killerTeam
            : (killerTeam == 0 ? 1 : 0);
        int otherTeam = (scoringTeam == 0) ? 1 : 0;

        int scoringPre = (scoringTeam >= 0 && scoringTeam < match.KillsByTeam.Count) ? match.KillsByTeam[scoringTeam] : 0;
        int scoringScore = scoringPre + (scored ? 1 : 0);
        int otherScore = (otherTeam >= 0 && otherTeam < match.KillsByTeam.Count) ? match.KillsByTeam[otherTeam] : 0;
        string scoringLabel = LabelOfTeam(match, scoringTeam);

        var elapsed = _clock.UtcNow - (match.StartedAt ?? _clock.UtcNow);
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        Send(matchId, arena, participants,
            $"Score: {scoringScore}-{otherScore} {scoringLabel} -- [{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}]");
    }

    private List<Player> ResolveParticipants(ActiveMatch match)
    {
        var list = new List<Player>();
        if (_resolver is null) return list;
        for (int t = 0; t < match.Teams.Count; t++)
        {
            for (int j = 0; j < match.Teams[t].Count; j++)
            {
                if (_resolver.Resolve(match.Teams[t][j]) is { } p) list.Add(p);
            }
        }
        return list;
    }

    private void Send(Guid matchId, Arena arena, List<Player> participants, string message)
    {
        if (_audience is not null)
            _audience.Broadcast(matchId, arena.Name, participants, message);
        else
            _chat.SendArenaMessage(arena, message);
    }

    private static int TeamOf(ActiveMatch match, PlayerKey key)
    {
        for (int t = 0; t < match.Teams.Count; t++)
            for (int j = 0; j < match.Teams[t].Count; j++)
                if (match.Teams[t][j].Equals(key)) return t;
        return -1;
    }

    private static string LabelOfTeam(ActiveMatch match, int teamIdx)
    {
        if (teamIdx < 0 || teamIdx >= match.Teams.Count) return "?";
        var team = match.Teams[teamIdx];
        if (team.Count == 0) return "?";
        if (team.Count == 1) return team[0].Name;
        var sb = new StringBuilder();
        for (int i = 0; i < team.Count; i++)
        {
            if (i > 0) sb.Append('/');
            sb.Append(team[i].Name);
        }
        return sb.ToString();
    }
}
