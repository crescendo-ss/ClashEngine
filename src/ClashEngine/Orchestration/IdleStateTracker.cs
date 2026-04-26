using System.Collections.Generic;
using ClashEngine.Core.Identity;
using SS.Packets.Game;

namespace ClashEngine.Orchestration;

/// <summary>
/// Per-match staging-phase idleness tracker. Each participant starts marked idle; their first
/// observed delta from the seed position-packet (rotation, x, y, or weapon-fire other than
/// repel) flips them to non-idle. <see cref="MatchOrchestrator"/> consults
/// <see cref="GetStillIdle"/> at the end of staging to decide whether to fail the match.
/// </summary>
internal sealed class IdleStateTracker
{
    private struct State
    {
        public sbyte? InitialRotation;
        public short? InitialX;
        public short? InitialY;
        public bool IsIdle;
    }

    private readonly Dictionary<PlayerKey, State> _byPlayer = new();

    /// <summary>Adds <paramref name="player"/> to the watch list, initially marked idle.</summary>
    public void RegisterParticipant(PlayerKey player) =>
        _byPlayer[player] = new State { IsIdle = true };

    /// <summary>Whether <paramref name="player"/> is still marked idle.</summary>
    public bool IsIdle(PlayerKey player) =>
        _byPlayer.TryGetValue(player, out var s) && s.IsIdle;

    /// <summary>
    /// Process a position packet. The first packet seeds the (rotation, x, y) anchor without
    /// flipping idle; subsequent packets that differ from the anchor (or carry a weapon other
    /// than null/repel) flip the player non-idle. Returns <see langword="true"/> exactly once
    /// per player -- on the packet that first detects movement -- so the caller can fire the
    /// "Got it -- you're ready" confirmation chat.
    /// </summary>
    public bool RecordPosition(PlayerKey player, sbyte rotation, short x, short y, WeaponCodes weapon)
    {
        if (!_byPlayer.TryGetValue(player, out var state)) return false;
        if (!state.IsIdle) return false;

        if (state.InitialRotation is null)
        {
            _byPlayer[player] = new State
            {
                InitialRotation = rotation,
                InitialX = x,
                InitialY = y,
                IsIdle = true,
            };
            return false;
        }

        bool moved = rotation != state.InitialRotation
                     || x != state.InitialX
                     || y != state.InitialY
                     || (weapon != WeaponCodes.Null && weapon != WeaponCodes.Repel);
        if (!moved) return false;

        state.IsIdle = false;
        _byPlayer[player] = state;
        return true;
    }

    /// <summary>Returns every participant whose idle flag is still set.</summary>
    public List<PlayerKey> GetStillIdle()
    {
        var afk = new List<PlayerKey>();
        foreach (var kvp in _byPlayer)
            if (kvp.Value.IsIdle) afk.Add(kvp.Key);
        return afk;
    }
}
