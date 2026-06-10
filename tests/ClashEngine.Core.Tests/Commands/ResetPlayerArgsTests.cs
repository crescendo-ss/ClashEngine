using ClashEngine.Core.Commands;

namespace ClashEngine.Core.Tests.Commands;

/// <summary>
/// The ?resetplayer argument parser. Subspace player names may contain spaces, so the central
/// contract is that the name is never tokenized — flags are only recognized at the ends of the
/// argument string and everything in between is the name, verbatim.
/// </summary>
public class ResetPlayerArgsTests
{
    [Fact]
    public void Plain_name_parses_without_flags()
    {
        var p = ResetPlayerArgs.Parse("Bob");
        Assert.Equal("Bob", p.PlayerName);
        Assert.False(p.KeepRating);
        Assert.Null(p.UnknownFlag);
    }

    [Fact]
    public void Name_with_spaces_is_taken_verbatim()
    {
        var p = ResetPlayerArgs.Parse("Bob the Builder");
        Assert.Equal("Bob the Builder", p.PlayerName);
        Assert.False(p.KeepRating);
    }

    [Fact]
    public void Interior_whitespace_is_preserved_ends_are_trimmed()
    {
        var p = ResetPlayerArgs.Parse("   Bob  the   Builder ");
        Assert.Equal("Bob  the   Builder", p.PlayerName);
    }

    [Fact]
    public void Flag_after_spaced_name()
    {
        var p = ResetPlayerArgs.Parse("Bob the Builder --keep-rating");
        Assert.Equal("Bob the Builder", p.PlayerName);
        Assert.True(p.KeepRating);
    }

    [Fact]
    public void Flag_before_spaced_name()
    {
        var p = ResetPlayerArgs.Parse("--keep-rating Bob the Builder");
        Assert.Equal("Bob the Builder", p.PlayerName);
        Assert.True(p.KeepRating);
    }

    [Fact]
    public void Flag_is_case_insensitive()
    {
        var p = ResetPlayerArgs.Parse("Bob --KEEP-RATING");
        Assert.Equal("Bob", p.PlayerName);
        Assert.True(p.KeepRating);
    }

    [Fact]
    public void Double_dash_inside_the_name_is_part_of_the_name()
    {
        // Only end-anchored tokens are flags; a "--" run in the middle belongs to the name.
        var p = ResetPlayerArgs.Parse("Bob --the-- Builder");
        Assert.Equal("Bob --the-- Builder", p.PlayerName);
        Assert.False(p.KeepRating);
        Assert.Null(p.UnknownFlag);
    }

    [Fact]
    public void Unknown_trailing_flag_is_reported()
    {
        var p = ResetPlayerArgs.Parse("Bob --keep-ratings");
        Assert.Equal("--keep-ratings", p.UnknownFlag);
        Assert.Null(p.PlayerName);
    }

    [Fact]
    public void Unknown_leading_flag_is_reported()
    {
        var p = ResetPlayerArgs.Parse("--nuke Bob");
        Assert.Equal("--nuke", p.UnknownFlag);
        Assert.Null(p.PlayerName);
    }

    [Fact]
    public void Empty_input_yields_no_name()
    {
        Assert.Null(ResetPlayerArgs.Parse("").PlayerName);
        Assert.Null(ResetPlayerArgs.Parse("   ").PlayerName);
    }

    [Fact]
    public void Flag_without_a_name_yields_no_name()
    {
        var p = ResetPlayerArgs.Parse("--keep-rating");
        Assert.Null(p.PlayerName);
        Assert.True(p.KeepRating);
        Assert.Null(p.UnknownFlag);
    }

    [Fact]
    public void Flags_on_both_ends_of_a_spaced_name()
    {
        var p = ResetPlayerArgs.Parse("--keep-rating Bob the Builder --keep-rating");
        Assert.Equal("Bob the Builder", p.PlayerName);
        Assert.True(p.KeepRating);
    }
}
