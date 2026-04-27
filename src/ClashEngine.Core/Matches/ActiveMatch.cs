using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Matches;

/// <summary>
/// In-flight match. Owns the per-match FSM (Forming -> Live -> Completed/Abandoned/Cancelled)
/// and per-player participation FSM (Pending -> Active -> InGrace -> Abandoned).
/// </summary>
/// <remarks>
/// Single-threaded by design. The engine drives this on the mainloop thread. Methods are no-ops
/// for unknown players or invalid transitions -- callers should not throw on bogus events.
/// </remarks>
public sealed class ActiveMatch
{
    private readonly Dictionary<PlayerKey, PlayerStatus> _status = new();
    private readonly Dictionary<PlayerKey, DateTimeOffset> _leftAt = new();
    private readonly Dictionary<PlayerKey, int> _teamOf = new();
    private readonly Dictionary<PlayerKey, int> _killsByPlayer = new();
    private readonly Dictionary<PlayerKey, int> _deathsByPlayer = new();
    private readonly Dictionary<PlayerKey, int> _teamkillsByPlayer = new();
    private readonly Dictionary<PlayerKey, int> _livesRemaining = new();
    private readonly Dictionary<PlayerKey, DateTimeOffset> _exitedAt = new();
    private readonly Dictionary<PlayerKey, List<ParticipationPeriod>> _participations = new();
    private readonly HashSet<PlayerKey> _candidateAbandoners = new();
    private readonly Dictionary<int, DateTimeOffset> _teamCollapsedSince = new();
    private readonly int[] _killsByTeam;
    private readonly IMatchEndPolicy _endPolicy;

    public ActiveMatch(
        Guid matchId,
        GameTypeId gameType,
        IReadOnlyList<IReadOnlyList<PlayerKey>> teams,
        IMatchEndPolicy endPolicy,
        TimeSpan joinTimeout,
        TimeSpan graceWindow,
        DateTimeOffset proposedAt,
        int? livesPerPlayer = null,
        TimeSpan? teamCollapseGrace = null)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(endPolicy);
        if (teams.Count < 2)
            throw new ArgumentException("Need at least 2 teams.", nameof(teams));
        if (joinTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(joinTimeout), "Must be positive.");
        if (graceWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(graceWindow), "Must be positive.");
        if (livesPerPlayer is { } lives && lives < 1)
            throw new ArgumentOutOfRangeException(nameof(livesPerPlayer), "Must be >= 1 when specified.");
        if (teamCollapseGrace is { } tcg && tcg < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(teamCollapseGrace), "Must be non-negative.");

        MatchId = matchId;
        GameType = gameType;
        Teams = teams;
        _endPolicy = endPolicy;
        JoinTimeout = joinTimeout;
        GraceWindow = graceWindow;
        ProposedAt = proposedAt;
        LivesPerPlayer = livesPerPlayer;
        TeamCollapseGrace = teamCollapseGrace ?? TimeSpan.FromSeconds(10);
        State = MatchState.Forming;
        _killsByTeam = new int[teams.Count];

