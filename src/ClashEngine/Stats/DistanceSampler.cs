using System;
using System.Collections.Generic;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Stats;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Utilities;

namespace ClashEngine.Stats;

/// <summary>
/// Drives periodic distance-to-nearest-enemy sampling for every active match. On each tick of
/// its mainloop timer, walks every active match in <see cref="MatchmakingEngine.ActiveMatches"/>
/// and records a sample per in-arena player: the pixel distance to the closest live enemy on
/// an opposing team.
/// </summary>
/// <remarks>
/// <para>"Live enemy" = a participant on a different team in the same match who is currently
/// connected via <see cref="PlayerKeyResolver"/> AND has lives remaining (or unlimited). If no
/// live enemy can be located -- e.g. all opponents have been eliminated, or none are currently
/// in-arena -- the player's sample is skipped that tick rather than emitting a sentinel value.</para>
///
/// <para>Sampler runs on the mainloop. Cadence is configurable via
/// <c>[ClashEngine] DistanceSampleHz</c> (default 5). Setting <c>DistanceSampleHz = 0</c>
/// disables sampling entirely and the host won't even register the timer.</para>
///
/// <para>The list of samples per player is appended in chronological order on
/// <see cref="PlayerStats.DistanceSamples"/> and serialized into the match envelope as the
/// <c>distanceSamples</c> array.</para>
/// </remarks>
public sealed class DistanceSampler
{
    private const string LogCategory = nameof(DistanceSampler);

    private readonly MatchmakingEngine _engine;
    private readonly MatchStatsRegistry _registry;
    private readonly PlayerKeyResolver _resolver;
    private readonly IMainloopTimer _timer;
    private readonly ILogManager _log;
    private readonly int _periodMs;
    private bool _registered;

    public DistanceSampler(
        MatchmakingEngine engine,
        MatchStatsRegistry registry,
        PlayerKeyResolver resolver,
        IMainloopTimer timer,
        ILogManager log,
        int hz)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        if (hz < 1 || hz > 50)
            throw new ArgumentOutOfRangeException(nameof(hz), "Hz must be between 1 and 50.");
        _periodMs = 1000 / hz;
    }

    /// <summary>Period (ms) between samples, derived from the configured Hz.</summary>
    public int PeriodMs => _periodMs;

    public void Register()
    {
        if (_registered) return;
        _timer.SetTimer(OnTick, initialDelay: _periodMs, interval: _periodMs, key: this);
        _registered = true;
    }

    public void Unregister()
    {
        if (!_registered) return;
        _timer.ClearTimer(OnTick, key: this);
        _registered = false;
    }

    private bool OnTick()
    {
        try
        {
            uint nowTick = (uint)ServerTick.Now;
            foreach (var (matchId, match) in _engine.ActiveMatches)
            {
                if (match.State != MatchState.Live) continue;
                SampleMatch(match, nowTick);
            }
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory, $"Tick failed: {ex}");
        }
        return true;   // keep firing
    }

    private void SampleMatch(ActiveMatch match, uint nowTick)
    {
        // Resolve every team's players to live SS Player references up front so we can do
        // pairwise nearest lookups without re-resolving N*M times per tick.
        // Slot (team, slot) -> Player or null. Same shape as match.Teams.
        var resolved = new Player?[match.Teams.Count][];
        for (int t = 0; t < match.Teams.Count; t++)
        {
            var team = match.Teams[t];
            var arr = new Player?[team.Count];
            for (int j = 0; j < team.Count; j++)
            {
                var key = team[j];
                var p = _resolver.Resolve(key);
                if (p is null) { arr[j] = null; continue; }
                if (!IsLive(match, key, p)) { arr[j] = null; continue; }
                arr[j] = p;
            }
            resolved[t] = arr;
        }

        for (int t = 0; t < match.Teams.Count; t++)
        {
            var team = match.Teams[t];
            var teamPlayers = resolved[t];
            for (int j = 0; j < team.Count; j++)
            {
                var self = teamPlayers[j];
                if (self is null) continue;   // not in-arena or out of lives

                float bestSq = float.PositiveInfinity;
                for (int t2 = 0; t2 < match.Teams.Count; t2++)
                {
                    if (t2 == t) continue;
                    var enemyPlayers = resolved[t2];
                    for (int k = 0; k < enemyPlayers.Length; k++)
                    {
                        var enemy = enemyPlayers[k];
                        if (enemy is null) continue;
                        float dx = self.Position.X - enemy.Position.X;
                        float dy = self.Position.Y - enemy.Position.Y;
                        float sq = dx * dx + dy * dy;
                        if (sq < bestSq) bestSq = sq;
                    }
                }

                if (float.IsPositiveInfinity(bestSq)) continue;   // no live enemy this tick
                float dist = MathF.Sqrt(bestSq);
                _registry.OnDistanceSample(team[j], nowTick, dist);
            }
        }
    }

    /// <summary>
    /// True if <paramref name="player"/> should contribute to nearest-enemy calculations:
    /// in-arena (not spec) and has lives remaining (or unlimited). Players in grace, abandoned,
    /// or knocked out are excluded so their last-known position doesn't influence samples.
    /// </summary>
    private static bool IsLive(ActiveMatch match, PlayerKey key, Player player)
    {
        if (player.Ship == ShipType.Spec) return false;
        if (player.Status != PlayerState.Playing) return false;
        if (!match.Statuses.TryGetValue(key, out var status)) return false;
        if (status != PlayerStatus.Active) return false;
        if (match.LivesPerPlayer.HasValue && match.ExitedAt.ContainsKey(key))
            return false;
        return true;
    }
}
