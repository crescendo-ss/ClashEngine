using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Queue;

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

    /// <summary>
    /// Players whose status flipped to <see cref="PlayerStatus.Abandoned"/> at any point during
    /// the match (grace expired, no-show, AFK cancel). Sticky -- a subsequent <c>?return</c>
    /// re-activates them but the flag remains so they're still counted as an abandoner at match
    /// end.
    /// </summary>
    private readonly HashSet<PlayerKey> _everAbandoned = new();
    private readonly Dictionary<int, DateTimeOffset> _teamCollapsedSince = new();
    private readonly int[] _killsByTeam;
    private readonly IMatchEndPolicy _endPolicy;

    /// <summary>Per-team wall-clock of the last position sample seen inside the presence zone
    /// from an Active, non-knocked-out member. Seeded to the GO! time by <see cref="MarkLive"/>
    /// (every team starts "present" and must enter the zone within the timeout). Null when the
    /// match has no presence zone.</summary>
    private readonly DateTimeOffset[]? _zoneLastPresence;

    /// <summary>Teams currently flagged zone-vacant (absent past the detection threshold but not
    /// yet past <see cref="PresenceZoneTimeout"/>), keyed to the moment they were last present.
    /// Maintained by <see cref="Tick"/>; cleared by an inside sample. The engine diffs this across
    /// ticks to emit vacated/reclaimed telemetry, mirroring <see cref="TeamCollapsedSince"/>.</summary>
    private readonly Dictionary<int, DateTimeOffset> _zoneVacantSince = new();

    /// <summary>How long a team must be sample-absent from the zone before it's flagged vacant
    /// (and the warning telemetry fires). Comfortably above the client position-packet cadence so
    /// ordinary packet gaps from a stationary-but-present team don't flap the warning.</summary>
    private static readonly TimeSpan ZoneVacancyDetection = TimeSpan.FromSeconds(2);

    public ActiveMatch(
        Guid matchId,
        string gameType,
        IReadOnlyList<IReadOnlyList<PlayerKey>> teams,
        IMatchEndPolicy endPolicy,
        TimeSpan joinTimeout,
        TimeSpan graceWindow,
        DateTimeOffset proposedAt,
        int? livesPerPlayer = null,
        TimeSpan? teamCollapseGrace = null,
        TimeSpan? eliminationCooldown = null,
        SpawnArea? presenceZone = null,
        TimeSpan? presenceZoneTimeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameType);
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
        if (eliminationCooldown is { } ecd && ecd < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(eliminationCooldown), "Must be non-negative.");
        if (presenceZone is { } pz && pz.RadiusTiles < 1)
            throw new ArgumentOutOfRangeException(nameof(presenceZone), "RadiusTiles must be >= 1.");
        if (presenceZoneTimeout is { } pzt && pzt <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(presenceZoneTimeout), "Must be positive.");

        MatchId = matchId;
        GameType = gameType;
        Teams = teams;
        _endPolicy = endPolicy;
        JoinTimeout = joinTimeout;
        GraceWindow = graceWindow;
        ProposedAt = proposedAt;
        LivesPerPlayer = livesPerPlayer;
        TeamCollapseGrace = teamCollapseGrace ?? TimeSpan.FromSeconds(10);
        EliminationCooldown = eliminationCooldown;
        PresenceZone = presenceZone;
        PresenceZoneTimeout = presenceZoneTimeout ?? TimeSpan.FromSeconds(30);
        State = MatchState.Forming;
        _killsByTeam = new int[teams.Count];
        _zoneLastPresence = presenceZone is null ? null : new DateTimeOffset[teams.Count];

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
                // "Lives = N" means N spawns total (the initial spawn + N-1 respawns), so the
                // counter tracks remaining respawns -- it starts at N-1 and a death decrements it,
                // hitting 0 on the death that closes the player's last life.
                if (livesPerPlayer.HasValue) _livesRemaining[p] = livesPerPlayer.Value - 1;
            }
        }
    }

    public Guid MatchId { get; }

    /// <summary>
    /// Registry name of the game type (matches <see cref="GameTypeDef.Name"/>). Threaded
    /// through to <see cref="MatchOutcome.GameType"/> and the v5 upload envelope's
    /// <c>match.gameType</c> string.
    /// </summary>
    public string GameType { get; }
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
    /// Lives each player gets in the match: the initial spawn plus this-many-minus-one respawns,
    /// so a player at <see cref="LivesRemaining"/> 0 just used their last life.
    /// <see langword="null"/> = unlimited (no exit-time tracking).
    /// </summary>
    public int? LivesPerPlayer { get; }

    /// <summary>
    /// Cooldown applied to a player released by a last-life elimination in this match before
    /// <c>?play</c> re-queues them, sourced from the game type's
    /// <c>GameType&lt;i&gt;EliminationCooldown</c>. <see langword="null"/> = use the engine's
    /// built-in default; <see cref="TimeSpan.Zero"/> = no cooldown for this game type. Only
    /// consulted when <see cref="LivesPerPlayer"/> is set.
    /// </summary>
    public TimeSpan? EliminationCooldown { get; }

    /// <summary>
    /// The game type's "stay in the zone" box (tiles), or <see langword="null"/> when this match
    /// has no zone-presence rule. While Live, every team must keep at least one Active,
    /// non-knocked-out player inside this box: a team sample-absent for
    /// <see cref="PresenceZoneTimeout"/> forfeits (ranked last,
    /// <see cref="MatchOutcomeReason.ZoneForfeit"/>). Presence is fed by the host adapter via
    /// <see cref="OnPositionSample"/>.
    /// </summary>
    public SpawnArea? PresenceZone { get; }

    /// <summary>
    /// How long a team may go without any member inside <see cref="PresenceZone"/> before
    /// forfeiting. The clock starts at GO! (a team must also <em>enter</em> the zone within this
    /// window) and resets on every inside sample. Default 30s; only meaningful with a zone set.
    /// </summary>
    public TimeSpan PresenceZoneTimeout { get; }

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
    /// Per-team timestamp of the last confirmed zone presence for teams currently flagged
    /// zone-vacant: nobody from the team has been sampled inside <see cref="PresenceZone"/> since
    /// that moment (and the absence has exceeded the detection threshold). The team forfeits at
    /// <c>value + PresenceZoneTimeout</c> unless an inside sample clears the flag first. Always
    /// empty when no zone is configured. Useful for surfacing forfeit warnings to participants.
    /// </summary>
    public IReadOnlyDictionary<int, DateTimeOffset> ZoneVacantSince => _zoneVacantSince;

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

    /// <summary>
    /// True when the player is no longer eligible to return: lives-mode players whose last life
    /// has been spent (have an <see cref="ExitedAt"/> entry). Abandoned players are NOT knocked
    /// out -- they can still recover via <c>?return</c> (see <see cref="OnPlayerReturned"/>).
    /// Statbox / item-list / scoreboard surfaces share this predicate so they all draw the same
    /// "still in" line.
    /// </summary>
    public bool IsKnockedOut(PlayerKey player)
    {
        return LivesPerPlayer.HasValue && _exitedAt.ContainsKey(player);
    }

    public int? TeamIndexOf(PlayerKey player) =>
        _teamOf.TryGetValue(player, out var t) ? t : null;

    /// <summary>
    /// True if <paramref name="player"/> is currently on track to be counted as an abandoner at
    /// match end: they left while at least one teammate was still viable and have not been excused
    /// by a within-grace return. Mirrors the candidate set that feeds
    /// <see cref="MatchOutcome.AbandonedBy"/>.
    /// </summary>
    public bool IsCandidateAbandoner(PlayerKey player) => _candidateAbandoners.Contains(player);

    /// <summary>
    /// Teammates of <paramref name="player"/> who are currently <see cref="PlayerStatus.Active"/>
    /// and not knocked out -- i.e. still in the match and able to keep playing. Used to notify the
    /// survivors when a teammate is assessed an abandon that they may now leave penalty-free.
    /// </summary>
    public IReadOnlyList<PlayerKey> ActiveTeammatesOf(PlayerKey player)
    {
        if (!_teamOf.TryGetValue(player, out var teamIdx)) return Array.Empty<PlayerKey>();
        List<PlayerKey>? result = null;
        var team = Teams[teamIdx];
        for (int j = 0; j < team.Count; j++)
        {
            var mate = team[j];
            if (mate == player) continue;
            if (GetStatus(mate) != PlayerStatus.Active) continue;
            if (LivesPerPlayer.HasValue && _exitedAt.ContainsKey(mate)) continue;
            (result ??= new List<PlayerKey>()).Add(mate);
        }
        return result is null ? Array.Empty<PlayerKey>() : result;
    }

    /// <summary>Player has entered the arena and joined their assigned ship/freq.</summary>
    public void OnPlayerJoined(PlayerKey player, DateTimeOffset at)
    {
        if (State != MatchState.Forming) return;
        if (!_status.TryGetValue(player, out var s) || s != PlayerStatus.Pending) return;

        _status[player] = PlayerStatus.Active;
        OpenParticipation(player, at);
    }

    /// <summary>
    /// Transition Forming -> Live. The orchestrator calls this at GO! after Setup, Staging, and
    /// Countdown have completed; before then, "all players Active" only means they were placed
    /// onto ships, not that gameplay has started. No-op if any player isn't Active yet, if the
    /// match isn't Forming, or if it's already Live.
    /// </summary>
    public bool MarkLive(DateTimeOffset at)
    {
        if (State != MatchState.Forming) return false;
        if (!AllActive()) return false;
        State = MatchState.Live;
        StartedAt = at;
        // Every team starts the zone-presence clock at GO!: they must enter (and then hold) the
        // zone within PresenceZoneTimeout of the match going live.
        if (_zoneLastPresence is not null)
            for (int t = 0; t < Teams.Count; t++) _zoneLastPresence[t] = at;
        return true;
    }

    /// <summary>
    /// A position report for <paramref name="player"/> (tile coordinates), fed by the host
    /// adapter while the match is Live. Only samples from Active, non-knocked-out participants
    /// inside <see cref="PresenceZone"/> count: they refresh the player's team's presence clock
    /// and clear any pending zone-vacant flag. Returns the team index that just reclaimed the
    /// zone (was flagged vacant, now present) so the caller can emit reclaim telemetry, or
    /// <see langword="null"/> for every other case. No-op without a configured zone.
    /// </summary>
    public int? OnPositionSample(PlayerKey player, int tileX, int tileY, DateTimeOffset at)
    {
        if (PresenceZone is not { } zone) return null;
        if (State != MatchState.Live) return null;
        if (!_teamOf.TryGetValue(player, out var teamIdx)) return null;
        if (_status[player] != PlayerStatus.Active) return null;
        if (LivesPerPlayer.HasValue && _exitedAt.ContainsKey(player)) return null;
        if (!zone.Contains(tileX, tileY)) return null;

        _zoneLastPresence![teamIdx] = at;
        return _zoneVacantSince.Remove(teamIdx) ? teamIdx : null;
    }

    /// <summary>
    /// Player left the arena, switched to spec, or disconnected. The server cannot reliably tell
    /// these apart, so every departure is treated uniformly. A placed (Active) player who leaves --
    /// whether the match is still Forming (pre-GO!: they were placed during Setup/Staging/Countdown)
    /// or already Live -- goes into <see cref="PlayerStatus.InGrace"/> with <see cref="GraceWindow"/>
    /// to return. Returning in time forgives them (see <see cref="OnPlayerReturned"/>); failing to
    /// return lets <see cref="Tick"/> flip them to <see cref="PlayerStatus.Abandoned"/>, which the
    /// candidate rule (lives remaining + at least one viable teammate) records as abandonment.
    /// <para>
    /// Granting pre-GO! departures the same grace as in-progress ones is deliberate: it lets a
    /// player spec during the countdown and <c>?return</c> without being permanently stranded out
    /// of the match (a no-longer-Active player blocks <see cref="MarkLive"/>) or eating an abandon
    /// penalty for a lapse they recovered from. A pre-GO! bailer who never comes back is still
    /// caught -- their grace expires and the join-timeout cancellation collects them all the same.
    /// </para>
    /// </summary>
    public void OnPlayerLeft(PlayerKey player, DateTimeOffset at)
    {
        if (!_status.TryGetValue(player, out var s)) return;
        if (State != MatchState.Forming && State != MatchState.Live) return;
        if (s != PlayerStatus.Active) return;

        _status[player] = PlayerStatus.InGrace;
        _leftAt[player] = at;
        CloseParticipation(player, at);
        if (IsAbandonmentCandidateAt(player))
            _candidateAbandoners.Add(player);

        // Stamp team-collapse start at the actual departure time (not at the next Tick).
        UpdateTeamCollapseTimers(at);
    }

    /// <summary>
    /// Player returned to their ship/freq. <see cref="PlayerStatus.InGrace"/> returns are within
    /// the per-player grace window: the player is forgiven and dropped from the abandoner
    /// candidate set. <see cref="PlayerStatus.Abandoned"/> returns happen via <c>?return</c>
    /// after the grace expired: the player rejoins the match (flips back to Active and the
    /// team-collapse timer clears) but the abandon flag is sticky -- they're still counted as an
    /// abandoner at match end via <see cref="_everAbandoned"/>. Knocked-out players (lives
    /// exhausted) cannot return.
    /// Returns <see langword="true"/> when the return took effect (the player flipped back to
    /// Active); <see langword="false"/> when it was a no-op (unknown player, not in a returnable
    /// status, match no longer running, or knocked out).
    /// </summary>
    public bool OnPlayerReturned(PlayerKey player, DateTimeOffset at)
    {
        if (!_status.TryGetValue(player, out var s)) return false;
        if (s != PlayerStatus.InGrace && s != PlayerStatus.Abandoned) return false;
        if (State != MatchState.Live && State != MatchState.Forming) return false;
        // Knocked out -- their slot is closed for the rest of the match.
        if (LivesPerPlayer.HasValue && _exitedAt.ContainsKey(player)) return false;

        _status[player] = PlayerStatus.Active;
        _leftAt.Remove(player);
        // Within-grace return forgives the candidate flag; after-grace return does not (the
        // sticky _everAbandoned set still counts them as an abandoner at match end).
        if (s == PlayerStatus.InGrace) _candidateAbandoners.Remove(player);
        OpenParticipation(player, at);

        // Clear the team-collapse timer if this player's return brings their team back to life.
        if (_teamOf.TryGetValue(player, out var teamIdx))
            _teamCollapsedSince.Remove(teamIdx);
        return true;
    }

    /// <summary>
    /// A departure is abandonment-candidate only when (a) the player still has lives
    /// (or lives are unlimited) AND (b) at least one teammate is still viable. Players
    /// who exhaust their lives or whose team is already dead are not "leaving teammates
    /// out to dry."
    /// </summary>
    private bool IsAbandonmentCandidateAt(PlayerKey player)
    {
        // A player on their last life still has 0 remaining respawns -- "exhausted" is the
        // ExitedAt marker, set the moment the eliminating death lands.
        if (LivesPerPlayer.HasValue && _exitedAt.ContainsKey(player))
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
        if (LivesPerPlayer.HasValue && _exitedAt.ContainsKey(player))
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
        bool isTeamkill = killerTeam == victimTeam;

        if (isTeamkill)
        {
            Increment(_teamkillsByPlayer, killer);

            // 2-team penalty (matches CaptainsMatch / TeamVersusMatch): the opposing team
            // gets a point. In 3+ team matches there is no canonical "other" team to award,
            // so the score-side penalty is skipped -- the lives decrement and griefing-flag
            // tracking below still apply.
            if (Teams.Count == 2)
            {
                int otherTeam = killerTeam == 0 ? 1 : 0;
                _killsByTeam[otherTeam]++;
            }
        }
        else
        {
            Increment(_killsByPlayer, killer);
            _killsByTeam[killerTeam]++;
        }

        // Always decrement the victim's remaining lives, regardless of TK. CaptainsMatch
        // (Lives-- at CaptainsMatch.cs:741) and TeamVersusMatch (Lives-- at
        // TeamVersusMatch.cs:1158) both decrement unconditionally; the early-return-on-TK
        // pattern that used to live here was the outlier and silently let griefers wipe the
        // statbox without consequence.
        //
        // The counter is remaining respawns: a death with respawns left consumes one and the
        // player lives on; a death with none left is the eliminating one. LivesPerPlayer = 1
        // seeds 0 respawns so the first death qualifies; LivesPerPlayer = 2 seeds 1 respawn so
        // the player gets one revival before elimination.
        if (LivesPerPlayer.HasValue
            && _livesRemaining.TryGetValue(victim, out var lives))
        {
            if (lives > 0)
                _livesRemaining[victim] = lives - 1;
            else if (!_exitedAt.ContainsKey(victim))
                _exitedAt[victim] = at;
        }

        // After a death, refresh collapse timers and check for immediate forfeit/collapse --
        // including TK-driven eliminations, since a team can theoretically self-wipe.
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
                    _everAbandoned.Add(p);
                }
            }
        }

        UpdateTeamCollapseTimers(at);

        // If only one team is still alive (not yet forfeited past the collapse grace), the
        // others have forfeited. If no teams are alive, the match collapses entirely.
        if (State == MatchState.Live && TryForfeitOrAbandon(at))
            return;

        // Zone presence: a team sample-absent from the presence zone past the timeout forfeits.
        if (State == MatchState.Live && TryZoneForfeit(at))
            return;

        TryFinish(at);
    }

    /// <summary>
    /// Maintain the per-team zone-presence state and forfeit teams whose absence from
    /// <see cref="PresenceZone"/> has exceeded <see cref="PresenceZoneTimeout"/>. Teams with no
    /// live member are the collapse machinery's concern -- their presence clock is kept fresh so
    /// a team recovering from a full disconnect isn't instantly zone-forfeited on stale data.
    /// Returns true when the match was finalized.
    /// </summary>
    private bool TryZoneForfeit(DateTimeOffset at)
    {
        if (PresenceZone is null) return false;

        List<int>? violators = null;
        for (int t = 0; t < Teams.Count; t++)
        {
            if (!HasLiveMember(t))
            {
                _zoneLastPresence![t] = at;
                _zoneVacantSince.Remove(t);
                continue;
            }

            var absent = at - _zoneLastPresence![t];
            if (absent >= PresenceZoneTimeout)
                (violators ??= new List<int>()).Add(t);
            else if (absent >= ZoneVacancyDetection && !_zoneVacantSince.ContainsKey(t))
                _zoneVacantSince[t] = _zoneLastPresence[t];
        }

        if (violators is null) return false;
        if (violators.Count == Teams.Count)
        {
            // Nobody is contesting the zone: no team earned a win over the others, so the
            // match ends as a draw -- every team shares rank 1 (OpenSkill treats equal ranks
            // as a tie).
            FinalizeAsZoneDraw(at);
            return true;
        }
        FinalizeAsZoneForfeit(at, violators);
        return true;
    }

    /// <summary>
    /// Finalize as a zone draw: every team abandoned the presence zone past the timeout, so
    /// nobody wins and nobody loses. All teams share rank 1; the outcome still carries
    /// <see cref="MatchOutcomeReason.ZoneForfeit"/> so adapters can announce why it ended.
    /// </summary>
    private void FinalizeAsZoneDraw(DateTimeOffset at)
    {
        var ranked = new RankedTeam[Teams.Count];
        for (int t = 0; t < Teams.Count; t++)
            ranked[t] = new RankedTeam(1, Teams[t], _killsByTeam[t]);

        State = MatchState.Completed;
        EndedAt = at;
        CloseAllOpenParticipations(at);
        Outcome = Enrich(
            new MatchOutcome(MatchId, GameType, ranked, CollectAbandoners(), MatchState.Completed, at,
                EndReason: MatchOutcomeReason.ZoneForfeit),
            at);
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
            if (LivesPerPlayer.HasValue && _exitedAt.ContainsKey(p)) continue;
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
    /// A team has a "recoverable" member if at least one player is <see cref="PlayerStatus.InGrace"/>
    /// or <see cref="PlayerStatus.Abandoned"/>, AND has lives remaining (or unlimited). InGrace
    /// players auto-recover by re-shipping; Abandoned players can recover via the <c>?return</c>
    /// command. Either way, the team is given the team-collapse grace window to recover before
    /// forfeiting -- only a fully-knocked-out roster forfeits immediately.
    /// </summary>
    private bool HasRecoverableMember(int teamIdx)
    {
        var team = Teams[teamIdx];
        for (int j = 0; j < team.Count; j++)
        {
            var p = team[j];
            var s = _status[p];
            if (s != PlayerStatus.InGrace && s != PlayerStatus.Abandoned) continue;
            if (LivesPerPlayer.HasValue && _exitedAt.ContainsKey(p)) continue;
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
                _everAbandoned.Add(p);
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
    /// Drops <paramref name="player"/> from the abandonment-candidate set so they are NOT
    /// included in <see cref="MatchOutcome.AbandonedBy"/> when this match finalizes. The
    /// player's team membership, kill/death counters, and FSM status are left intact; only
    /// the post-match penalty accounting changes. Used by the engine's admin <c>ResetPlayer</c>
    /// path so a reset target who happens to be mid-match does not get an abandonment penalty
    /// applied on top of the wipe we just performed.
    /// </summary>
    public void ExcludeFromAbandonment(PlayerKey player)
    {
        _candidateAbandoners.Remove(player);
        _everAbandoned.Remove(player);
    }

    /// <summary>
    /// Cancels a Forming match because the orchestrator detected one or more idle players in
    /// the staging window. Every player named in <paramref name="afkPlayers"/> is marked as an
    /// abandoner regardless of their current status (Active players who got placed but didn't
    /// ready up are just as responsible as Pending no-shows whose placement never landed).
    /// Any remaining Pending player not in the AFK list is still marked as a no-show.
    /// </summary>
    public void CancelAsAfk(IEnumerable<PlayerKey> afkPlayers, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(afkPlayers);
        if (State != MatchState.Forming) return;

        foreach (var p in afkPlayers)
        {
            if (!_status.ContainsKey(p)) continue;   // unknown player -- skip
            _status[p] = PlayerStatus.Abandoned;
            _everAbandoned.Add(p);
            _candidateAbandoners.Add(p);
        }
        FinalizeCancellation(at);
    }

    /// <summary>
    /// Cancels a Forming match on demand -- used when the orchestrator reaches GO! and finds a
    /// rostered player still in spec, so the match can't start. Identical to the join-timeout
    /// cancellation (no-shows flagged, abandonment assessed by the usual candidate rule, so a lone
    /// leaver who stranded no viable teammate stays penalty-free) but immediate, rather than after
    /// the full <see cref="JoinTimeout"/>. No-op once the match has left Forming.
    /// </summary>
    public void Cancel(DateTimeOffset at)
    {
        if (State != MatchState.Forming) return;
        FinalizeCancellation(at);
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

    /// <summary>
    /// Finalize as a zone forfeit: every team in <paramref name="violators"/> failed to hold the
    /// presence zone. Zone-holding teams rank above violators; within each group, by kill count.
    /// The outcome carries <see cref="MatchOutcomeReason.ZoneForfeit"/> so adapters can announce why.
    /// </summary>
    private void FinalizeAsZoneForfeit(DateTimeOffset at, List<int> violators)
    {
        var order = new (int teamIdx, int score, bool held)[Teams.Count];
        for (int t = 0; t < Teams.Count; t++)
            order[t] = (t, _killsByTeam[t], !violators.Contains(t));
        Array.Sort(order, (a, b) =>
        {
            if (a.held != b.held) return b.held.CompareTo(a.held);
            int byScore = b.score.CompareTo(a.score);
            return byScore != 0 ? byScore : a.teamIdx.CompareTo(b.teamIdx);
        });

        var ranked = new RankedTeam[Teams.Count];
        for (int i = 0; i < order.Length; i++)
            ranked[i] = new RankedTeam(i + 1, Teams[order[i].teamIdx], order[i].score);

        State = MatchState.Completed;
        EndedAt = at;
        CloseAllOpenParticipations(at);
        Outcome = Enrich(
            new MatchOutcome(MatchId, GameType, ranked, CollectAbandoners(), MatchState.Completed, at,
                EndReason: MatchOutcomeReason.ZoneForfeit),
            at);
    }

    private List<PlayerKey> CollectAbandoners()
    {
        var list = new List<PlayerKey>();
        foreach (var p in _candidateAbandoners)
        {
            if (!_status.TryGetValue(p, out var s)) continue;
            // Three buckets count as abandoners at match end:
            //   * Abandoned: never returned (or specced out at end without recovering).
            //   * InGrace: still in grace at end -- never returned in time.
            //   * Active + ever-Abandoned: returned via ?return after grace expired. Sticky flag
            //     keeps them counted because they disrupted the match by being away too long.
            if (s == PlayerStatus.Abandoned || s == PlayerStatus.InGrace || _everAbandoned.Contains(p))
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
