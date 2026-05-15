using System;
using System.Collections.Generic;

namespace ClashEngine.Core.GameType;

/// <summary>
/// Flat, globally-scoped registry of game-type definitions. Each entry is tagged with the
/// arena whose <c>[ClashEngine]</c> section contributed it (or <see langword="null"/> for
/// zone-wide contributions from global.conf) so the lifecycle wiring can remove that arena's
/// contribution when the arena is detached.
/// </summary>
/// <remarks>
/// <para>Collision rules ("force globally unique names" policy):</para>
/// <list type="bullet">
///   <item>Two contributions with the same name AND same byte <c>Id</c> AND same source: idempotent (no-op).</item>
///   <item>Two contributions with the same name but different byte <c>Id</c>: rejected.</item>
///   <item>Two contributions with the same byte <c>Id</c> but different names: rejected.</item>
///   <item>A reload that attempts to change an existing entry's byte <c>Id</c> is rejected, because
///         the <c>Id</c> is persisted as a key in the ratings store -- silently renumbering would
///         orphan historical rating rows.</item>
/// </list>
///
/// <para>All mutations go through <see cref="ReplaceArenaContribution"/>, which performs validation
/// against the projected post-swap state (excluding the source's existing contribution) and either
/// commits atomically or leaves the registry unchanged with a returned error list. This means a
/// failed hot reload never leaves the registry in a torn state.</para>
/// </remarks>
public sealed class GameTypeRegistry
{
    private readonly Dictionary<string, GameTypeDef> _byName = new(StringComparer.OrdinalIgnoreCase);

    // Track source arena per entry so detach can remove only that contribution. null key represents
    // the zone-wide contribution (global.conf [ClashEngine]).
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

        // 1. Internal consistency: no duplicate names or duplicate IDs within the incoming set.
        var byNameIncoming = new Dictionary<string, GameTypeDef>(StringComparer.OrdinalIgnoreCase);
        var byIdIncoming = new Dictionary<byte, GameTypeDef>();
        for (int i = 0; i < newDefs.Count; i++)
        {
            var d = newDefs[i];
            if (byNameIncoming.TryGetValue(d.Name, out var dupName))
            {
                if (dupName.Id != d.Id)
                    errorList.Add($"GameType '{d.Name}' appears twice in {SourceLabel(sourceArena)} with different IDs ({dupName.Id} vs {d.Id}).");
                continue;
            }
            byNameIncoming[d.Name] = d;
            if (byIdIncoming.TryGetValue(d.Id, out var dupId))
            {
                errorList.Add($"GameType Id={d.Id} used by both '{dupId.Name}' and '{d.Name}' in {SourceLabel(sourceArena)}.");
                continue;
            }
            byIdIncoming[d.Id] = d;
        }

        // 2. Validate against entries owned by OTHER sources (this source's existing entries are
        //    about to be replaced, so they don't conflict with themselves).
        foreach (var existing in _byName)
        {
            string existingSource = _sourceByName.TryGetValue(existing.Key, out var s) ? (s ?? string.Empty) : string.Empty;
            bool isOurs = NullableStringEquals(s: existingSource == string.Empty ? null : existingSource, t: sourceArena);
            if (isOurs) continue;

            // Name collision: another source owns this name. Whatever Id we propose, this is rejected.
            if (byNameIncoming.TryGetValue(existing.Key, out var incomingForName))
            {
                errorList.Add(
                    $"GameType '{existing.Value.Name}' is already defined by {SourceLabel(existing.Value, _sourceByName[existing.Key])}; " +
                    $"{SourceLabel(sourceArena)} attempted to redefine it (Id={incomingForName.Id}).");
            }
        }
        // Id collisions against entries owned by other sources.
        foreach (var existing in _byName)
        {
            string? existingSource = _sourceByName.TryGetValue(existing.Key, out var s) ? s : null;
            bool isOurs = NullableStringEquals(existingSource, sourceArena);
            if (isOurs) continue;
            if (byIdIncoming.TryGetValue(existing.Value.Id, out var incomingForId)
                && !string.Equals(incomingForId.Name, existing.Value.Name, StringComparison.OrdinalIgnoreCase))
            {
                errorList.Add(
                    $"GameType Id={existing.Value.Id} is already used by '{existing.Value.Name}' (owned by {SourceLabel(existing.Value, existingSource)}); " +
                    $"{SourceLabel(sourceArena)} attempted to assign it to '{incomingForId.Name}'.");
            }
        }

        // 3. Persistence safety: an entry owned by THIS source that survives the replace must keep
        //    its byte Id. If the operator renamed an Id under the same game-type name, reject --
        //    PersistRatingStore keys by Id, so silently renumbering would orphan rating rows.
        foreach (var kvp in _byName)
        {
            string? existingSource = _sourceByName.TryGetValue(kvp.Key, out var s) ? s : null;
            if (!NullableStringEquals(existingSource, sourceArena)) continue;
            if (byNameIncoming.TryGetValue(kvp.Key, out var incoming) && incoming.Id != kvp.Value.Id)
            {
                errorList.Add(
                    $"GameType '{kvp.Value.Name}' Id change rejected: was {kvp.Value.Id}, would become {incoming.Id}. " +
                    $"Rating data is keyed by Id; changing it would orphan historical rows. " +
                    $"To re-number safely, rename the game type as well.");
            }
        }

        if (errorList.Count > 0)
        {
            errors = errorList;
            return false;
        }

        // 4. Commit: remove this source's previous entries, then add the new ones.
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

    private static string SourceLabel(GameTypeDef def, string? sourceArena) => SourceLabel(sourceArena);
}
