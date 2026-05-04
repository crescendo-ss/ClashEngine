using System;
using System.Collections.Generic;

namespace ClashEngine.Adapter;

/// <summary>
/// Hands out a per-match base freq so concurrent matches in the same arena don't all park their
/// teams on freqs 100/200. Each match gets a contiguous block of <c>numTeams</c> freqs starting at
/// the allocated base; the next allocation in the same arena advances by <c>numTeams * 100</c>,
/// wrapping after <see cref="MaxFreq"/>. With 2-team matches and the default 100..2000 range that
/// gives 10 distinct freq slots before any wrap-around collision.
/// </summary>
/// <remarks>
/// Allocation is keyed by arena name so a busy arena's rotation doesn't bleed into a neighbor's
/// numbering. The freq base for an active match is also indexed by <c>matchId</c> so the LVZ
/// team adapter and the freq-lock advisor can ask for the match's actual base instead of
/// re-deriving it from the static team-index convention. Single-threaded by design — all callers
/// run on the SS mainloop.
/// </remarks>
public sealed class MatchFreqAllocator
{
    public const short BaseFreq = 100;
    public const short FreqStep = 100;
    public const short MaxFreq = 2000;

    private readonly Dictionary<string, short> _nextByArena = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, short> _baseByMatch = new();

    /// <summary>
    /// Reserve a freq base for <paramref name="matchId"/> in <paramref name="arenaName"/>. Returns
    /// the base; <c>FreqOf(matchId, t) == base + t * 100</c>. Subsequent allocations in the same
    /// arena advance by <c>numTeams * 100</c> and wrap.
    /// </summary>
    public short Allocate(Guid matchId, string? arenaName, int numTeams)
    {
        if (numTeams <= 0) return BaseFreq;
        var arenaKey = arenaName ?? string.Empty;
        if (!_nextByArena.TryGetValue(arenaKey, out var current)) current = BaseFreq;

        short block = (short)(numTeams * FreqStep);
        // Largest base that still fits all numTeams freqs at or below MaxFreq.
        short maxStart = (short)(MaxFreq - (numTeams - 1) * FreqStep);
        if (current > maxStart || current < BaseFreq) current = BaseFreq;

        short next = (short)(current + block);
        if (next > maxStart) next = BaseFreq;
        _nextByArena[arenaKey] = next;

        _baseByMatch[matchId] = current;
        return current;
    }

    /// <summary>
    /// Returns the freq base previously reserved via <see cref="Allocate"/>, or
    /// <see cref="BaseFreq"/> if the match has no entry (e.g. an old match created before the
    /// allocator was wired in).
    /// </summary>
    public short GetBase(Guid matchId) =>
        _baseByMatch.TryGetValue(matchId, out var b) ? b : BaseFreq;

    /// <summary>Convenience: <c>GetBase(matchId) + teamIdx * 100</c>.</summary>
    public short FreqOf(Guid matchId, int teamIdx) =>
        (short)(GetBase(matchId) + teamIdx * FreqStep);

    /// <summary>Drop the match's base entry. The arena's rotating counter is not reset.</summary>
    public void Release(Guid matchId) => _baseByMatch.Remove(matchId);
}
