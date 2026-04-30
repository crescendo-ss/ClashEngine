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
    /// <summary>Pixel slack around the warp anchor that still counts as "at spawn". Continuum
    /// warps land the ship at the tile-corner pixel, but the client may report a
    /// sub-tile-rounded value or a slight offset; one tile of slack covers normal jitter
    /// without false-positive movement detection.</summary>
    private const int AnchorSlackPixels = 16;

    private struct State
    {
        public sbyte? InitialRotation;
        public short? InitialX;
        public short? InitialY;
        public bool IsIdle;
        /// <summary>True iff <see cref="InitialX"/>/<see cref="InitialY"/> were pre-set by
        /// <see cref="AnchorAt"/> (warp destination) rather than seeded from a position packet.
        /// While set, position packets that land far from the anchor are treated as stale
        /// pre-warp packets and ignored; the first packet near the anchor seeds rotation
        /// (and re-seeds position to the actual reported pixel) and clears this flag.</summary>
        public bool AnchorPending;
    }

    private readonly Dictionary<PlayerKey, State> _byPlayer = new();

    /// <summary>Adds <paramref name="player"/> to the watch list, initially marked idle.</summary>
    public void RegisterParticipant(PlayerKey player) =>
        _byPlayer[player] = new State { IsIdle = true };

    /// <summary>Whether <paramref name="player"/> is still marked idle.</summary>
    public bool IsIdle(PlayerKey player) =>
        _byPlayer.TryGetValue(player, out var s) && s.IsIdle;

    /// <summary>Pre-anchor the player at the warp destination so stale in-flight position
    /// packets (sent by the client before it received the warp) don't seed a wrong anchor
    /// and trigger false-positive movement detection. <paramref name="xPixels"/> and
    /// <paramref name="yPixels"/> must match the position-packet pixel space (1 tile = 16
    /// pixels). The first incoming packet whose X/Y is within <see cref="AnchorSlackPixels"/>
    /// of this anchor seeds rotation; packets outside that window are dropped as stale.</summary>
    public void AnchorAt(PlayerKey player, short xPixels, short yPixels)
    {
        if (!_byPlayer.TryGetValue(player, out var state)) return;
        if (!state.IsIdle) return;
        state.InitialX = xPixels;
        state.InitialY = yPixels;
        state.InitialRotation = null;
        state.AnchorPending = true;
        _byPlayer[player] = state;
    }

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

        if (state.AnchorPending)
        {
            // Drop pre-warp packets that report the ship's old position. Continuum sends the
            // warp S2C; client position packets sent before the client processes that warp
            // arrive here with stale coords. They'll be far from the warp anchor.
            int dx = x - state.InitialX!.Value;
            int dy = y - state.InitialY!.Value;
            if (dx > AnchorSlackPixels || dx < -AnchorSlackPixels
                || dy > AnchorSlackPixels || dy < -AnchorSlackPixels)
            {
                return false;
            }

            // First post-warp packet. Treat the actual reported pixel coords as the seed
            // (Continuum may report a sub-pixel offset within slack), capture the bot's
            // current rotation, and clear the anchor-pending flag so subsequent packets go
            // through the normal movement check below.
            state.InitialRotation = rotation;
            state.InitialX = x;
            state.InitialY = y;
            state.AnchorPending = false;
            // Weapon fire on the first post-warp packet still counts as movement.
            if (weapon != WeaponCodes.Null && weapon != WeaponCodes.Repel)
            {
                state.IsIdle = false;
                _byPlayer[player] = state;
                return true;
            }
            _byPlayer[player] = state;
            return false;
        }

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
