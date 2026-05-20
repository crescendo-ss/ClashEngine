using ClashEngine.Core.Stats;

namespace ClashEngine.Core.Tests.Stats;

public class StatsApiPathsTests
{
    // ---- DeriveGameTypeRegistrationUrl ------------------------------------------------------

    [Theory]
    [InlineData("https://stats.example.com/api/matches", "https://stats.example.com/api/gametypes")]
    [InlineData("https://stats.example.com/api/Matches", "https://stats.example.com/api/gametypes")] // case-insensitive
    [InlineData("https://stats.example.com/api/matches/", "https://stats.example.com/api/gametypes")] // trailing slash
    [InlineData("https://stats.example.com/api", "https://stats.example.com/api/gametypes")]
    [InlineData("https://stats.example.com/api/", "https://stats.example.com/api/gametypes")]
    [InlineData("https://stats.example.com", "https://stats.example.com/gametypes")]
    public void DeriveGameTypeRegistrationUrl_replaces_matches_or_appends(string upload, string expected)
    {
        Assert.Equal(expected, StatsApiPaths.DeriveGameTypeRegistrationUrl(upload));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DeriveGameTypeRegistrationUrl_returns_null_for_unset(string? upload)
    {
        Assert.Null(StatsApiPaths.DeriveGameTypeRegistrationUrl(upload));
    }

    [Fact]
    public void DeriveGameTypeRegistrationUrl_does_not_strip_matches_mid_path()
    {
        // /matches as the LAST segment is the convention; an embedded /matches/foo is not it.
        Assert.Equal("https://stats.example.com/api/matches/foo/gametypes",
            StatsApiPaths.DeriveGameTypeRegistrationUrl("https://stats.example.com/api/matches/foo"));
    }

    // ---- DeriveStatsApiBase -----------------------------------------------------------------

    [Theory]
    [InlineData("https://stats.example.com/api/matches", "https://stats.example.com/api")]
    [InlineData("https://stats.example.com/api/gametypes", "https://stats.example.com/api")]
    [InlineData("https://stats.example.com/api/MATCHES", "https://stats.example.com/api")] // case-insensitive
    [InlineData("https://stats.example.com/api/gametypes/", "https://stats.example.com/api")] // trailing slash
    [InlineData("https://stats.example.com/api", "https://stats.example.com/api")]
    [InlineData("https://stats.example.com/api/", "https://stats.example.com/api")]
    [InlineData("https://stats.example.com", "https://stats.example.com")]
    public void DeriveStatsApiBase_strips_known_suffixes(string upload, string expected)
    {
        Assert.Equal(expected, StatsApiPaths.DeriveStatsApiBase(upload));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DeriveStatsApiBase_returns_null_for_unset(string? upload)
    {
        Assert.Null(StatsApiPaths.DeriveStatsApiBase(upload));
    }

    [Fact]
    public void Round_trip_gametype_then_base_lands_on_api_root()
    {
        // Operator sets UploadUrl=.../api/matches.  GameType URL is .../api/gametypes.
        // Feeding THAT through DeriveStatsApiBase gets us back to .../api -- the rating
        // endpoints' base. Confirms the two helpers compose for an operator who only
        // configures UploadUrl once.
        var gtUrl = StatsApiPaths.DeriveGameTypeRegistrationUrl("https://stats.example.com/api/matches");
        Assert.Equal("https://stats.example.com/api", StatsApiPaths.DeriveStatsApiBase(gtUrl));
    }
}
