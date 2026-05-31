using System;
using System.Collections.Generic;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Matching;

/// <summary>
/// Orchestrates queue membership and the per-tick search for a viable match. All public methods
/// must be called on a single thread (the engine is single-threaded by design).
/// </summary>
/// <remarks>
/// Adaptive popping: when a viable partition is first found in a queue, it is held as a candidate
/// for up to <see cref="QueueDefinition.HoldWindow"/>. On each tick the matcher recomputes the
/// best partition and updates the candidate if quality improves. The candidate pops when:
/// <list type="bullet">
/// <item>Its quality reaches <see cref="QueueDefinition.QualityCeiling"/>, or</item>
/// <item>The hold window has elapsed, or</item>
/// <item>The pool no longer permits any viable partition (rare; restart hold).</item>
/// </list>
/// </remarks>
public sealed class Matcher
{
    /// <summary>
    /// Minimum quality delta for a held-candidate replacement to fire
    /// <see cref="IMatchmakingTelemetry.OnQueueHoldImproved"/>. Tiny improvements still update the
    /// internal candidate but stay silent so chat doesn't churn.
    /// </summary>
    private const double HoldImprovementMinDelta = 0.1;

    private readonly QueueRegistry _registry;
    private readonly MultiQueueIndex _multiQueue;
    private readonly TeamBalancer _balancer;
    private readonly IMatchQualityFunction _quality;
    private readonly IClock _clock;
    private readonly Func<IMatchmakingTelemetry> _telemetry;

    /// <summary>Per-queue held candidate awaiting hold-window expiry or quality-ceiling hit.</summary>
    private readonly Dictionary<string, HeldCandidate> _held = new(StringComparer.OrdinalIgnoreCase);

    private sealed record HeldCandidate(BalanceResult Result, DateTimeOffset HeldSince);

    public Matcher(
        QueueRegistry registry,
        MultiQueueIndex multiQueue,
        TeamBalancer balancer,
        IMatchQualityFunction quality,
        IClock clock,
        Func<IMatchmakingTelemetry>? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(multiQueue);
        ArgumentNullException.ThrowIfNull(balancer);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(clock);

        _registry = registry;
        _multiQueue = multiQueue;
        _balancer = balancer;
        _quality = quality;
        _clock = clock;
        // Resolved per-call so engine swaps via SetTelemetry are picked up immediately.
        _telemetry = telemetry ?? (() => NoOpTelemetry.Instance);
    }

    public bool Enqueue(PlayerKey player, Rating rating, string queueName, GroupId? group = null)
    {
        if (!_registry.TryGet(queueName, out var def)) return false;

        var entry = new QueueEntry(player, rating, _clock.UtcNow, group);
        if (!def.Queue.Add(entry)) return false;

        _multiQueue.Add(player, def.UniqueId);
        return true;
    }

    public bool EnqueuePriority(PlayerKey player, Rating rating, string queueName, GroupId? group = null)
    {
        if (!_registry.TryGet(queueName, out var def)) return false;

        var entry = new QueueEntry(player, rating, _clock.UtcNow, group);
        if (!def.Queue.AddPriority(entry)) return false;

        _multiQueue.Add(player, def.UniqueId);
        return true;
    }

    public bool Dequeue(PlayerKey player, string queueName)
    {
        if (!_registry.TryGet(queueName, out var def)) return false;
        bool removed = def.Queue.Remove(player);
        if (removed)
        {
            _multiQueue.Remove(player, def.UniqueId);
            // The held candidate may include this player; invalidate it so we recompute next tick.
            InvalidateHeldIfContains(def.UniqueId, player);
        }
        return removed;
    }

    public IReadOnlyList<string> DequeueEverywhere(PlayerKey player)
    {
        var names = _multiQueue.RemoveAll(player);
        for (int i = 0; i < names.Count; i++)
        {
            if (_registry.TryGet(names[i], out var def))
                def.Queue.Remove(player);
            InvalidateHeldIfContains(names[i], player);
        }
        return names;
    }

