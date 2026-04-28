using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Queue;
using SS.Matchmaking.League;
using SS.Matchmaking.TeamVersus;

namespace ClashEngine.Lvz;

/// <summary>
/// <see cref="ITeam"/> adapter exposing one of an <see cref="Core.Matches.ActiveMatch"/>'s
/// teams. Score reads through to <c>match.KillsByTeam</c> on every access so MatchLvz's
/// scoreboard refresh always reflects live engine state.
/// </summary>
internal sealed class ClashTeam : ITeam
{
    private readonly ClashMatchData _matchData;
    private readonly ReadOnlyCollection<IPlayerSlot> _slotsRO;

    public ClashTeam(ClashMatchData matchData, int teamIdx, IReadOnlyList<PlayerKey> slotKeys)
    {
        ArgumentNullException.ThrowIfNull(matchData);
        ArgumentNullException.ThrowIfNull(slotKeys);
        _matchData = matchData;
        TeamIdx = teamIdx;
        Freq = QueueDefinition.FreqOf(teamIdx);

        var slots = new IPlayerSlot[slotKeys.Count];
        for (int j = 0; j < slotKeys.Count; j++)
            slots[j] = new ClashPlayerSlot(_matchData, this, slotIdx: j, slotKeys[j]);
        _slotsRO = Array.AsReadOnly(slots);
    }

    public IMatchData MatchData => _matchData;
    public int TeamIdx { get; }
    public short Freq { get; }
    public LeagueTeamInfo? LeagueTeam => null;
    public ReadOnlyCollection<IPlayerSlot> Slots => _slotsRO;

    /// <summary>Live-read of team kills. Capped to short (MatchLvz expects short and team kills
    /// would overflow long before approaching that ceiling).</summary>
    public short Score
    {
        get
        {
            var kbt = _matchData.Match.KillsByTeam;
            if (TeamIdx < 0 || TeamIdx >= kbt.Count) return 0;
            int v = kbt[TeamIdx];
            if (v < short.MinValue) return short.MinValue;
            if (v > short.MaxValue) return short.MaxValue;
            return (short)v;
        }
    }
}
