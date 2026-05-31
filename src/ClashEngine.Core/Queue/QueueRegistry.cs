using System;
using System.Collections.Generic;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;

namespace ClashEngine.Core.Queue;

/// <summary>
/// Tracks all configured matchmaking queues, indexed by qualified name (case-insensitive).
/// </summary>
/// <remarks>
/// <para>Queues defined in a per-arena <c>[ClashEngine]</c> section (in arena.conf or anywhere
/// <c>#include</c>'d from it) are stored under the qualified name
/// "<c>{ownerArena}/{baseName}</c>" so two arenas can each declare a queue named e.g.
/// <c>3v3comp</c> without collision. Queues registered without an owner-arena (the legacy
/// test path) are keyed by their bare name.</para>
///
/// <para>All hot-reload mutations go through
/// <see cref="ReplaceArenaContribution(string, IReadOnlyList{QueueDefinition}, out IReadOnlyList{QueueDefinition}, out IReadOnlyList{QueueDefinition}, out IReadOnlyList{string})"/>.
/// On commit, the caller receives the lists of removed and added (and "changed", surfaced as
/// removed+added) queues so it can drop waiters from removed/changed queues with a chat notice
/// before publishing new state. Failed commits leave the registry unchanged.</para>
/// </remarks>
public sealed class QueueRegistry
{
    private readonly Dictionary<string, QueueDefinition> _byName = new(StringComparer.OrdinalIgnoreCase);

    public QueueDefinition Register(
        string name,
        MatchShape shape,
        PartitionQualityPolicy qualityPolicy,
        string gameType = "",
        Func<IMatchEndPolicy>? endPolicyFactory = null,
        Func<IGriefingHeuristic>? griefingHeuristicFactory = null,
        int vetoesRequired = 2,
        TimeSpan? vetoWindow = null,
        double ratingWeight = 1.0,
        string? matchArenaName = null,
        IReadOnlyList<IReadOnlyList<SpawnPoint>>? spawnSetByTeam = null,
        int? maxSpawnDriftTiles = null,
        bool warpOnSpawn = false,
        TimeSpan? stagingDuration = null,
        TimeSpan? countdownDuration = null,
        int? lookAheadWindow = null,
        bool promoteWinnersToFront = false,
        int maxConsecutiveDefenses = 3,
        TimeSpan? holdWindow = null,
        double qualityCeiling = 0.9,
        TimeSpan? knockoutSpecDelay = null,
        int? livesPerPlayer = null,
        TimeSpan? teamCollapseGrace = null,
        TimeSpan? shipChangeGracePeriod = null,
        TimeSpan? timeLimit = null,
        ItemsAction returnItemsAction = ItemsAction.Full,
        string? ownerArenaName = null,
        string? label = null,
        TimeSpan? afkDwellWarning = null,
        TimeSpan? afkDwellCull = null,
        TimeSpan? eliminationCooldown = null)
    {
        if (_byName.ContainsKey(name))
            throw new ArgumentException($"Queue '{name}' already registered.", nameof(name));

        var def = new QueueDefinition(
            name, shape, qualityPolicy, gameType,
            endPolicyFactory, griefingHeuristicFactory,
            vetoesRequired, vetoWindow, ratingWeight,
            matchArenaName, spawnSetByTeam, maxSpawnDriftTiles, warpOnSpawn,
            stagingDuration, countdownDuration,
            lookAheadWindow, promoteWinnersToFront, maxConsecutiveDefenses,
            holdWindow, qualityCeiling, knockoutSpecDelay,
            livesPerPlayer, teamCollapseGrace, shipChangeGracePeriod,
            timeLimit, returnItemsAction, ownerArenaName, label,
            afkDwellWarning, afkDwellCull, eliminationCooldown);
        _byName[name] = def;
        return def;
    }

