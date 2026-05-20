using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ClashEngine.Core.Ratings;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// HTTP-backed <see cref="IRatingSync"/>. Pulls one <c>(player, gameType)</c> rating from
/// the stats server's <c>GET /api/players/{name}/rating?gameType=X</c> endpoint. Used by
/// <see cref="RatingSyncCoordinator"/> to seed the local cache the first time a player
/// connects to a zone that has no persisted rating for them.
/// </summary>
/// <remarks>
/// <para>URL convention: the rating endpoint is derived from the host's
/// <c>[ClashEngine] UploadUrl</c>, the same key that drives match uploads and gametype
/// registration. See <see cref="DeriveStatsApiBase"/> -- strip the trailing
/// <c>/matches</c> (or <c>/gametypes</c>) segment to get the API root, then tack on
/// <c>/players/{name}/rating?gameType=X</c>. The same <c>X-Api-Key</c> authenticates every
/// endpoint.</para>
///
/// <para><see cref="TryPullAsync"/> returns <see langword="null"/> for both "server has no
/// row" (200 with null fields, or 404) and "could not reach the server" (5xx, network,
/// timeout). The coordinator interprets null as "leave the cache as-is", so transport
/// failures simply mean a new player keeps the engine's default rating until they play their
/// first match (which then drives the rating via the match-envelope channel).</para>
///
/// <para>There is no push method here; ratings flow BACK to the server via the
/// match-upload envelope. See the <see cref="IRatingSync"/> remarks for the design rationale.</para>
/// </remarks>
public sealed class HttpRatingSync : IRatingSync, IDisposable
{
    private const string LogCategory = nameof(HttpRatingSync);

    // CamelCase to match the rating.schema.json field names.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _apiBase;
    private readonly string _apiKey;
    private readonly ILogManager _log;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _requestTimeout;

    public HttpRatingSync(
        string statsApiBase,
        string apiKey,
        ILogManager log,
        HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statsApiBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiBase = statsApiBase.TrimEnd('/');
        _apiKey = apiKey;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        _http = httpClient ?? new HttpClient { Timeout = _requestTimeout };
        _ownsHttpClient = httpClient is null;
    }

    public async Task<Rating?> TryPullAsync(string playerName, string gameType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameType);

        // Contract: GET /api/players/{name}/rating?gameType=X. Both segments are URL-escaped
        // so a name with shell-active characters or spaces doesn't break the request.
        string url = $"{_apiBase}/players/{Uri.EscapeDataString(playerName)}/rating?gameType={Uri.EscapeDataString(gameType)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);

        HttpResponseMessage? response = null;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
            {
                string body = await SafeReadBody(response, ct).ConfigureAwait(false);
                _log.LogM(LogLevel.Warn, LogCategory,
                    $"Pull failed for player '{playerName}' gameType='{gameType}': HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 300)}");
                return null;
            }

            // 200 body: { mu, sigma, gamesPlayed, updatedAt }; nulls mean "no row for this pair"
            // (per the server cheat-sheet). Treat any missing required field as "no row".
            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            RatingDto? dto;
            try { dto = JsonSerializer.Deserialize<RatingDto>(json, JsonOptions); }
            catch (JsonException ex)
            {
                _log.LogM(LogLevel.Warn, LogCategory,
                    $"Pull response for '{playerName}' gameType='{gameType}' was not parseable JSON: {ex.Message}");
                return null;
            }
            if (dto is null || dto.Mu is null || dto.Sigma is null || dto.GamesPlayed is null || dto.UpdatedAt is null)
                return null;

            return new Rating(
                Mu: dto.Mu.Value,
                Sigma: dto.Sigma.Value,
                GamesPlayed: dto.GamesPlayed.Value,
                LastSeen: dto.UpdatedAt.Value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Warn, LogCategory,
                $"Pull error for '{playerName}' gameType='{gameType}': {ex.Message}");
            return null;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static async Task<string> SafeReadBody(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return "<unavailable>"; }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";

    /// <summary>
    /// Convenience passthrough to <see cref="Core.Stats.StatsApiPaths.DeriveStatsApiBase"/>
    /// so existing callers in this project keep the same entry point. The actual convention
    /// lives in Core (it's the same one <see cref="HttpGameTypeRegistrar"/> uses).
    /// </summary>
    public static string? DeriveStatsApiBase(string? uploadUrl) =>
        Core.Stats.StatsApiPaths.DeriveStatsApiBase(uploadUrl);

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    // ---- wire-format DTO ----------------------------------------------------------------------

    /// <summary>Single-rating response body. Nullable fields mirror the server's "200 + nulls = no row".</summary>
    private sealed record RatingDto(double? Mu, double? Sigma, uint? GamesPlayed, DateTimeOffset? UpdatedAt);
}
