using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Tests.Identity;

public class GameTypeIdTests
{
    [Fact]
    public void Value_round_trips()
    {
        Assert.Equal((byte)7, new GameTypeId(7).Value);
    }

    [Fact]
    public void From_int_succeeds_in_range()
    {
        Assert.Equal((byte)0, GameTypeId.From(0).Value);
        Assert.Equal((byte)255, GameTypeId.From(255).Value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData(int.MaxValue)]
    public void From_int_throws_out_of_range(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GameTypeId.From(value));
    }

    [Fact]
    public void Equality_is_value_based()
    {
        Assert.Equal(new GameTypeId(3), new GameTypeId(3));
        Assert.NotEqual(new GameTypeId(3), new GameTypeId(4));
    }
}