    /// <summary>
    /// Adds a fully-constructed <see cref="QueueDefinition"/> under its qualified name. Throws if
    /// the name is already taken. Use this for arena-contributed queues where the parser has
    /// already pre-qualified the name.
    /// </summary>
    public QueueDefinition Add(QueueDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_byName.ContainsKey(definition.UniqueId))
            throw new ArgumentException($"Queue '{definition.UniqueId}' already registered.", nameof(definition));
        _byName[definition.UniqueId] = definition;
        return definition;
    }

    public bool TryGet(string name, out QueueDefinition definition)
    {
        var found = _byName.TryGetValue(name, out var d);
        definition = d!;
        return found;
    }

    public bool Contains(string name) => _byName.ContainsKey(name);

    public IEnumerable<QueueDefinition> Definitions => _byName.Values;

    public int Count => _byName.Count;

    /// <summary>
    /// Computes the diff that <see cref="ApplyArenaContribution"/> would produce, without
    /// mutating the registry. Lets the caller drain waiters from queues that are about to be
    /// removed BEFORE the swap (because the matcher's per-queue dequeue path requires the queue
    /// to still be in the registry). Returns <see langword="false"/> if the incoming set is
    /// internally inconsistent (duplicate names, mismatched owner-arena tags, etc.).
    /// </summary>
    public bool TryComputeArenaContributionDiff(
        string ownerArenaName,
        IReadOnlyList<QueueDefinition> newDefs,
        out IReadOnlyList<QueueDefinition> wouldRemove,
        out IReadOnlyList<QueueDefinition> wouldAdd,
        out IReadOnlyList<string> errors)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerArenaName);
        ArgumentNullException.ThrowIfNull(newDefs);

        var errorList = new List<string>();
        var byNameIncoming = new Dictionary<string, QueueDefinition>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < newDefs.Count; i++)
        {
            var d = newDefs[i];
            if (!string.Equals(d.OwnerArenaName, ownerArenaName, StringComparison.OrdinalIgnoreCase))
            {
                errorList.Add($"Queue '{d.BaseName}' has OwnerArenaName '{d.OwnerArenaName}' but is being contributed by arena '{ownerArenaName}'.");
                continue;
            }
            if (byNameIncoming.ContainsKey(d.UniqueId))
            {
                errorList.Add($"Queue '{d.BaseName}' appears twice in arena '{ownerArenaName}' [ClashEngine].");
                continue;
            }
            byNameIncoming[d.UniqueId] = d;
        }

        if (errorList.Count > 0)
        {
            wouldRemove = Array.Empty<QueueDefinition>();
            wouldAdd = Array.Empty<QueueDefinition>();
            errors = errorList;
            return false;
        }

        var existingOwned = new List<QueueDefinition>();
        foreach (var kvp in _byName)
        {
            if (string.Equals(kvp.Value.OwnerArenaName, ownerArenaName, StringComparison.OrdinalIgnoreCase))
                existingOwned.Add(kvp.Value);
        }
        var existingByName = new Dictionary<string, QueueDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in existingOwned) existingByName[d.UniqueId] = d;

        var removedList = new List<QueueDefinition>();
        var addedList = new List<QueueDefinition>();
        foreach (var d in existingOwned)
        {
            if (!byNameIncoming.TryGetValue(d.UniqueId, out var incoming))
                removedList.Add(d);
            else if (!QueueShapeMatches(d, incoming))
                removedList.Add(d);
        }
        foreach (var d in byNameIncoming.Values)
        {
            if (!existingByName.TryGetValue(d.UniqueId, out var prev))
                addedList.Add(d);
            else if (!QueueShapeMatches(prev, d))
                addedList.Add(d);
        }

        wouldRemove = removedList;
        wouldAdd = addedList;
        errors = Array.Empty<string>();
        return true;
    }

    /// <summary>
    /// Applies the contribution swap -- removes every queue currently owned by
    /// <paramref name="ownerArenaName"/> and adds the contents of <paramref name="newDefs"/>.
    /// Pairs with <see cref="TryComputeArenaContributionDiff"/>: callers should preview, drain
    /// waiters from the to-be-removed queues, and only then apply. Skips internal validation
    /// (assumes the caller already validated via <see cref="TryComputeArenaContributionDiff"/>).
    /// </summary>
    public void ApplyArenaContribution(string ownerArenaName, IReadOnlyList<QueueDefinition> newDefs)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerArenaName);
        ArgumentNullException.ThrowIfNull(newDefs);

        // Remove existing owned entries.
        var keysToDrop = new List<string>();
        foreach (var kvp in _byName)
        {
            if (string.Equals(kvp.Value.OwnerArenaName, ownerArenaName, StringComparison.OrdinalIgnoreCase))
                keysToDrop.Add(kvp.Key);
        }
        foreach (var k in keysToDrop) _byName.Remove(k);

        // Add new entries.
        for (int i = 0; i < newDefs.Count; i++)
            _byName[newDefs[i].UniqueId] = newDefs[i];
    }

    /// <summary>
    /// Removes every queue owned by <paramref name="ownerArenaName"/>. Returns the removed
    /// definitions so callers can drop waiters with a chat notice.
    /// </summary>
    public IReadOnlyList<QueueDefinition> RemoveByOwner(string ownerArenaName)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerArenaName);
        var removed = new List<QueueDefinition>();
        var keys = new List<string>();
        foreach (var kvp in _byName)
        {
            if (string.Equals(kvp.Value.OwnerArenaName, ownerArenaName, StringComparison.OrdinalIgnoreCase))
            {
                removed.Add(kvp.Value);
                keys.Add(kvp.Key);
            }
        }
        foreach (var k in keys) _byName.Remove(k);
        return removed;
    }

    /// <summary>
    /// Composes the qualified registry key for an arena-owned queue. Used by the parser to
    /// pre-qualify names so two arenas can each declare e.g. "3v3comp" without collision.
    /// </summary>
    public static string QualifyName(string ownerArenaName, string baseName)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerArenaName);
        ArgumentException.ThrowIfNullOrEmpty(baseName);
        return $"{ownerArenaName}/{baseName}";
    }

    /// <summary>
    /// Maps a sequence of space-separated tokens to a single queue <c>BaseName</c> for
    /// <c>?play</c> / <c>?queue</c> lookup. Contract: split on ASCII whitespace, drop empty
    /// runs, join with a single <c>_</c>. Returns an empty string when the input has no
    /// non-whitespace tokens.
    /// <para>Examples: <c>"4v4"</c> → <c>"4v4"</c>; <c>"casual 4v4"</c> → <c>"casual_4v4"</c>;
    /// <c>"  CASUAL    4v4  "</c> → <c>"CASUAL_4v4"</c> (lookup against the registry is
    /// case-insensitive); <c>"casual_4v4"</c> → <c>"casual_4v4"</c> (already a single token).</para>
    /// </summary>
    public static string JoinTokensToQueueName(ReadOnlySpan<char> input)
    {
        input = input.Trim();
        if (input.IsEmpty) return string.Empty;

        // Fast path: no inner whitespace = single token, return as-is.
        bool hasWhitespace = false;
        for (int i = 0; i < input.Length; i++)
        {
            if (char.IsWhiteSpace(input[i])) { hasWhitespace = true; break; }
        }
        if (!hasWhitespace) return input.ToString();

        var sb = new System.Text.StringBuilder(input.Length);
        bool inToken = false;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (char.IsWhiteSpace(c))
            {
                if (inToken) { sb.Append('_'); inToken = false; }
            }
            else
            {
                sb.Append(c);
                inToken = true;
            }
        }
        // Defensive: trim a trailing '_' the loop may have appended (cannot happen with the
        // current pre-trimmed input but keeps the postcondition stable under future edits).
        if (sb.Length > 0 && sb[^1] == '_') sb.Length--;
        return sb.ToString();
    }

    /// <summary>
    /// True when two queue definitions are "the same" for hot-reload purposes -- i.e. no waiter
    /// drop is required because no field that affects an in-flight queue entry has changed. Kept
    /// deliberately conservative: any field change registers as "different" and triggers a
    /// drop-and-rebuild so we don't silently retain stale per-queue policies.
    /// </summary>
    private static bool QueueShapeMatches(QueueDefinition a, QueueDefinition b)
    {
        if (!string.Equals(a.UniqueId, b.UniqueId, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.Shape.TeamCount != b.Shape.TeamCount) return false;
        if (a.Shape.PlayersPerTeam != b.Shape.PlayersPerTeam) return false;
        if (!string.Equals(a.GameType, b.GameType, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(a.Label, b.Label, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.MatchArenaName, b.MatchArenaName, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.LookAheadWindow != b.LookAheadWindow) return false;
        if (a.HoldWindow != b.HoldWindow) return false;
        if (a.QualityCeiling != b.QualityCeiling) return false;
        if (a.VetoesRequired != b.VetoesRequired) return false;
        if (a.VetoWindow != b.VetoWindow) return false;
        if (a.RatingWeight != b.RatingWeight) return false;
        if (a.PromoteWinnersToFront != b.PromoteWinnersToFront) return false;
        if (a.MaxConsecutiveDefenses != b.MaxConsecutiveDefenses) return false;
        if (a.StagingDuration != b.StagingDuration) return false;
        if (a.CountdownDuration != b.CountdownDuration) return false;
        if (a.KnockoutSpecDelay != b.KnockoutSpecDelay) return false;
        if (a.LivesPerPlayer != b.LivesPerPlayer) return false;
        if (a.TeamCollapseGrace != b.TeamCollapseGrace) return false;
        if (a.ShipChangeGracePeriod != b.ShipChangeGracePeriod) return false;
        if (a.TimeLimit != b.TimeLimit) return false;
        if (a.ReturnItemsAction != b.ReturnItemsAction) return false;
        if (a.WarpOnSpawn != b.WarpOnSpawn) return false;
        if (a.MaxSpawnDriftTiles != b.MaxSpawnDriftTiles) return false;
        if (a.AfkDwellWarning != b.AfkDwellWarning) return false;
        if (a.AfkDwellCull != b.AfkDwellCull) return false;
        return true;
    }
}
