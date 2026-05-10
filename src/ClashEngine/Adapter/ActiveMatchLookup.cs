using System;
using ClashEngine.Core;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Stats;
using ClashEngine.Lvz;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Interfaces;
using SS.Packets.Game;

namespace ClashEngine.Adapter;

/// <summary>
/// Resolves "the ClashEngine match this requester is associated with" for player-initiated
/// commands like <c>?chart</c> and <c>?items</c>. Resolution order:
/// (1) requester is participating -> their match;
/// (2) requester is in spec -> the match they're focused on (via <see cref="IMatchFocus"/>);
/// (3) requester is in spec -> any active match with at least one participant in their arena.
/// </summary>
public sealed class ActiveMatchLookup
{
    private readonly IComponentBroker _broker;
    private readonly MatchmakingEngine _engine;
    private readonly MatchStatsRegistry _registry;
    private readonly PlayerKeyResolver _resolver;

    public ActiveMatchLookup(
        IComponentBroker broker,
        MatchmakingEngine engine,
        MatchStatsRegistry registry,
        PlayerKeyResolver resolver)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public readonly record struct Result(ActiveMatch Match, StatsRecorder Recorder, PlayerKey SelfKey);

    /// <summary>
    /// Returns the match + recorder for <paramref name="requester"/>, or null if the requester
    /// can't be resolved to a player key or no active match applies.
    /// <paramref name="errorMessage"/> carries the user-facing reason on null returns and is
    /// safe to forward via <c>IChat.SendMessage</c>.
    /// </summary>
    public Result? TryFind(Player requester, out string errorMessage)
    {
        if (_resolver.KeyOf(requester) is not PlayerKey selfKey)
        {
            errorMessage = "Could not resolve your player key.";
            return null;
        }

        Guid? matchId = _registry.MatchIdOf(selfKey);
        if (matchId is null && requester.Ship == ShipType.Spec)
            matchId = ResolveSpectatedMatchId(requester) ?? FindMatchInArena(requester.Arena);

        if (matchId is null
            || !_engine.ActiveMatches.TryGetValue(matchId.Value, out var match)
            || !_registry.ActiveRecorders.TryGetValue(matchId.Value, out var recorder))
        {
            errorMessage = "No active match to show.";
            return null;
        }

        errorMessage = string.Empty;
        return new Result(match, recorder, selfKey);
    }

    private Guid? ResolveSpectatedMatchId(Player player)
    {
        var focus = _broker.GetInterface<IMatchFocus>();
        if (focus is null) return null;
        try
        {
            return focus.GetFocusedMatch(player) is ClashMatchData cmd ? cmd.Match.MatchId : null;
        }
        finally
        {
            _broker.ReleaseInterface(ref focus);
        }
    }

    private Guid? FindMatchInArena(Arena? arena)
    {
        if (arena is null) return null;
        foreach (var (id, m) in _engine.ActiveMatches)
        {
            for (int t = 0; t < m.Teams.Count; t++)
            {
                for (int j = 0; j < m.Teams[t].Count; j++)
                {
                    var p = _resolver.Resolve(m.Teams[t][j]);
                    if (p?.Arena is { } a && string.Equals(a.Name, arena.Name, StringComparison.OrdinalIgnoreCase))
                        return id;
                }
            }
        }
        return null;
    }
}
