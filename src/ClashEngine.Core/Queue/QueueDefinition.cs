using System;
using System.Collections.Generic;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;

namespace ClashEngine.Core.Queue;

/// <summary>
/// A named matchmaking queue together with its match shape, quality policy, the rating
/// game-type it stores ratings under, end-of-match adjudication, griefing-detection knobs,
/// and per-team arena / spawn placement config.
/// </summary>
public sealed class QueueDefinition
{
    public QueueDefinition(
        string uniqueId,
        MatchShape shape,
        PartitionQualityPolicy qualityPolicy,
        string gameType = "",
        Func<IMatchEndPolicy>? endPolicyFactory = null,
        Func<IGriefingHeuristic>? griefingHeuristicFactory = null,
        int vetoesRequired = 2,
        TimeSpan? vetoWindow = null,
        double ratingWeight = 1.0,
        string? matchArenaName = null,
        IReadOnlyList<IReadOnlyList<SpawnPoint>>? spawnSetByTeam = null,
        int? maxSpawnDriftTiles = null,
        bool warpOnSpawn = false,
        TimeSpan? stagingDuration = null,
        TimeSpan? countdownDuration = null,
        int? lookAheadWindow = null,
        bool promoteWinnersToFront = false,
        int maxConsecutiveDefenses = 3,
        TimeSpan? holdWindow = null,
        double qualityCeiling = 0.9,
        TimeSpan? knockoutSpecDelay = null,
        int? livesPerPlayer = null,
        TimeSpan? teamCollapseGrace = null,
        TimeSpan? shipChangeGracePeriod = null,
        TimeSpan? timeLimit = null,
        ItemsAction returnItemsAction = ItemsAction.Full,
        string? ownerArenaName = null,
        string? label = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(uniqueId);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(qualityPolicy);
        if (vetoesRequired < 1)
            throw new ArgumentOutOfRangeException(nameof(vetoesRequired), "Must be >= 1.");
        if (ratingWeight < 0 || ratingWeight > 1)
            throw new ArgumentOutOfRangeException(nameof(ratingWeight), "Must be in [0, 1].");
        if (maxConsecutiveDefenses < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConsecutiveDefenses), "Must be >= 1.");
        if (qualityCeiling < 0 || qualityCeiling > 1)
            throw new ArgumentOutOfRangeException(nameof(qualityCeiling), "Must be in [0, 1].");
        if (holdWindow is { } hw && hw < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(holdWindow), "Must be non-negative.");
        if (knockoutSpecDelay is { } ksd && ksd < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(knockoutSpecDelay), "Must be non-negative.");
        if (livesPerPlayer is { } lpp && lpp < 1)
            throw new ArgumentOutOfRangeException(nameof(livesPerPlayer), "Must be >= 1 when specified.");
        if (teamCollapseGrace is { } tcg && tcg < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(teamCollapseGrace), "Must be non-negative.");
        if (shipChangeGracePeriod is { } scgp && scgp < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(shipChangeGracePeriod), "Must be non-negative.");
        if (timeLimit is { } tl && tl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeLimit), "Must be positive when set.");

        if (spawnSetByTeam is not null)
        {
            if (spawnSetByTeam.Count != shape.TeamCount)
                throw new ArgumentException(
                    $"spawnSetByTeam must have one entry per team ({shape.TeamCount}); got {spawnSetByTeam.Count}.",
                    nameof(spawnSetByTeam));
            for (int t = 0; t < spawnSetByTeam.Count; t++)
                if (spawnSetByTeam[t] is null || spawnSetByTeam[t].Count == 0)
                    throw new ArgumentException(
                        $"spawnSetByTeam[{t}] must contain at least one spawn point.",
                        nameof(spawnSetByTeam));
        }

        if (maxSpawnDriftTiles is { } drift && drift < 0)
            throw new ArgumentOutOfRangeException(nameof(maxSpawnDriftTiles), "Must be non-negative.");

        if (stagingDuration is { } sd && sd <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stagingDuration), "Must be positive.");
        if (countdownDuration is { } cd && cd < TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(countdownDuration), "Must be at least 5 seconds.");

        int effectiveLookAhead = lookAheadWindow ?? shape.TotalPlayers;
        if (effectiveLookAhead < shape.TotalPlayers)
            throw new ArgumentOutOfRangeException(nameof(lookAheadWindow),
                $"Must be >= TotalPlayers ({shape.TotalPlayers}).");

        UniqueId = uniqueId;
        Shape = shape;
        QualityPolicy = qualityPolicy;
        GameType = gameType ?? string.Empty;
        EndPolicyFactory = endPolicyFactory ?? (() => new KillCountEndPolicy(15));
        GriefingHeuristicFactory = griefingHeuristicFactory ?? (() => NoGriefingHeuristic.Instance);
        VetoesRequired = vetoesRequired;
        VetoWindow = vetoWindow ?? TimeSpan.FromSeconds(60);
        RatingWeight = ratingWeight;
        MatchArenaName = matchArenaName;
        SpawnSetByTeam = spawnSetByTeam;
        MaxSpawnDriftTiles = maxSpawnDriftTiles;
        WarpOnSpawn = warpOnSpawn;
        StagingDuration = stagingDuration ?? TimeSpan.FromSeconds(10);
        CountdownDuration = countdownDuration ?? TimeSpan.FromSeconds(10);
        LookAheadWindow = effectiveLookAhead;
        PromoteWinnersToFront = promoteWinnersToFront;
        MaxConsecutiveDefenses = maxConsecutiveDefenses;
        HoldWindow = holdWindow ?? TimeSpan.FromSeconds(10);
        QualityCeiling = qualityCeiling;
        KnockoutSpecDelay = knockoutSpecDelay ?? TimeSpan.Zero;
        LivesPerPlayer = livesPerPlayer;
        TeamCollapseGrace = teamCollapseGrace;
        ShipChangeGracePeriod = shipChangeGracePeriod ?? TimeSpan.FromSeconds(10);
        TimeLimit = timeLimit;
        ReturnItemsAction = returnItemsAction;
        OwnerArenaName = ownerArenaName;

        // BaseName is the structural part of UniqueId without the arena prefix. Used as the
        // Label fallback when the operator didn't supply one.
        if (string.IsNullOrEmpty(ownerArenaName))
        {
            BaseName = uniqueId;
        }
        else
        {
            string prefix = ownerArenaName + "/";
            BaseName = uniqueId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? uniqueId[prefix.Length..]
                : uniqueId;
        }
        Label = string.IsNullOrWhiteSpace(label) ? BaseName : label!;

        Queue = new PlayerQueue(uniqueId);
    }

    /// <summary>
    /// Arena that owns this queue (the arena whose <c>[ClashEngine]</c> section contributed
    /// it), or <see langword="null"/> for queues registered at construction without an owning
    /// arena (legacy / test path). The owning arena drives lifecycle: when the arena is
    /// detached from <see cref="ClashModule"/>, every queue it contributed is removed from the
    /// registry.
    /// </summary>
    public string? OwnerArenaName { get; }

    /// <summary>
    /// Structural short identifier: <see cref="UniqueId"/> with the leading "<c>{ownerArena}/</c>"
    /// prefix stripped. This is the operator-facing identifier they write under
    /// <c>[ClashEngine]</c> as <c>Queue&lt;i&gt;Name</c> and that players type after
    /// <c>?play</c> (e.g. "<c>casual_4v4</c>"). For human-friendly display use
    /// <see cref="Label"/> instead.
    /// </summary>
    public string BaseName { get; }

    /// <summary>
    /// Operator-chosen pretty string for chat output and the match-stats JSON payload (e.g.
    /// "<c>4v4 (Casual)</c>"). Falls back to <see cref="BaseName"/> when the operator did not
    /// supply a <c>Queue&lt;i&gt;Label</c> under <c>[ClashEngine]</c>.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Globally-unique key for this queue in the <see cref="QueueRegistry"/>. Composed as
    /// "<c>{OwnerArenaName}/{BaseName}</c>" for arena-owned queues, or just <c>BaseName</c> for
    /// queues constructed without an owner (legacy / test path). Used as the dictionary key,
    /// for matcher proposals, in-flight match indexing, and the JSON payload's
    /// <c>queueName</c> field. Operators never type this directly; for chat / display use
    /// <see cref="Label"/>.
    /// </summary>
    public string UniqueId { get; }
    public MatchShape Shape { get; }
    public PartitionQualityPolicy QualityPolicy { get; }

    /// <summary>
    /// Registry name of the game type this queue forms matches under. Looked up against the
    /// engine's <see cref="ClashEngine.Core.GameType.GameTypeRegistry"/>; only queues whose
    /// gametype has been accepted by the stats server appear in the live registry. Carried
    /// through to <see cref="ActiveMatch.GameType"/> and the v5 match-upload envelope's
    /// <c>match.gameType</c> string. Defaults to <see cref="string.Empty"/> for legacy / test
    /// fixtures that don't care about a specific game type.
    /// </summary>
    public string GameType { get; }
    public Func<IMatchEndPolicy> EndPolicyFactory { get; }
    public Func<IGriefingHeuristic> GriefingHeuristicFactory { get; }
    public int VetoesRequired { get; }
    public TimeSpan VetoWindow { get; }
    public double RatingWeight { get; }
    public string? MatchArenaName { get; }

    /// <summary>
    /// Per-team set of candidate spawn points. At setup the orchestrator picks one entry from
    /// each team's list at random; every player on that team is warped to the chosen point.
    /// <see langword="null"/> means no spawn override (players spawn wherever the arena's
    /// default spawn places them).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<SpawnPoint>>? SpawnSetByTeam { get; }

    /// <summary>
    /// Maximum drift (in tiles, where 1 tile = 16 pixels) a player may travel from their team's
    /// chosen spawn during Staging and Countdown before the orchestrator forces a warp back.
    /// <see langword="null"/> or 0 disables drift enforcement.
    /// </summary>
    public int? MaxSpawnDriftTiles { get; }

    /// <summary>
    /// Master switch for the spawn-warp behavior. When <see langword="true"/>, the orchestrator
    /// picks one entry from each team's <see cref="SpawnSetByTeam"/> at setup, warps the team to
    /// it, enforces <see cref="MaxSpawnDriftTiles"/> during Staging/Countdown, and re-warps the
    /// team to that spawn at GO. When <see langword="false"/> (default), all spawn-related warps
    /// are suppressed and the configured spawn coordinates are ignored, leaving players at the
    /// arena's default spawn.
    /// </summary>
    public bool WarpOnSpawn { get; }

    /// <summary>
    /// Length of the staging (warmup / readiness-check) window between match formation and the
    /// pre-GO countdown. Players must demonstrate non-idleness within this window or the match
    /// is cancelled. Default 10 seconds.
    /// </summary>
    public TimeSpan StagingDuration { get; }

    /// <summary>
    /// Length of the pre-GO countdown. The orchestrator broadcasts <c>"All set!"</c> up-front
    /// (with <c>"Starting in N seconds!"</c> appended for N&gt;10), then ticks <c>"-3-"</c>
    /// → <c>"-2-"</c> → <c>"-1-"</c> → <c>"GO!"</c> over the final 3s. Ships lock
    /// <see cref="ClashEngine.Adapter.MatchFreqAdvisor.ShipLockBeforeStart"/> (5s) before GO,
    /// so values above that leave a free ship-pick window at the start of the countdown.
    /// Minimum 5s, default 10s (5s pick + 5s locked).
    /// </summary>
    public TimeSpan CountdownDuration { get; }

    /// <summary>
    /// Maximum number of candidates from the head of the queue the matcher considers when
    /// looking for the best partition. Defaults to <see cref="MatchShape.TotalPlayers"/> (no
    /// expansion). Larger values let the balancer trade strict FIFO order for better quality;
    /// the longest-waiting candidate is always included in any considered partition.
    /// </summary>
    public int LookAheadWindow { get; }

    /// <summary>
    /// When <see langword="true"/>, the winning team's players are auto-re-enqueued at the head
    /// of this queue after a Completed match (a "king of the hill" mode).
    /// </summary>
    public bool PromoteWinnersToFront { get; }

    /// <summary>
    /// Maximum number of consecutive defenses before champions are sent to the back of the queue
    /// to give challengers a clean shot. Only meaningful with <see cref="PromoteWinnersToFront"/>.
    /// </summary>
    public int MaxConsecutiveDefenses { get; }

    /// <summary>
    /// Once the matcher finds an acceptable partition for this queue, it holds the candidate for
    /// up to this duration to see if additional arrivals can improve the partition's quality.
    /// Set to <c>TimeSpan.Zero</c> to pop immediately on first viable proposal.
    /// </summary>
    public TimeSpan HoldWindow { get; }

    /// <summary>
    /// If a held candidate's quality reaches this threshold, the matcher pops it immediately
    /// without waiting for the rest of the hold window.
    /// </summary>
    public double QualityCeiling { get; }

    /// <summary>
    /// Grace period between a player exhausting their last life and being forced to spec. The
    /// player remains in their ship during this window so residual mines / bombs / shrapnel they
    /// just fired can land. Defaults to <see cref="TimeSpan.Zero"/> (immediate spec). Match-end
    /// cleanup ignores this and specs everyone immediately.
    /// </summary>
    public TimeSpan KnockoutSpecDelay { get; }

    /// <summary>
    /// Lives each player starts with. <see langword="null"/> = unlimited (no elimination). When
    /// set, a player at 0 lives is eliminated and released from the match-roster (subject to
    /// <see cref="PenaltyKind.EliminationCooldown"/> if configured).
    /// </summary>
    public int? LivesPerPlayer { get; }

    /// <summary>
    /// How long an entire team can be without any Active or Pending players before forfeiting.
    /// <see langword="null"/> falls back to <see cref="ActiveMatch"/>'s default (10 s). Distinct
    /// from per-player grace: the team-collapse grace gives a team time to recover from
    /// simultaneous disconnects without waiting for individual graces to expire.
    /// </summary>
    public TimeSpan? TeamCollapseGrace { get; }

    /// <summary>
    /// How long after a death a player may freely change ships before being re-locked to their
    /// current ship for the rest of the life. Mid-life ship changes are otherwise forbidden
    /// because each new ship reset refreshes item counts (a free reload). Defaults to 10 seconds;
    /// set to <see cref="TimeSpan.Zero"/> to forbid all ship changes during a match.
    /// </summary>
    public TimeSpan ShipChangeGracePeriod { get; }

    /// <summary>
    /// Wall-clock duration after which the engine ends the match (via <c>TimeLimitEndPolicy</c>),
    /// and which the host adapter exposes through <c>IMatchConfiguration.TimeLimit</c> so the
    /// scoreboard timer renders. <see langword="null"/> means kill-count / lives-only -- no timer
    /// display either.
    /// </summary>
    public TimeSpan? TimeLimit { get; }

    /// <summary>
    /// What to do with a player's stockpilable item counts when they <c>?return</c> to this match
    /// after self-speccing. Inherited from the queue's game type
    /// (<c>GameType&lt;i&gt;ReturnItemsAction</c>). <see cref="ItemsAction.Full"/> (default) leaves
    /// them with the fresh ship's spawn loadout. <see cref="ItemsAction.Restore"/> deducts the
    /// spawn loadout back down to whatever items they had when they specced (closes the burst/repel
    /// free-reload loophole). <see cref="ItemsAction.Burn"/> zeros their loadout entirely.
    /// </summary>
    public ItemsAction ReturnItemsAction { get; }

    /// <summary>Conventional team-index -> freq number (100, 200, 300, ...).</summary>
    public static short FreqOf(int teamIndex) => (short)(100 * (teamIndex + 1));

    public PlayerQueue Queue { get; }
}
