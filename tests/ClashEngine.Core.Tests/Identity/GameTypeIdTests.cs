using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Tests.Identity;

public class GameTypeIdTests
{
    [Fact]
    public void Value_round_trips()
    {
        Assert.Equal(7u, new GameTypeId(7).Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(int.MaxValue)]
    public void From_int_accepts_non_negative(int value)
    {
        Assert.Equal((uint)value, GameTypeId.From(value).Value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void From_int_throws_negative(int value)
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
