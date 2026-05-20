using System;
using System.Collections.Generic;

namespace ClashEngine.Core.Identity;

/// <summary>
/// Which layer matched in <see cref="PlayerNameMatcher.Resolve"/>. Ordered so
/// <c>default(NameMatchKind)</c> is <see cref="None"/>, never a false success.
/// </summary>
public enum NameMatchKind
{
    /// <summary>Nothing in range -- typo too far from any candidate.</summary>
    None,
    /// <summary>Typed string equalled a candidate (case-insensitive).</summary>
    Exact,
    /// <summary>Typed string was a unique case-insensitive prefix of one candidate.</summary>
    Prefix,
    /// <summary>Typed string had a unique Damerau-Levenshtein match within the threshold.</summary>
    Fuzzy,
    /// <summary>Multiple candidates tied at the best matching layer (listed in
    /// <see cref="NameMatch.Candidates"/>). Callers should reject rather than guess.</summary>
    Ambiguous,
}

/// <summary>Outcome of <see cref="PlayerNameMatcher.Resolve"/>.</summary>
/// <param name="Kind">Which layer matched, or None/Ambiguous.</param>
/// <param name="Name">Canonical-cased candidate name when <paramref name="Kind"/> is
/// Exact/Prefix/Fuzzy; null otherwise.</param>
/// <param name="Candidates">Tied candidates when <paramref name="Kind"/> is Ambiguous,
/// sorted case-insensitively; empty otherwise (never null when constructed via Resolve).</param>
public readonly record struct NameMatch(NameMatchKind Kind, string? Name, IReadOnlyList<string> Candidates);

/// <summary>
/// Typo-tolerant player-name lookup used by commands that take a player argument. Layers:
///   1. exact case-insensitive match
///   2. unique case-insensitive prefix match
///   3. unique Damerau-Levenshtein match within a length-scaled edit-distance threshold
/// At each layer, a tie among multiple candidates short-circuits to <see cref="NameMatchKind.Ambiguous"/>
/// -- callers reject the lookup rather than guess. Pure string logic, no SS dependency, so the
/// algorithm is unit-testable without mocking <c>Player</c>.
/// </summary>
public static class PlayerNameMatcher
{
    /// <summary>Per-input-length edit-distance threshold for the fuzzy step. Short inputs
    /// (&lt;3 chars) skip the fuzzy step entirely so a single-character typo doesn't match
    /// arbitrary names (e.g. "ab" within distance 2 reaches half the alphabet).</summary>
    private static int ThresholdFor(int len) => len switch
    {
        < 3 => 0,
        <= 5 => 1,
        _ => 2,
    };

    /// <summary>
    /// Resolve <paramref name="typed"/> against <paramref name="candidates"/>. Returns the first
    /// layer that produces a unique hit; a tie at any layer returns <see cref="NameMatchKind.Ambiguous"/>
    /// without falling through (fuzzy can only broaden a prefix-tied set, never resolve it).
    /// </summary>
    public static NameMatch Resolve(string typed, IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (string.IsNullOrWhiteSpace(typed))
            return new(NameMatchKind.None, null, Array.Empty<string>());
        typed = typed.Trim();

        // 1) Exact case-insensitive.
        foreach (var c in candidates)
        {
            if (string.Equals(c, typed, StringComparison.OrdinalIgnoreCase))
                return new(NameMatchKind.Exact, c, Array.Empty<string>());
        }

        // 2) Unique case-insensitive prefix. A tie at this layer is reported as Ambiguous --
        //    we do NOT fall through to the broader fuzzy step, since fuzzy can only add
        //    candidates, never disambiguate the prefix hits.
        var prefixHits = new List<string>();
        foreach (var c in candidates)
        {
            if (c.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                prefixHits.Add(c);
        }
        if (prefixHits.Count == 1)
            return new(NameMatchKind.Prefix, prefixHits[0], Array.Empty<string>());
        if (prefixHits.Count > 1)
        {
            prefixHits.Sort(StringComparer.OrdinalIgnoreCase);
            return new(NameMatchKind.Ambiguous, null, prefixHits);
        }

        // 3) Damerau-Levenshtein (Optimal String Alignment) within a length-scaled threshold.
        //    Strict best wins; a tie at the minimum distance is Ambiguous. Threshold 0 disables
        //    the layer entirely for very short inputs.
        int threshold = ThresholdFor(typed.Length);
        if (threshold == 0)
            return new(NameMatchKind.None, null, Array.Empty<string>());

        int bestDist = int.MaxValue;
        var bestHits = new List<string>();
        foreach (var c in candidates)
        {
            int d = OsaDistance(typed, c);
            if (d > threshold) continue;
            if (d < bestDist)
            {
                bestDist = d;
                bestHits.Clear();
                bestHits.Add(c);
            }
            else if (d == bestDist)
            {
                bestHits.Add(c);
            }
        }
        if (bestHits.Count == 1)
            return new(NameMatchKind.Fuzzy, bestHits[0], Array.Empty<string>());
        if (bestHits.Count > 1)
        {
            bestHits.Sort(StringComparer.OrdinalIgnoreCase);
            return new(NameMatchKind.Ambiguous, null, bestHits);
        }
        return new(NameMatchKind.None, null, Array.Empty<string>());
    }

    /// <summary>
    /// Optimal String Alignment distance (restricted Damerau-Levenshtein) between two strings,
    /// case-insensitive. Adjacent transposition counts as cost 1 (so "Daina" -> "Diana" is
    /// distance 1, not 2 as plain Levenshtein would give). The "restricted" variant doesn't
    /// allow a substring to be edited and then transposed; player names are typically &lt;= 24
    /// chars so the unrestricted form's extra memory and complexity isn't worth it.
    /// </summary>
    public static int OsaDistance(string s, string t)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(t);
        if (s.Length == 0) return t.Length;
        if (t.Length == 0) return s.Length;

        // Pre-lowercase so the inner loop is a plain char compare; player names are short
        // enough that the two allocations are negligible compared to the n*m matrix.
        string a = s.ToLowerInvariant();
        string b = t.ToLowerInvariant();
        int n = a.Length, m = b.Length;
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int del = d[i - 1, j] + 1;
                int ins = d[i, j - 1] + 1;
                int sub = d[i - 1, j - 1] + cost;
                int best = del < ins ? del : ins;
                if (sub < best) best = sub;
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    int trans = d[i - 2, j - 2] + 1;
                    if (trans < best) best = trans;
                }
                d[i, j] = best;
            }
        }
        return d[n, m];
    }
}
