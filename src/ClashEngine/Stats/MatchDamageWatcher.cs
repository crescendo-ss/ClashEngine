using System.Collections.Generic;
using ClashEngine.Adapter;
using ClashEngine.Core.Identity;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// Owns the per-match <see cref="IWatchDamage"/> watches. <see cref="PlayerDamageCallback"/>
/// only fires for players that have at least one outstanding callback watch (see SS Core's
/// <c>WatchDamage</c> module), so without this gate every damage event would be silently
/// dropped and stats like <c>DamageDealt</c>/<c>DamageTaken</c>/<c>HitCount</c> would stay at
/// zero. <see cref="ClashStatsTelemetry"/> calls <see cref="WatchAll"/> at match start and
/// <see cref="UnwatchAll"/> at match end so the watch is symmetrically released.
/// </summary>
/// <remarks>
/// <para>If <paramref name="watchDamage"/> is <see langword="null"/> -- the SS module graph
/// didn't load <c>IWatchDamage</c> -- both methods are no-ops and damage stats remain zero.
/// The host logs that case once at module load.</para>
///
/// <para>Player resolution can fail (player disconnected mid-match); the <c>WatchDamage</c>
/// module cleans up dropped players on its own, so a missing <c>Player</c> here just means we
/// skip without trying to release. The <see cref="_watched"/> set tracks who we successfully
/// added so <see cref="UnwatchAll"/> matches up only on those.</para>
/// </remarks>
public sealed class MatchDamageWatcher
{
    private readonly IWatchDamage? _watchDamage;
    private readonly PlayerKeyResolver _resolver;
    private readonly HashSet<PlayerKey> _watched = new();

    public MatchDamageWatcher(IWatchDamage? watchDamage, PlayerKeyResolver resolver)
    {
        _watchDamage = watchDamage;
        _resolver = resolver ?? throw new System.ArgumentNullException(nameof(resolver));
    }

    /// <summary>Whether the watcher is wired to a real <see cref="IWatchDamage"/>; false means
    /// damage stats will accumulate as zero.</summary>
    public bool IsActive => _watchDamage is not null;

    /// <summary>Add a damage-callback watch for every resolvable player on
    /// <paramref name="teams"/>. Idempotent on a per-player basis.</summary>
    public void WatchAll(IReadOnlyList<IReadOnlyList<PlayerKey>> teams)
    {
        if (_watchDamage is null) return;
        for (int t = 0; t < teams.Count; t++)
        {
            for (int j = 0; j < teams[t].Count; j++)
            {
                var key = teams[t][j];
                if (!_watched.Add(key)) continue;
                if (_resolver.Resolve(key) is { } player)
                    _watchDamage.AddCallbackWatch(player);
            }
        }
    }

    /// <summary>Symmetric release of the watches added by an earlier
    /// <see cref="WatchAll"/>. Players the watcher never added (e.g. resolution failed at start
    /// time) are skipped.</summary>
    public void UnwatchAll(IReadOnlyList<IReadOnlyList<PlayerKey>> teams)
    {
        if (_watchDamage is null) return;
        for (int t = 0; t < teams.Count; t++)
        {
            for (int j = 0; j < teams[t].Count; j++)
            {
                var key = teams[t][j];
                if (!_watched.Remove(key)) continue;
                if (_resolver.Resolve(key) is { } player)
                    _watchDamage.RemoveCallbackWatch(player);
            }
        }
    }
}
