using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Tests.Identity;

public class PlayerNameMatcherTests
{
    // Representative roster: covers prefix collisions (Bob/Bobby, Cres-/CresFan),
    // a transposition target (Diana), and a unique-prefix target (Carla).
    private static readonly string[] Sample =
        { "Crescendo", "Diana", "Bob", "Bobby", "CresFan", "Carla" };

    // ---- exact ----

    [Fact]
    public void Exact_match_is_case_insensitive()
    {
        var result = PlayerNameMatcher.Resolve("crescendo", Sample);
        Assert.Equal(NameMatchKind.Exact, result.Kind);
        Assert.Equal("Crescendo", result.Name);
    }

    [Fact]
    public void Exact_match_returned_even_when_a_prefix_collision_exists()
    {
        // "Bob" is exact AND a prefix of "Bobby" -- exact must win.
        var result = PlayerNameMatcher.Resolve("Bob", Sample);
        Assert.Equal(NameMatchKind.Exact, result.Kind);
        Assert.Equal("Bob", result.Name);
    }

    // ---- prefix ----

    [Fact]
    public void Unique_prefix_match()
    {
        var result = PlayerNameMatcher.Resolve("Dia", Sample);
        Assert.Equal(NameMatchKind.Prefix, result.Kind);
        Assert.Equal("Diana", result.Name);
    }

    [Fact]
    public void Ambiguous_prefix_match_reports_both_candidates()
    {
        var result = PlayerNameMatcher.Resolve("Cres", Sample);
        Assert.Equal(NameMatchKind.Ambiguous, result.Kind);
        Assert.Null(result.Name);
        Assert.Contains("Crescendo", result.Candidates);
        Assert.Contains("CresFan", result.Candidates);
    }

    [Fact]
    public void Ambiguous_prefix_does_not_fall_through_to_fuzzy()
    {
        // Even if fuzzy *could* pick one over the other, ambiguous prefix short-circuits.
        // Adding more candidates to the prefix-tied set isn't going to disambiguate it.
        var result = PlayerNameMatcher.Resolve("Bo", Sample);
        Assert.Equal(NameMatchKind.Ambiguous, result.Kind);
    }

    // ---- fuzzy ----

    [Fact]
    public void Fuzzy_match_handles_transposition_at_distance_1()
    {
        // "Daina" vs "Diana": adjacent swap of i/a. OSA distance 1.
        var result = PlayerNameMatcher.Resolve("Daina", Sample);
        Assert.Equal(NameMatchKind.Fuzzy, result.Kind);
        Assert.Equal("Diana", result.Name);
    }

    [Fact]
    public void Fuzzy_match_handles_missing_letter()
    {
        // "Crescndo" vs "Crescendo": deletion of one 'e', distance 1.
        var result = PlayerNameMatcher.Resolve("Crescndo", Sample);
        Assert.Equal(NameMatchKind.Fuzzy, result.Kind);
        Assert.Equal("Crescendo", result.Name);
    }

    [Fact]
    public void Fuzzy_match_handles_substitution()
    {
        // "Carlz" vs "Carla": substitution at position 4, distance 1.
        var result = PlayerNameMatcher.Resolve("Carlz", Sample);
        Assert.Equal(NameMatchKind.Fuzzy, result.Kind);
        Assert.Equal("Carla", result.Name);
    }

    [Fact]
    public void Fuzzy_ambiguous_at_min_distance_is_rejected()
    {
        // "Mark" vs {"Mart","Marc"}: distance 1 to both (single substitution at the last
        // char), neither a prefix of "Mark". Tie at the minimum -> ambiguous, not a guess.
        // (Note: we can't reuse the default Sample for this -- a Bob/Bobby pair would be
        // disambiguated by the prefix layer before fuzzy gets a chance.)
        var roster = new[] { "Mart", "Marc", "Doug" };
        var result = PlayerNameMatcher.Resolve("Mark", roster);
        Assert.Equal(NameMatchKind.Ambiguous, result.Kind);
        Assert.Contains("Mart", result.Candidates);
        Assert.Contains("Marc", result.Candidates);
    }

    [Fact]
    public void Prefix_layer_wins_over_a_distance_1_fuzzy_alternative()
    {
        // "Bobb" is a prefix of "Bobby" (unique) AND distance 1 to "Bob". Prefix wins --
        // a longer typed string is a stronger signal of intent than a shorter alternative.
        var result = PlayerNameMatcher.Resolve("Bobb", Sample);
        Assert.Equal(NameMatchKind.Prefix, result.Kind);
        Assert.Equal("Bobby", result.Name);
    }

    [Fact]
    public void Strict_best_beats_a_worse_in_threshold_candidate()
    {
        // From "Crescndo": distance 1 to "Crescendo", distance >2 to everything else.
        // Strict winner -> Fuzzy, not Ambiguous.
        var result = PlayerNameMatcher.Resolve("Crescndo", Sample);
        Assert.Equal(NameMatchKind.Fuzzy, result.Kind);
        Assert.Equal("Crescendo", result.Name);
    }

    // ---- threshold / none ----

    [Fact]
    public void Short_input_skips_fuzzy_step()
    {
        // "Bi" -- 2 chars, threshold 0. Not a prefix of anything in Sample. Critically must
        // NOT fuzzy-match "Bob" (distance 2), because at length 2 that's too permissive.
        var result = PlayerNameMatcher.Resolve("Bi", Sample);
        Assert.Equal(NameMatchKind.None, result.Kind);
    }

    [Fact]
    public void No_match_when_typo_is_too_far()
    {
        var result = PlayerNameMatcher.Resolve("Xyzzy", Sample);
        Assert.Equal(NameMatchKind.None, result.Kind);
    }

    [Fact]
    public void Whitespace_input_is_None()
    {
        var result = PlayerNameMatcher.Resolve("   ", Sample);
        Assert.Equal(NameMatchKind.None, result.Kind);
    }

    [Fact]
    public void Empty_candidates_is_None()
    {
        var result = PlayerNameMatcher.Resolve("Diana", Array.Empty<string>());
        Assert.Equal(NameMatchKind.None, result.Kind);
    }

    [Fact]
    public void Input_is_trimmed_before_matching()
    {
        var result = PlayerNameMatcher.Resolve("  Diana  ", Sample);
        Assert.Equal(NameMatchKind.Exact, result.Kind);
        Assert.Equal("Diana", result.Name);
    }

    // ---- OSA distance unit tests ----

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "abc", 0)]
    [InlineData("abc", "ABC", 0)]      // case-insensitive
    [InlineData("abc", "abd", 1)]      // substitution
    [InlineData("abc", "ab", 1)]       // deletion
    [InlineData("ab", "abc", 1)]       // insertion
    [InlineData("ab", "ba", 1)]        // adjacent transposition
    [InlineData("Diana", "Daina", 1)]  // adjacent transposition (i/a)
    [InlineData("kitten", "sitting", 3)] // classic Levenshtein example
    [InlineData("abc", "xyz", 3)]
    public void OsaDistance_cases(string a, string b, int expected)
    {
        Assert.Equal(expected, PlayerNameMatcher.OsaDistance(a, b));
        // OSA is symmetric for the cases we care about; testing both directions catches
        // any off-by-one in the transposition branch.
        Assert.Equal(expected, PlayerNameMatcher.OsaDistance(b, a));
    }
}