    public MatchProposal? TryProposeMatch()
    {
        var now = _clock.UtcNow;

        foreach (var def in _registry.Definitions)
        {
            var fullSnapshot = def.Queue.Snapshot();
            if (fullSnapshot.Count < def.Shape.TotalPlayers)
            {
                _held.Remove(def.UniqueId);
                continue;
            }

            int poolSize = Math.Min(fullSnapshot.Count, def.LookAheadWindow);
            var snapshot = poolSize == fullSnapshot.Count
                ? fullSnapshot
                : SliceFront(fullSnapshot, poolSize);

            // Group sizes are counted over the FULL queue (not the lookahead slice) so the balancer
            // can tell a party is only partially within the pool and refuse to pull part of it in.
            var fullGroupSizes = ComputeGroupSizes(fullSnapshot);

            var longestWait = now - snapshot[0].EnqueuedAt;
            double minQuality = def.QualityPolicy.MinQuality(longestWait);

            var current = FindBestRespectingGroups(snapshot, def, minQuality, fullGroupSizes);
            if (current is null)
            {
                _held.Remove(def.UniqueId);
                continue;
            }

            // Update the held candidate if quality improved (or none was held yet). Hold-start
            // and (notable) hold-improvement are surfaced via telemetry so the adapter can keep
            // candidates informed during the lookahead window.
            if (_held.TryGetValue(def.UniqueId, out var prior))
            {
                if (current.Quality > prior.Result.Quality)
                {
                    double oldQ = prior.Result.Quality;
                    _held[def.UniqueId] = prior with { Result = current };
                    if (current.Quality - oldQ >= HoldImprovementMinDelta)
                        _telemetry().OnQueueHoldImproved(def.UniqueId, FlattenPlayers(current), oldQ, current.Quality);
                }
            }
            else
            {
                _held[def.UniqueId] = new HeldCandidate(current, now);
                _telemetry().OnQueueHoldStarted(def.UniqueId, FlattenPlayers(current), current.Quality, def.HoldWindow);
            }

            var held = _held[def.UniqueId];
            bool ceilingHit = held.Result.Quality >= def.QualityCeiling;
            bool windowElapsed = now - held.HeldSince >= def.HoldWindow;
            bool noLookaheadHeadroom = poolSize == def.LookAheadWindow;

            // Pop when: ceiling hit, window expired, or pool is already saturated (no further
            // arrivals can be considered without exceeding LookAheadWindow).
            if (!ceilingHit && !windowElapsed && !noLookaheadHeadroom) continue;

            var chosen = held.Result;
            _held.Remove(def.UniqueId);

            // Dequeue every matched player from all queues they were searching. DequeueEverywhere
            // returns each player's full set of queues, which always includes this proposal's queue;
            // carry the *other* queues out so the engine can surface those drops too (the proposal
            // queue's removal it emits itself).
            Dictionary<PlayerKey, IReadOnlyList<string>>? alsoRemovedFrom = null;
            for (int t = 0; t < chosen.Teams.Count; t++)
                for (int j = 0; j < chosen.Teams[t].Count; j++)
                {
                    var p = chosen.Teams[t][j];
                    var removedFrom = DequeueEverywhere(p);
                    if (removedFrom.Count <= 1) continue; // only the proposal queue -> nothing else
                    var others = new List<string>(removedFrom.Count - 1);
                    for (int k = 0; k < removedFrom.Count; k++)
                        if (!string.Equals(removedFrom[k], def.UniqueId, StringComparison.Ordinal))
                            others.Add(removedFrom[k]);
                    if (others.Count > 0)
                        (alsoRemovedFrom ??= new()).Add(p, others);
                }

            return new MatchProposal(def.UniqueId, def.Shape, chosen.Teams, chosen.Quality, now,
                alsoRemovedFrom ?? MatchProposal.NoOtherQueues);
        }

        return null;
    }

    private BalanceResult? FindBestRespectingGroups(
        IReadOnlyList<QueueEntry> snapshot, QueueDefinition def, double minQuality,
        IReadOnlyDictionary<GroupId, int>? fullGroupSizes)
    {
        var grouped = _balancer.FindBest(snapshot, def.Shape, _quality, requireGroupsTogether: true, fullGroupSizes);
        if (grouped is not null && grouped.Quality >= minQuality) return grouped;

        var split = _balancer.FindBest(snapshot, def.Shape, _quality, requireGroupsTogether: false, fullGroupSizes);
        if (split is not null && split.Quality >= minQuality) return split;
        return null;
    }

    /// <summary>
    /// Counts queued members per group across <paramref name="entries"/>. Returns null when no
    /// grouped entries are present (the common solo-only case), letting the balancer skip the check.
    /// </summary>
    private static IReadOnlyDictionary<GroupId, int>? ComputeGroupSizes(IReadOnlyList<QueueEntry> entries)
    {
        Dictionary<GroupId, int>? sizes = null;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Group is not GroupId g) continue;
            sizes ??= new Dictionary<GroupId, int>();
            sizes[g] = sizes.TryGetValue(g, out var c) ? c + 1 : 1;
        }
        return sizes;
    }

    private void InvalidateHeldIfContains(string queueName, PlayerKey player)
    {
        if (!_held.TryGetValue(queueName, out var candidate)) return;
        for (int t = 0; t < candidate.Result.Teams.Count; t++)
            for (int j = 0; j < candidate.Result.Teams[t].Count; j++)
                if (candidate.Result.Teams[t][j] == player)
                {
                    _held.Remove(queueName);
                    return;
                }
    }

    private static IReadOnlyList<QueueEntry> SliceFront(IReadOnlyList<QueueEntry> source, int count)
    {
        var arr = new QueueEntry[count];
        for (int i = 0; i < count; i++) arr[i] = source[i];
        return arr;
    }

    private static IReadOnlyList<PlayerKey> FlattenPlayers(BalanceResult result)
    {
        int total = 0;
        for (int t = 0; t < result.Teams.Count; t++) total += result.Teams[t].Count;
        var arr = new PlayerKey[total];
        int idx = 0;
        for (int t = 0; t < result.Teams.Count; t++)
            for (int j = 0; j < result.Teams[t].Count; j++)
                arr[idx++] = result.Teams[t][j];
        return arr;
    }
}
