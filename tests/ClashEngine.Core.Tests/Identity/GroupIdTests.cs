using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Tests.Identity;

public class GroupIdTests
{
    [Fact]
    public void New_returns_fresh_unique_ids()
    {
        var a = GroupId.New();
        var b = GroupId.New();
        Assert.NotEqual(a, b);
        Assert.False(a.IsDefault);
    }

    [Fact]
    public void Default_is_marked()
    {
        Assert.True(default(GroupId).IsDefault);
    }

    [Fact]
    public void Same_value_compares_equal()
    {
        var v = Guid.NewGuid();
        Assert.Equal(new GroupId(v), new GroupId(v));
    }
}