        for (int t = 0; t < teams.Count; t++)
        {
            for (int j = 0; j < teams[t].Count; j++)
            {
                var p = teams[t][j];
                if (p.IsDefault)
                    throw new ArgumentException("All players must have a non-default key.", nameof(teams));
                if (_teamOf.ContainsKey(p))
                    throw new ArgumentException($"Player {p} appears in multiple teams.", nameof(teams));
                _teamOf[p] = t;
                _status[p] = PlayerStatus.Pending;
                if (livesPerPlayer.HasValue) _livesRemaining[p] = livesPerPlayer.Value;
            }
        }
    }

    public Guid MatchId { get; }
    public GameTypeId GameType { get; }
    public IReadOnlyList<IReadOnlyList<PlayerKey>> Teams { get; }
    public MatchState State { get; private set; }
    public DateTimeOffset ProposedAt { get; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public TimeSpan JoinTimeout { get; }
    public TimeSpan GraceWindow { get; }

    /// <summary>
    /// How long an entire team can be without any Active or Pending players before forfeiting.
    /// Distinct from <see cref="GraceWindow"/>, which is per-player. Default 10s -- gives a team
    /// time to recover from simultaneous disconnects without waiting for individual graces.
    /// </summary>
    public TimeSpan TeamCollapseGrace { get; }

    /// <summary>
    /// Lives each player starts with. <see langword="null"/> = unlimited (no exit-time tracking).
    /// </summary>
    public int? LivesPerPlayer { get; }

    public IReadOnlyList<int> KillsByTeam => _killsByTeam;
    public IReadOnlyDictionary<PlayerKey, int> KillsByPlayer => _killsByPlayer;
    public IReadOnlyDictionary<PlayerKey, int> DeathsByPlayer => _deathsByPlayer;
    public IReadOnlyDictionary<PlayerKey, int> TeamkillsByPlayer => _teamkillsByPlayer;

    /// <summary>Current lives remaining per player. Empty when <see cref="LivesPerPlayer"/> is null.</summary>
    public IReadOnlyDictionary<PlayerKey, int> LivesRemaining => _livesRemaining;

    /// <summary>
    /// Wall-clock time at which a player exhausted their last life. Only populated for players
    /// whose lives reached 0; absent for players still alive at end of match (or for matches
    /// without configured lives).
    /// </summary>
    public IReadOnlyDictionary<PlayerKey, DateTimeOffset> ExitedAt => _exitedAt;

    public IReadOnlyDictionary<PlayerKey, PlayerStatus> Statuses => _status;
    public MatchOutcome? Outcome { get; private set; }

    /// <summary>
    /// Per-team timestamp marking when the team most recently lost all live members. A team has
    /// <see cref="TeamCollapseGrace"/> from this moment to recover before forfeiting. Cleared when
    /// any teammate returns. Useful for surfacing forfeit warnings to participants.
    /// </summary>
    public IReadOnlyDictionary<int, DateTimeOffset> TeamCollapsedSince => _teamCollapsedSince;

    /// <summary>
    /// How long this player meaningfully participated: from <see cref="StartedAt"/> until
    /// they exhausted lives, or until <paramref name="now"/> if they're still alive.
    /// Returns <see langword="null"/> if the match never went live, the player was never on a
    /// team, or no lives are configured.
    /// </summary>
    public TimeSpan? GetParticipationTime(PlayerKey player, DateTimeOffset now)
    {
        if (StartedAt is null) return null;
        if (!_teamOf.ContainsKey(player)) return null;
        if (LivesPerPlayer is null) return null;

        var until = _exitedAt.TryGetValue(player, out var exited) ? exited : (EndedAt ?? now);
        var span = until - StartedAt.Value;
        return span < TimeSpan.Zero ? TimeSpan.Zero : span;
    }

    /// <summary>
    /// All present-in-arena windows for <paramref name="player"/>, in chronological order.
    /// A new period opens on each Pending->Active or InGrace->Active transition; it closes on
    /// the next Active->InGrace transition or when the match ends.
    /// </summary>
    public IReadOnlyList<ParticipationPeriod> GetParticipations(PlayerKey player) =>
        _participations.TryGetValue(player, out var list) ? list : Array.Empty<ParticipationPeriod>();

    /// <summary>First time the player became Active in this match, or null if they never did.</summary>
    public DateTimeOffset? EnteredAt(PlayerKey player)
    {
        if (!_participations.TryGetValue(player, out var list) || list.Count == 0) return null;
        return list[0].Enter;
    }

    /// <summary>Final time the player departed without returning, or null if they're still active or never entered.</summary>
    public DateTimeOffset? FinalDepartureAt(PlayerKey player)
    {
        if (!_participations.TryGetValue(player, out var list) || list.Count == 0) return null;
        var last = list[^1];
        return last.Exit;
    }

    /// <summary>
    /// Sum of all closed participation periods, plus the open period (if any) extended to
    /// <paramref name="now"/>. Excludes time spent in InGrace or Abandoned.
    /// </summary>
    public TimeSpan GetTotalActiveTime(PlayerKey player, DateTimeOffset now)
    {
        if (!_participations.TryGetValue(player, out var list) || list.Count == 0)
            return TimeSpan.Zero;

        TimeSpan total = TimeSpan.Zero;
        for (int i = 0; i < list.Count; i++)
        {
            var period = list[i];
            var end = period.Exit ?? now;
            var span = end - period.Enter;
            if (span > TimeSpan.Zero) total += span;
        }
        return total;
    }

    public PlayerStatus GetStatus(PlayerKey player) =>
        _status.TryGetValue(player, out var s) ? s : PlayerStatus.Pending;

    public int? TeamIndexOf(PlayerKey player) =>
        _teamOf.TryGetValue(player, out var t) ? t : null;

    /// <summary>Player has entered the arena and joined their assigned ship/freq.</summary>
    public void OnPlayerJoined(PlayerKey player, DateTimeOffset at)
    {
        if (State != MatchState.Forming) return;
        if (!_status.TryGetValue(player, out var s) || s != PlayerStatus.Pending) return;

        _status[player] = PlayerStatus.Active;
        OpenParticipation(player, at);

        if (AllActive())
        {
            State = MatchState.Live;
            StartedAt = at;
        }
    }

    /// <summary>
    /// Player left the arena, switched to spec, or disconnected. The server cannot reliably tell
    /// these apart, so every departure is treated uniformly. During Live the player goes into
    /// grace and has <see cref="GraceWindow"/> to return -- failure to return is recorded as
    /// abandonment (subject to the candidate rule: lives remaining + at least one viable
    /// teammate). During Forming there is no grace: leaving before the match starts is immediate
    /// abandonment, since pre-match bailers strand their teammates waiting for the join-timeout.
    /// </summary>
    public void OnPlayerLeft(PlayerKey player, DateTimeOffset at)
    {
        if (!_status.TryGetValue(player, out var s)) return;

        if (State == MatchState.Forming)
        {
            if (s == PlayerStatus.Active)
            {
                _status[player] = PlayerStatus.Abandoned;
                CloseParticipation(player, at);
                if (IsAbandonmentCandidateAt(player))
                    _candidateAbandoners.Add(player);
            }
            return;
        }

        if (State != MatchState.Live) return;
        if (s != PlayerStatus.Active) return;

        _status[player] = PlayerStatus.InGrace;
        _leftAt[player] = at;
        CloseParticipation(player, at);
        if (IsAbandonmentCandidateAt(player))
            _candidateAbandoners.Add(player);

        // Stamp team-collapse start at the actual departure time (not at the next Tick).
        UpdateTeamCollapseTimers(at);
    }

    /// <summary>Player returned to their ship/freq within the grace window.</summary>
    public void OnPlayerReturned(PlayerKey player, DateTimeOffset at)
    {
        if (!_status.TryGetValue(player, out var s)) return;
        if (s != PlayerStatus.InGrace) return;
        if (State != MatchState.Live && State != MatchState.Forming) return;

        _status[player] = PlayerStatus.Active;
        _leftAt.Remove(player);
        _candidateAbandoners.Remove(player);
        OpenParticipation(player, at);

        // Clear the team-collapse timer if this player's return brings their team back to life.
        if (_teamOf.TryGetValue(player, out var teamIdx))
            _teamCollapsedSince.Remove(teamIdx);

        if (State == MatchState.Forming && AllActive())
        {
            State = MatchState.Live;
            StartedAt = at;
        }
    }

    /// <summary>
    /// A departure is abandonment-candidate only when (a) the player still has lives
    /// (or lives are unlimited) AND (b) at least one teammate is still viable. Players
    /// who exhaust their lives or whose team is already dead are not "leaving teammates
    /// out to dry."
    /// </summary>
    private bool IsAbandonmentCandidateAt(PlayerKey player)
    {
        if (LivesPerPlayer.HasValue
            && _livesRemaining.TryGetValue(player, out var myLives)
            && myLives == 0)
            return false;

        if (!_teamOf.TryGetValue(player, out var teamIdx)) return false;

        var team = Teams[teamIdx];
        for (int j = 0; j < team.Count; j++)
        {
            var teammate = team[j];
            if (teammate == player) continue;
            if (IsViable(teammate)) return true;
        }
        return false;
    }

    private bool IsViable(PlayerKey player)
    {
        if (!_status.TryGetValue(player, out var s)) return false;
        if (s == PlayerStatus.Abandoned) return false;
        if (LivesPerPlayer.HasValue
            && _livesRemaining.TryGetValue(player, out var lives)
            && lives == 0)
            return false;
        return true;
    }

    private bool TeamHasViablePlayer(int teamIdx)
    {
        var team = Teams[teamIdx];
        for (int j = 0; j < team.Count; j++)
            if (IsViable(team[j])) return true;
        return false;
    }

    private int CountViableTeams()
    {
        int count = 0;
        for (int t = 0; t < Teams.Count; t++)
            if (TeamHasViablePlayer(t)) count++;
        return count;
    }

    private void OpenParticipation(PlayerKey player, DateTimeOffset at)
    {
        if (!_participations.TryGetValue(player, out var list))
        {
            list = new List<ParticipationPeriod>();
            _participations[player] = list;
        }
        list.Add(new ParticipationPeriod(at, null));
    }

    private void CloseParticipation(PlayerKey player, DateTimeOffset at)
    {
        if (!_participations.TryGetValue(player, out var list) || list.Count == 0) return;
        var last = list[^1];
        if (last.Exit is null)
            list[^1] = last with { Exit = at };
    }

    private void CloseAllOpenParticipations(DateTimeOffset at)
    {
        foreach (var list in _participations.Values)
        {
            if (list.Count == 0) continue;
            var last = list[^1];
            if (last.Exit is null)
                list[^1] = last with { Exit = at };
        }
    }

    /// <summary>Records a kill. No-op if either player is unknown to this match.</summary>
    public void OnKill(PlayerKey killer, PlayerKey victim, DateTimeOffset at)
    {
        if (State != MatchState.Live) return;
        if (!_teamOf.TryGetValue(killer, out var killerTeam)) return;
        if (!_teamOf.TryGetValue(victim, out var victimTeam)) return;

        Increment(_deathsByPlayer, victim);

        if (killerTeam == victimTeam)
        {
            Increment(_teamkillsByPlayer, killer);
            return;
        }

        Increment(_killsByPlayer, killer);
        _killsByTeam[killerTeam]++;

        // Decrement victim's lives (if lives are configured) and record exit time on zero.
        if (LivesPerPlayer.HasValue
            && _livesRemaining.TryGetValue(victim, out var lives)
            && lives > 0)
        {
            int newLives = lives - 1;
            _livesRemaining[victim] = newLives;
            if (newLives == 0 && !_exitedAt.ContainsKey(victim))
                _exitedAt[victim] = at;
        }

        // After a death, refresh collapse timers and check for immediate forfeit/collapse.
        UpdateTeamCollapseTimers(at);
        if (TryForfeitOrAbandon(at)) return;

        TryFinish(at);
    }

    private static void Increment(Dictionary<PlayerKey, int> map, PlayerKey key)
    {
        map[key] = map.TryGetValue(key, out var n) ? n + 1 : 1;
    }

    /// <summary>
    /// Drives time-based transitions: join-timeout cancellation, grace-window expirations,
    /// and end-policy checks.
    /// </summary>
    public void Tick(DateTimeOffset at)
    {
        if (State == MatchState.Completed || State == MatchState.Cancelled || State == MatchState.Abandoned)
            return;

        if (State == MatchState.Forming)
        {
            if (at - ProposedAt >= JoinTimeout)
            {
                FinalizeCancellation(at);
                return;
            }
        }

        // Expire any in-grace players who timed out.
        if (_leftAt.Count > 0)
        {
            List<PlayerKey>? expired = null;
            foreach (var kvp in _leftAt)
            {
                if (at - kvp.Value >= GraceWindow)
                    (expired ??= new List<PlayerKey>()).Add(kvp.Key);
            }
            if (expired is not null)
            {
                foreach (var p in expired)
                {
                    _status[p] = PlayerStatus.Abandoned;
                    _leftAt.Remove(p);
                }
            }
        }

        UpdateTeamCollapseTimers(at);

        // If only one team is still alive (not yet forfeited past the collapse grace), the
        // others have forfeited. If no teams are alive, the match collapses entirely.
        if (State == MatchState.Live && TryForfeitOrAbandon(at))
            return;

        TryFinish(at);
    }

    /// <summary>
    /// Maintain per-team collapse timestamps. A team is "collapsed" when no member is currently
    /// in {Active, Pending}. Collapse-grace begins on the first tick we see a team in this state;
    /// recovery (any member returning) clears the timer.
    /// </summary>
    private void UpdateTeamCollapseTimers(DateTimeOffset at)
    {
        for (int t = 0; t < Teams.Count; t++)
        {
            if (HasLiveMember(t)) _teamCollapsedSince.Remove(t);
            else if (!_teamCollapsedSince.ContainsKey(t)) _teamCollapsedSince[t] = at;
        }
    }

    /// <summary>
    /// A team has a "live" member if at least one player is in <see cref="PlayerStatus.Active"/>
    /// or <see cref="PlayerStatus.Pending"/> AND has lives remaining (or lives unlimited).
    /// InGrace and Abandoned players don't count as live for team-collapse purposes.
    /// </summary>
    private bool HasLiveMember(int teamIdx)
    {
        var team = Teams[teamIdx];
        for (int j = 0; j < team.Count; j++)
        {
            var p = team[j];
            var s = _status[p];
            if (s != PlayerStatus.Active && s != PlayerStatus.Pending) continue;
            if (LivesPerPlayer.HasValue
                && _livesRemaining.TryGetValue(p, out var lives)
                && lives == 0)
                continue;
            return true;
        }
        return false;
    }

    private bool IsTeamForfeited(int teamIdx, DateTimeOffset at)
    {
        if (HasLiveMember(teamIdx)) return false;

        // Team has no Active/Pending players with lives. If any teammates are still in grace
        // (and could potentially come back), use the team-collapse grace window. If everyone is
        // either Abandoned or out of lives, forfeit is immediate -- no possibility of recovery.
        if (!HasRecoverableMember(teamIdx)) return true;

        if (!_teamCollapsedSince.TryGetValue(teamIdx, out var since)) return false;
        return at - since >= TeamCollapseGrace;
    }

    /// <summary>
    /// A team has a "recoverable" member if at least one player is in <see cref="PlayerStatus.InGrace"/>
    /// AND has lives remaining (or unlimited). Such players can still rejoin via a return.
    /// </summary>
    private bool HasRecoverableMember(int teamIdx)
    {
        var team = Teams[teamIdx];
        for (int j = 0; j < team.Count; j++)
        {
            var p = team[j];
            if (_status[p] != PlayerStatus.InGrace) continue;
            if (LivesPerPlayer.HasValue
                && _livesRemaining.TryGetValue(p, out var lives)
                && lives == 0)
                continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// If exactly one team is still alive (haven't passed team-collapse grace), the match ends
    /// as a forfeit win for that team. If zero teams are alive, the match aborts entirely.
    /// </summary>
    private bool TryForfeitOrAbandon(DateTimeOffset at)
    {
        if (Teams.Count < 2) return false;

        int alive = 0, lastAliveIdx = -1;
        for (int t = 0; t < Teams.Count; t++)
        {
            if (!IsTeamForfeited(t, at))
            {
                alive++;
                lastAliveIdx = t;
            }
        }

        if (alive == 0) { FinalizeAbandonment(at); return true; }
        if (alive == 1) { FinalizeAsForfeit(at, lastAliveIdx); return true; }
        return false;
    }

    private bool AllActive()
    {
        foreach (var s in _status.Values)
            if (s != PlayerStatus.Active) return false;
        return true;
    }

    private void TryFinish(DateTimeOffset at)
    {
        var outcome = _endPolicy.CheckOutcome(this, at);
        if (outcome is null) return;

        State = MatchState.Completed;
        EndedAt = at;
        CloseAllOpenParticipations(at);
        Outcome = Enrich(outcome with { FinalState = MatchState.Completed, EndedAt = at }, at);
    }

    private void FinalizeCancellation(DateTimeOffset at)
    {
        // Mark every player who never reached Active as a no-show abandoner.
        var keys = new List<PlayerKey>(_status.Keys);
        foreach (var p in keys)
        {
            if (_status[p] == PlayerStatus.Pending)
            {
                _status[p] = PlayerStatus.Abandoned;
                _candidateAbandoners.Add(p);  // no-shows always count as abandoners
            }
        }
        State = MatchState.Cancelled;
        EndedAt = at;
        CloseAllOpenParticipations(at);
        // Includes both pre-match leavers (caught at OnPlayerLeft) and no-shows (just marked above).
        Outcome = new MatchOutcome(MatchId, GameType, Array.Empty<RankedTeam>(), CollectAbandoners(), MatchState.Cancelled, at);
    }

    /// <summary>
    /// Finalize as a forfeit win: exactly one team is still alive. Survivor ranks first; other
    /// teams ranked by kill count.
    /// </summary>
    private void FinalizeAsForfeit(DateTimeOffset at, int survivorIdx)
    {
        var others = new List<(int teamIdx, int score)>();
        for (int t = 0; t < Teams.Count; t++)
            if (t != survivorIdx) others.Add((t, _killsByTeam[t]));
        others.Sort((a, b) =>
        {
            int byScore = b.score.CompareTo(a.score);
            return byScore != 0 ? byScore : a.teamIdx.CompareTo(b.teamIdx);
        });

        var ranked = new RankedTeam[Teams.Count];
        ranked[0] = new RankedTeam(1, Teams[survivorIdx], _killsByTeam[survivorIdx]);
        for (int i = 0; i < others.Count; i++)
            ranked[i + 1] = new RankedTeam(i + 2, Teams[others[i].teamIdx], others[i].score);

        State = MatchState.Completed;
        EndedAt = at;
        CloseAllOpenParticipations(at);
        Outcome = Enrich(
            new MatchOutcome(MatchId, GameType, ranked, CollectAbandoners(), MatchState.Completed, at),
            at);
    }

    private List<PlayerKey> CollectAbandoners()
    {
        var list = new List<PlayerKey>();
        foreach (var p in _candidateAbandoners)
        {
            if (!_status.TryGetValue(p, out var s)) continue;
            // A candidate who is still in grace at match end never returned in time -- they're an abandoner.
            if (s == PlayerStatus.Abandoned || s == PlayerStatus.InGrace)
                list.Add(p);
        }
        return list;
    }

    private void FinalizeAbandonment(DateTimeOffset at)
    {
        // Rank teams by kills, descending. Empty (abandoned) teams go last.
        var ranks = new (int teamIdx, int score, bool alive)[Teams.Count];
        for (int t = 0; t < Teams.Count; t++)
        {
            bool alive = false;
            for (int j = 0; j < Teams[t].Count; j++)
            {
                var s = _status[Teams[t][j]];
                if (s != PlayerStatus.Abandoned) { alive = true; break; }
            }
            ranks[t] = (t, _killsByTeam[t], alive);
        }

        Array.Sort(ranks, (a, b) =>
        {
            if (a.alive != b.alive) return b.alive.CompareTo(a.alive);
            return b.score.CompareTo(a.score);
        });

        var rankedTeams = new RankedTeam[Teams.Count];
        for (int i = 0; i < ranks.Length; i++)
            rankedTeams[i] = new RankedTeam(i + 1, Teams[ranks[i].teamIdx], ranks[i].score);

        State = MatchState.Abandoned;
        EndedAt = at;
        CloseAllOpenParticipations(at);
        Outcome = Enrich(
            new MatchOutcome(MatchId, GameType, rankedTeams, CollectAbandoners(), MatchState.Abandoned, at),
            at);
    }

    /// <summary>
    /// Attach per-player kill / time-alive data so the rating updater can apply margin-of-victory
    /// scaling and per-player OpenSkill weights. No-op enrichment when the match never went live
    /// (no <see cref="StartedAt"/>) or when lives aren't configured -- in either case there's no
    /// meaningful "time alive" to weight against.
    /// </summary>
    private MatchOutcome Enrich(MatchOutcome basic, DateTimeOffset endedAt)
    {
        if (StartedAt is null) return basic;

        var stats = new Dictionary<PlayerKey, PlayerOutcomeStats>(_teamOf.Count);
        foreach (var (player, _) in _teamOf)
        {
            int kills = _killsByPlayer.TryGetValue(player, out var k) ? k : 0;
            var alive = GetParticipationTime(player, endedAt) ?? TimeSpan.Zero;
            stats[player] = new PlayerOutcomeStats(kills, alive);
        }
        var duration = endedAt - StartedAt.Value;
        return basic with
        {
            PlayerStats = stats,
            LivesPerPlayer = LivesPerPlayer,
            Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
        };
    }
}
