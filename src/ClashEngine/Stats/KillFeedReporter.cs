using System;
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
/// Composes the in-arena kill-feed line(s) for a match kill and broadcasts them to the arena.
/// Reads attribution from <see cref="StatsRecorder"/> (must be called <em>before</em> the
/// recorder clears its recovery state) and pulls scoreline / lives-remaining state from the
/// engine's <see cref="ActiveMatch"/>.
/// </summary>
public sealed class KillFeedReporter
{
    private readonly IChat _chat;
    private readonly MatchmakingEngine _engine;
    private readonly IClock _clock;

    public KillFeedReporter(IChat chat, MatchmakingEngine engine, IClock clock)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
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

        // Line 1: "Victim kb Killer (dmg) -- Add: A1 (d1), A2 (d2)"
        var line1 = new StringBuilder();
        line1.Append(victim.Name).Append(" kb ").Append(killer.Name)
             .Append(" (").Append(snapshot.KillerDamage).Append(')');
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
        _chat.SendArenaMessage(arena, line1.ToString());

        // Line 2 (optional): lives remaining for the victim, only when the match tracks lives.
        if (match.LivesPerPlayer.HasValue
            && match.LivesRemaining.TryGetValue(victim, out var lives)
            && lives > 0)
        {
            string suffix = lives == 1 ? "life" : "lives";
            _chat.SendArenaMessage(arena, $"{victim.Name} has {lives} {suffix} remaining");
        }

        // Line 3: "Score: a-b TEAM_NAME -- [m:ss]" with TEAM_NAME = killer's team.
        var (killerScore, victimScore) = FormatScore(match, killer, victim);
        var killerTeamLabel = LabelOfTeamContaining(match, killer);
        var elapsed = _clock.UtcNow - (match.StartedAt ?? _clock.UtcNow);
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        _chat.SendArenaMessage(arena,
            $"Score: {killerScore}-{victimScore} {killerTeamLabel} -- [{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}]");
    }

    private static (int KillerScore, int VictimScore) FormatScore(ActiveMatch match, PlayerKey killer, PlayerKey victim)
    {
        int killerTeam = TeamOf(match, killer);
        int victimTeam = TeamOf(match, victim);
        int kScore = killerTeam >= 0 && killerTeam < match.KillsByTeam.Count ? match.KillsByTeam[killerTeam] : 0;
        int vScore = victimTeam >= 0 && victimTeam < match.KillsByTeam.Count ? match.KillsByTeam[victimTeam] : 0;
        return (kScore, vScore);
    }

    private static int TeamOf(ActiveMatch match, PlayerKey key)
    {
        for (int t = 0; t < match.Teams.Count; t++)
            for (int j = 0; j < match.Teams[t].Count; j++)
                if (match.Teams[t][j].Equals(key)) return t;
        return -1;
    }

    private static string LabelOfTeamContaining(ActiveMatch match, PlayerKey key)
    {
        int t = TeamOf(match, key);
        if (t < 0) return "?";
        var team = match.Teams[t];
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
