using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Tests.Identity;

public class PlayerKeyTests
{
    [Fact]
    public void Equality_is_case_insensitive()
    {
        Assert.Equal(new PlayerKey("Alice"), new PlayerKey("alice"));
        Assert.Equal(new PlayerKey("Alice"), new PlayerKey("ALICE"));
    }

    [Fact]
    public void Different_names_not_equal()
    {
        Assert.NotEqual(new PlayerKey("Alice"), new PlayerKey("Bob"));
    }

    [Fact]
    public void Hash_codes_match_for_case_insensitive_equal_keys()
    {
        Assert.Equal(new PlayerKey("Alice").GetHashCode(), new PlayerKey("alice").GetHashCode());
    }

    [Fact]
    public void Equality_operator_works()
    {
        Assert.True(new PlayerKey("Alice") == new PlayerKey("ALICE"));
        Assert.False(new PlayerKey("Alice") != new PlayerKey("alice"));
        Assert.True(new PlayerKey("Alice") != new PlayerKey("Bob"));
    }

    [Fact]
    public void Default_struct_is_marked_default()
    {
        Assert.True(default(PlayerKey).IsDefault);
        Assert.False(new PlayerKey("Alice").IsDefault);
    }

    [Fact]
    public void Default_keys_are_equal_to_each_other()
    {
        Assert.Equal(default(PlayerKey), default(PlayerKey));
    }

    [Fact]
    public void Default_key_not_equal_to_named_key()
    {
        Assert.NotEqual(default(PlayerKey), new PlayerKey("Alice"));
    }

    [Fact]
    public void Empty_name_throws()
    {
        Assert.Throws<ArgumentException>(() => new PlayerKey(""));
    }

    [Fact]
    public void Null_name_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PlayerKey(null!));
    }

    [Fact]
    public void ToString_returns_name()
    {
        Assert.Equal("Alice", new PlayerKey("Alice").ToString());
    }

    // Subspace player names may contain spaces. PlayerKey must carry them verbatim — every
    // name-taking command (?accept/?decline/?forgive/?resetplayer) funnels the typed name into
    // a PlayerKey and relies on this equality to find the stored player.

    [Fact]
    public void Spaced_names_compare_case_insensitively()
    {
        Assert.Equal(new PlayerKey("Bob the Builder"), new PlayerKey("bob THE builder"));
        Assert.Equal(new PlayerKey("Bob the Builder").GetHashCode(),
                     new PlayerKey("bob the builder").GetHashCode());
    }

    [Fact]
    public void Spaces_are_significant()
    {
        Assert.NotEqual(new PlayerKey("Bob Smith"), new PlayerKey("BobSmith"));
        Assert.NotEqual(new PlayerKey("Bob  Smith"), new PlayerKey("Bob Smith"));
    }

    [Fact]
    public void Spaced_name_round_trips_through_a_dictionary()
    {
        var dict = new Dictionary<PlayerKey, int> { [new PlayerKey("Bob the Builder")] = 7 };
        Assert.Equal(7, dict[new PlayerKey("BOB THE BUILDER")]);
        Assert.False(dict.ContainsKey(new PlayerKey("Bob")));
    }

    [Fact]
    public void Works_as_dictionary_key()
    {
        var dict = new Dictionary<PlayerKey, int>
        {
            [new PlayerKey("Alice")] = 1,
            [new PlayerKey("Bob")] = 2,
        };

        Assert.Equal(1, dict[new PlayerKey("ALICE")]);
        Assert.Equal(2, dict[new PlayerKey("bob")]);
        Assert.False(dict.ContainsKey(new PlayerKey("Carol")));
    }
}
