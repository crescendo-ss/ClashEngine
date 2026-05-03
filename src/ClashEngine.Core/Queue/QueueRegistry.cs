using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;

namespace ClashEngine.Core.Queue;

/// <summary>
/// Tracks all configured matchmaking queues, indexed by name (case-insensitive).
/// </summary>
public sealed class QueueRegistry
{
    private readonly Dictionary<string, QueueDefinition> _byName = new(StringComparer.OrdinalIgnoreCase);

    public QueueDefinition Register(
        string name,
        MatchShape shape,
        PartitionQualityPolicy qualityPolicy,
        GameTypeId gameType = default,
        Func<IMatchEndPolicy>? endPolicyFactory = null,
        Func<IGriefingHeuristic>? griefingHeuristicFactory = null,
        int vetoesRequired = 2,
        TimeSpan? vetoWindow = null,
        double ratingWeight = 1.0,
        string? matchArenaName = null,
        IReadOnlyList<IReadOnlyList<int>>? shipBySlot = null,
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
        MatchmakingTier tier = MatchmakingTier.Competitive,
        TimeSpan? shipChangeGracePeriod = null,
        TimeSpan? timeLimit = null)
    {
        if (_byName.ContainsKey(name))
            throw new ArgumentException($"Queue '{name}' already registered.", nameof(name));

        var def = new QueueDefinition(
            name, shape, qualityPolicy, gameType,
            endPolicyFactory, griefingHeuristicFactory,
            vetoesRequired, vetoWindow, ratingWeight,
            matchArenaName, shipBySlot, spawnSetByTeam, maxSpawnDriftTiles, warpOnSpawn,
            stagingDuration, countdownDuration,
            lookAheadWindow, promoteWinnersToFront, maxConsecutiveDefenses,
            holdWindow, qualityCeiling, knockoutSpecDelay,
            livesPerPlayer, teamCollapseGrace, tier, shipChangeGracePeriod,
            timeLimit);
        _byName[name] = def;
        return def;
    }

    public bool TryGet(string name, out QueueDefinition definition)
    {
        var found = _byName.TryGetValue(name, out var d);
        definition = d!;
        return found;
    }

    public bool Contains(string name) => _byName.ContainsKey(name);

    public IEnumerable<QueueDefinition> Definitions => _byName.Values;

    public int Count => _byName.Count;
}
