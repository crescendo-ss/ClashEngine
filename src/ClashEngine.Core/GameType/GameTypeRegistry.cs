using System;
using System.Collections.Generic;

namespace ClashEngine.Core.GameType;

/// <summary>
/// Flat, globally-scoped registry of game-type definitions. Each entry is tagged with the arena
/// whose <c>[ClashEngine]</c> section contributed it, so a re-attach of that arena re-commits its
/// own entries (same-source replace) and so collisions name the responsible arena.
/// </summary>
/// <remarks>
/// <para><b>Stickiness.</b> The registry itself supports <see cref="Remove"/>, but the host treats
/// game types as <i>process-lifetime sticky</i>: it does NOT remove them when an arena detaches.
/// Game types are globally referenceable (a queue in any arena may name a game type declared in
/// any other), so keeping them registered after the declaring arena detaches means dependent
/// queues elsewhere keep resolving, and the name stays owned by its first definer (a later arena
/// cannot redefine it). <see cref="Remove"/> remains for explicit teardown / tests.</para>
///
/// <para>Identity is the gametype <see cref="GameTypeDef.Name"/> string -- the same identifier
/// the stats server accepts via the <c>gametype</c> POST and the <c>match.gameType</c> field
/// on the v5 match-upload schema. There is no separate numeric id.</para>
///
/// <para>Collision rules:</para>
/// <list type="bullet">
///   <item>Two contributions with the same name from the same source: idempotent (no-op).</item>
///   <item>Two contributions with the same name from different sources: rejected with an
///         error -- the first contribution wins, the loser must rename.</item>
///   <item>An incoming set with duplicate names within itself: rejected.</item>
/// </list>
///
/// <para>The "id change rejection" rule that used to live here is gone with <c>GameTypeId</c>:
/// stats persistence is now keyed by the name string, and the stats server is the source of
/// truth for versioning. A rename in conf is treated the same way as a new gametype on the
/// server side -- the operator must POST it with its original arena to keep its rating rows.</para>
///
/// <para>All mutations go through <see cref="ReplaceArenaContribution"/>, which performs
/// validation against the projected post-swap state (excluding the source's existing
/// contribution) and either commits atomically or leaves the registry unchanged with a
/// returned error list. A failed hot reload never leaves the registry in a torn state.</para>
/// </remarks>
public sealed class GameTypeRegistry
{
    private readonly Dictionary<string, GameTypeDef> _byName = new(StringComparer.OrdinalIgnoreCase);

    // Track source arena per entry. Used to scope same-source replace on re-attach and to name the
    // responsible arena in collision errors. (A null value is the legacy not-tied-to-an-arena tag;
    // the host no longer contributes one.)
    private readonly Dictionary<string, string?> _sourceByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a game type by name (case-insensitive). Returns <see langword="false"/> if not registered.
    /// </summary>
    public bool TryGet(string name, out GameTypeDef def)
    {
        var found = _byName.TryGetValue(name, out var d);
        def = d!;
        return found;
    }

    public bool Contains(string name) => _byName.ContainsKey(name);

    public IEnumerable<GameTypeDef> Definitions => _byName.Values;

    public int Count => _byName.Count;

    /// <summary>
    /// Source-arena tag for a registered entry (<see langword="null"/> = zone-wide). Returns
    /// <see langword="false"/> if no entry by that name exists.
    /// </summary>
    public bool TryGetSource(string name, out string? sourceArena)
    {
        if (_sourceByName.TryGetValue(name, out var s)) { sourceArena = s; return true; }
        sourceArena = null;
        return false;
    }

    /// <summary>
    /// Atomically replaces the set of game types contributed by <paramref name="sourceArena"/>
    /// (use <see langword="null"/> for the zone-wide contribution). Validates the projected
    /// post-swap state against entries from other sources and rejects on collision; on rejection
    /// the registry is unchanged and the returned <paramref name="errors"/> describes what was
    /// rejected. Returns <see langword="true"/> on commit.
    /// </summary>
    public bool ReplaceArenaContribution(
        string? sourceArena,
        IReadOnlyList<GameTypeDef> newDefs,
        out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(newDefs);

        var errorList = new List<string>();

        // 1. Internal consistency: no duplicate names within the incoming set.
        var byNameIncoming = new Dictionary<string, GameTypeDef>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < newDefs.Count; i++)
        {
            var d = newDefs[i];
            if (byNameIncoming.ContainsKey(d.Name))
            {
                errorList.Add($"GameType '{d.Name}' appears twice in {SourceLabel(sourceArena)}.");
                continue;
            }
            byNameIncoming[d.Name] = d;
        }

        // 2. Name collision against entries owned by OTHER sources. This source's own existing
        //    entries are about to be replaced, so they don't conflict with themselves.
        foreach (var existing in _byName)
        {
            string? existingSource = _sourceByName.TryGetValue(existing.Key, out var s) ? s : null;
            if (NullableStringEquals(existingSource, sourceArena)) continue;
            if (byNameIncoming.ContainsKey(existing.Key))
            {
                errorList.Add(
                    $"GameType '{existing.Value.Name}' is already defined by {SourceLabel(existingSource)}; " +
                    $"{SourceLabel(sourceArena)} attempted to redefine it.");
            }
        }

        if (errorList.Count > 0)
        {
            errors = errorList;
            return false;
        }

        // 3. Commit: remove this source's previous entries, then add the new ones.
        var toRemove = new List<string>();
        foreach (var kvp in _sourceByName)
            if (NullableStringEquals(kvp.Value, sourceArena)) toRemove.Add(kvp.Key);
        foreach (var k in toRemove)
        {
            _byName.Remove(k);
            _sourceByName.Remove(k);
        }
        foreach (var d in byNameIncoming.Values)
        {
            _byName[d.Name] = d;
            _sourceByName[d.Name] = sourceArena;
        }

        errors = Array.Empty<string>();
        return true;
    }

    /// <summary>
    /// Removes every entry contributed by <paramref name="sourceArena"/> (<see langword="null"/> =
    /// zone-wide contribution). Returns the names of the removed entries so callers can fire
    /// dependent cleanup (e.g. dropping queues that referenced them).
    /// </summary>
    public IReadOnlyList<string> Remove(string? sourceArena)
    {
        var removed = new List<string>();
        foreach (var kvp in _sourceByName)
            if (NullableStringEquals(kvp.Value, sourceArena)) removed.Add(kvp.Key);
        foreach (var k in removed)
        {
            _byName.Remove(k);
            _sourceByName.Remove(k);
        }
        return removed;
    }

    private static bool NullableStringEquals(string? s, string? t)
    {
        if (s is null && t is null) return true;
        if (s is null || t is null) return false;
        return string.Equals(s, t, StringComparison.OrdinalIgnoreCase);
    }

    private static string SourceLabel(string? sourceArena) =>
        sourceArena is null ? "global.conf [ClashEngine] (zone-wide)" : $"arena '{sourceArena}' [ClashEngine]";
}
