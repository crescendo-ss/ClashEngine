using System;

namespace ClashEngine.Core.Stats;

/// <summary>
/// URL-derivation helpers for the stats server endpoints. The host's <c>[ClashEngine] UploadUrl</c>
/// is the single source of truth: every stats-server URL the engine talks to (match upload,
/// gametype registration, rating sync) is derived from it by stripping or appending well-known
/// path segments.
/// </summary>
/// <remarks>
/// Lives in Core (not the host) so the conventions are unit-testable without spinning up the
/// host's HTTP transport. The HTTP impl classes call these and then add the transport.
/// </remarks>
public static class StatsApiPaths
{
    private const string MatchesSuffix = "/matches";
    private const string GameTypesSuffix = "/gametypes";

    /// <summary>
    /// Derives the gametype-registration URL (<c>POST /api/gametypes</c>) from the configured
    /// <c>UploadUrl</c>. If <paramref name="uploadUrl"/> ends with <c>/matches</c>
    /// (case-insensitive), the trailing segment is replaced with <c>/gametypes</c>. Otherwise
    /// <c>/gametypes</c> is appended (after stripping any trailing slash). Returns
    /// <see langword="null"/> when <paramref name="uploadUrl"/> is null or whitespace.
    /// </summary>
    public static string? DeriveGameTypeRegistrationUrl(string? uploadUrl)
    {
        if (string.IsNullOrWhiteSpace(uploadUrl)) return null;
        // Trim trailing whitespace AND a trailing slash up front so a stray
        // ".../api/matches/" (common operator typo) takes the same path as ".../api/matches".
        var trimmed = uploadUrl.TrimEnd().TrimEnd('/');

        if (trimmed.EndsWith(MatchesSuffix, StringComparison.OrdinalIgnoreCase))
            return trimmed.Substring(0, trimmed.Length - MatchesSuffix.Length) + "/gametypes";

        return trimmed + "/gametypes";
    }

    /// <summary>
    /// Derives the stats-API base path (e.g. <c>https://stats.example.com/api</c>) from the
    /// configured <c>UploadUrl</c>. Strips a trailing <c>/matches</c> or <c>/gametypes</c>
    /// (case-insensitive), or uses the URL verbatim (trailing slash trimmed) if neither
    /// suffix is present. The rating-sync endpoints (<c>{base}/players/{name}/rating</c>,
    /// <c>{base}/ratings</c>) hang off this base. Returns <see langword="null"/> when
    /// <paramref name="uploadUrl"/> is null or whitespace.
    /// </summary>
    public static string? DeriveStatsApiBase(string? uploadUrl)
    {
        if (string.IsNullOrWhiteSpace(uploadUrl)) return null;
        var trimmed = uploadUrl.TrimEnd().TrimEnd('/');

        if (trimmed.EndsWith(MatchesSuffix, StringComparison.OrdinalIgnoreCase))
            return trimmed.Substring(0, trimmed.Length - MatchesSuffix.Length);
        if (trimmed.EndsWith(GameTypesSuffix, StringComparison.OrdinalIgnoreCase))
            return trimmed.Substring(0, trimmed.Length - GameTypesSuffix.Length);
        return trimmed;
    }
}
