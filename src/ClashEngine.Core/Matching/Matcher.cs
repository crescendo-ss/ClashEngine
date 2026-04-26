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
    private readonly QueueRegistry _registry;
    private readonly MultiQueueIndex _multiQueue;
    private readonly TeamBalancer _balancer;
    private readonly IMatchQualityFunction _quality;
    private readonly IClock _clock;

    /// <summary>Per-queue held candidate awaiting hold-window expiry or quality-ceiling hit.</summary>
    private readonly Dictionary<string, HeldCandidate> _held = new(StringComparer.OrdinalIgnoreCase);

    private sealed record HeldCandidate(BalanceResult Result, DateTimeOffset HeldSince);

    public Matcher(
        QueueRegistry registry,
        MultiQueueIndex multiQueue,
        TeamBalancer balancer,
        IMatchQualityFunction quality,
        IClock clock)
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
    }

    public bool Enqueue(PlayerKey player, Rating rating, string queueName, GroupId? group = null)
    {
        if (!_registry.TryGet(queueName, out var def)) return false;

        var entry = new QueueEntry(player, rating, _clock.UtcNow, group);
        if (!def.Queue.Add(entry)) return false;

        _multiQueue.Add(player, def.Name);
        return true;
    }

    public bool EnqueuePriority(PlayerKey player, Rating rating, string queueName, GroupId? group = null)
    {
        if (!_registry.TryGet(queueName, out var def)) return false;

        var entry = new QueueEntry(player, rating, _clock.UtcNow, group);
        if (!def.Queue.AddPriority(entry)) return false;

        _multiQueue.Add(player, def.Name);
        return true;
    }

    public bool Dequeue(PlayerKey player, string queueName)
    {
        if (!_registry.TryGet(queueName, out var def)) return false;
        bool removed = def.Queue.Remove(player);
        if (removed)
        {
            _multiQueue.Remove(player, def.Name);
            // The held candidate may include this player; invalidate it so we recompute next tick.
            InvalidateHeldIfContains(def.Name, player);
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
                _held.Remove(def.Name);
                continue;
            }

            int poolSize = Math.Min(fullSnapshot.Count, def.LookAheadWindow);
            var snapshot = poolSize == fullSnapshot.Count
                ? fullSnapshot
                : SliceFront(fullSnapshot, poolSize);

            var longestWait = now - snapshot[0].EnqueuedAt;
            double minQuality = def.QualityPolicy.MinQuality(longestWait);

            var current = FindBestRespectingGroups(snapshot, def, minQuality);
            if (current is null)
            {
                _held.Remove(def.Name);
                continue;
            }

            // Update the held candidate if quality improved (or none was held yet).
            if (_held.TryGetValue(def.Name, out var prior))
            {
                if (current.Quality > prior.Result.Quality)
                    _held[def.Name] = prior with { Result = current };
            }
            else
            {
                _held[def.Name] = new HeldCandidate(current, now);
            }

            var held = _held[def.Name];
            bool ceilingHit = held.Result.Quality >= def.QualityCeiling;
            bool windowElapsed = now - held.HeldSince >= def.HoldWindow;
            bool noLookaheadHeadroom = poolSize == def.LookAheadWindow;

            // Pop when: ceiling hit, window expired, or pool is already saturated (no further
            // arrivals can be considered without exceeding LookAheadWindow).
            if (!ceilingHit && !windowElapsed && !noLookaheadHeadroom) continue;

            var chosen = held.Result;
            _held.Remove(def.Name);

            for (int t = 0; t < chosen.Teams.Count; t++)
                for (int j = 0; j < chosen.Teams[t].Count; j++)
                    DequeueEverywhere(chosen.Teams[t][j]);

            return new MatchProposal(def.Name, def.Shape, chosen.Teams, chosen.Quality, now);
        }

        return null;
    }

    private BalanceResult? FindBestRespectingGroups(IReadOnlyList<QueueEntry> snapshot, QueueDefinition def, double minQuality)
    {
        var grouped = _balancer.FindBest(snapshot, def.Shape, _quality, requireGroupsTogether: true);
        if (grouped is not null && grouped.Quality >= minQuality) return grouped;

        var split = _balancer.FindBest(snapshot, def.Shape, _quality, requireGroupsTogether: false);
        if (split is not null && split.Quality >= minQuality) return split;
        return null;
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
}
