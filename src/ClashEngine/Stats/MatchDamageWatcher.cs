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
/// zero. <see cref="ClashStatsTelemetry"/> calls <see cref="WatchAll"/> at match start,
/// <see cref="Rewatch"/> whenever a participant returns to the match mid-game, and
/// <see cref="UnwatchAll"/> at match end so the watch is symmetrically released.
/// </summary>
/// <remarks>
/// <para>If <paramref name="watchDamage"/> is <see langword="null"/> -- the SS module graph
/// didn't load <c>IWatchDamage</c> -- all methods are no-ops and damage stats remain zero.
/// The host logs that case once at module load.</para>
///
/// <para>Damage reporting is client-side: the victim's own client only sends C2S Damage
/// packets after the server's <c>ToggleDamage(on)</c>, which SS's <c>WatchDamage</c> sends
/// solely on the watch count's 0-&gt;1 transition -- and that client flag does not survive an
/// arena re-entry or a reconnect. That's why a single watch at match start isn't enough and
/// every return needs <see cref="Rewatch"/>.</para>
///
/// <para><see cref="_watched"/> maps each participant to the exact <see cref="Player"/> the
/// watch was added on. A reconnect produces a different <see cref="Player"/> whose
/// <c>WatchDamage</c> per-player data starts at zero, so releasing against it would drive
/// SS's unguarded <c>CallbackWatchCount</c> negative -- which permanently suppresses the
/// 0-&gt;1 toggle for that player's later matches. <see cref="UnwatchAll"/> therefore only
/// releases when the currently-resolved player is the same object the watch went onto; a
/// stale object's count died with the connection and needs no release.</para>
/// </remarks>
public sealed class MatchDamageWatcher
{
    private readonly IWatchDamage? _watchDamage;
    private readonly PlayerKeyResolver _resolver;
    private readonly Dictionary<PlayerKey, Player> _watched = new();

    public MatchDamageWatcher(IWatchDamage? watchDamage, PlayerKeyResolver resolver)
    {
        _watchDamage = watchDamage;
        _resolver = resolver ?? throw new System.ArgumentNullException(nameof(resolver));
    }

    /// <summary>Whether the watcher is wired to a real <see cref="IWatchDamage"/>; false means
    /// damage stats will accumulate as zero.</summary>
    public bool IsActive => _watchDamage is not null;

    /// <summary>Add a damage-callback watch for every resolvable player on
    /// <paramref name="teams"/>. Idempotent on a per-player basis; players that fail to
    /// resolve are left untracked so <see cref="UnwatchAll"/> won't release a watch that was
    /// never added (a late <see cref="Rewatch"/> can still pick them up).</summary>
    public void WatchAll(IReadOnlyList<IReadOnlyList<PlayerKey>> teams)
    {
        if (_watchDamage is null) return;
        for (int t = 0; t < teams.Count; t++)
        {
            for (int j = 0; j < teams[t].Count; j++)
            {
                var key = teams[t][j];
                if (_resolver.Resolve(key) is not { } player) continue;
                if (_watched.TryGetValue(key, out var existing) && ReferenceEquals(existing, player)) continue;
                _watchDamage.AddCallbackWatch(player);
                _watched[key] = player;
            }
        }
    }

    /// <summary>
    /// Re-arm the damage watch for a participant who returned to the match mid-game. On the
    /// same connection the watch is cycled (remove + add) to force the watch count through
    /// 0-&gt;1 so SS re-sends <c>ToggleDamage(on)</c> -- required after an arena re-entry,
    /// where the server-side count stayed hot but the client's reporting flag reset. After a
    /// reconnect the key resolves to a new <see cref="Player"/> with a fresh (zero) count, so
    /// a plain add performs the 0-&gt;1 toggle; the stale object is left alone.
    /// </summary>
    public void Rewatch(PlayerKey key)
    {
        if (_watchDamage is null) return;
        if (_resolver.Resolve(key) is not { } player) return;

        if (_watched.TryGetValue(key, out var existing) && ReferenceEquals(existing, player))
            _watchDamage.RemoveCallbackWatch(player);
        _watchDamage.AddCallbackWatch(player);
        _watched[key] = player;
    }

    /// <summary>Symmetric release of the watches added by <see cref="WatchAll"/> /
    /// <see cref="Rewatch"/>. A watch is only released when the key still resolves to the
    /// same <see cref="Player"/> it was added on; after a reconnect the old object's watch
    /// state died with the connection and releasing against the new object would corrupt
    /// its (never-incremented) count.</summary>
    public void UnwatchAll(IReadOnlyList<IReadOnlyList<PlayerKey>> teams)
    {
        if (_watchDamage is null) return;
        for (int t = 0; t < teams.Count; t++)
        {
            for (int j = 0; j < teams[t].Count; j++)
            {
                var key = teams[t][j];
                if (!_watched.Remove(key, out var watchedPlayer)) continue;
                if (_resolver.Resolve(key) is { } player && ReferenceEquals(player, watchedPlayer))
                    _watchDamage.RemoveCallbackWatch(player);
            }
        }
    }
}
