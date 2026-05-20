using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ClashEngine.Core.Ratings;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// HTTP-backed <see cref="IRatingSync"/>. Wires the stats server's rating endpoints into the
/// engine's local rating store:
/// <list type="bullet">
///   <item><c>GET /api/players/{name}/rating?gameType=X</c> -- single pull, used by the
///         coordinator on player connect to seed the cache per registered gametype.</item>
///   <item><c>POST /api/ratings</c> -- bulk push (up to 500 entries), used on player
///         disconnect to ship every row the player owns as a single batch.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>URL convention: the rating endpoints are derived from the host's
/// <c>[ClashEngine] UploadUrl</c>, the same key that drives match uploads and gametype
/// registration. See <see cref="DeriveStatsApiBase"/> -- in short, strip the trailing
/// <c>/matches</c> (or <c>/gametypes</c>) segment to get the API root, then tack on the
/// rating-specific path. The same <c>X-Api-Key</c> authenticates every endpoint.</para>
///
/// <para>Status mapping (push): server's documented <c>200 { ok, accepted, skipped }</c>
/// becomes <see cref="RatingPushStatus.Ok"/> with those counts surfaced; 4xx ->
/// <see cref="RatingPushStatus.Rejected"/>; 5xx / network / timeout ->
/// <see cref="RatingPushStatus.Unreachable"/>. The Pull side returns
/// <see langword="null"/> for "no row" (server 200 with null fields), and also for any
/// transport failure -- the coordinator interprets a null pull as "use what's locally
/// cached", which is the right behavior when the server can't be reached.</para>
/// </remarks>
public sealed class HttpRatingSync : IRatingSync, IDisposable
{
    private const string LogCategory = nameof(HttpRatingSync);

    // CamelCase to match the rating.schema.json / ratings-batch.schema.json field names.
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

        // The contract is GET /api/players/{name}/rating?gameType=X. URL-encode both segments
        // so a name with shell-active characters or spaces (rare but possible legacy names)
        // doesn't break the request.
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
            // (per the server cheat-sheet). System.Text.Json will leave nullable fields null
            // and assert that all required fields are present when we hit our typed shape.
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

    public async Task<RatingPushResult> TryPushBatchAsync(
        IReadOnlyList<RatingEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return RatingPushResult.Ok(accepted: 0, skipped: 0);

        // Schema cap is 500 per batch. If a caller hands us more we chunk transparently rather
        // than reject, keeping the per-call statistics aggregated. In practice a single
        // disconnect-push for one player is well under 500.
        const int BatchCap = 500;
        int totalAccepted = 0;
        int totalSkipped = 0;
        for (int offset = 0; offset < entries.Count; offset += BatchCap)
        {
            int end = Math.Min(offset + BatchCap, entries.Count);
            var chunk = new RatingsBatchEntryDto[end - offset];
            for (int i = offset; i < end; i++)
            {
                var e = entries[i];
                chunk[i - offset] = new RatingsBatchEntryDto(
                    PlayerName: e.Player.Name,
                    GameType: e.GameType,
                    Mu: e.Value.Mu,
                    Sigma: e.Value.Sigma,
                    GamesPlayed: e.Value.GamesPlayed,
                    UpdatedAt: e.Value.LastSeen);
            }
            var body = new RatingsBatchDto(chunk);

            var result = await PushOneChunkAsync(body, ct).ConfigureAwait(false);
            if (result.Status != RatingPushStatus.Ok) return result;
            totalAccepted += result.Accepted;
            totalSkipped += result.Skipped;
        }

        return RatingPushResult.Ok(totalAccepted, totalSkipped);
    }

    private async Task<RatingPushResult> PushOneChunkAsync(RatingsBatchDto body, CancellationToken ct)
    {
        string url = $"{_apiBase}/ratings";
        string json = JsonSerializer.Serialize(body, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage? response = null;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                string respBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                BulkPushResponseDto? dto;
                try { dto = JsonSerializer.Deserialize<BulkPushResponseDto>(respBody, JsonOptions); }
                catch (JsonException) { dto = null; }

                int accepted = dto?.Accepted ?? body.Ratings.Length;
                int skipped = dto?.Skipped ?? 0;
                _log.LogM(LogLevel.Info, LogCategory,
                    $"Pushed {body.Ratings.Length} rating(s) ({accepted} stored, {skipped} skipped as stale).");
                return RatingPushResult.Ok(accepted, skipped);
            }

            string failBody = await SafeReadBody(response, ct).ConfigureAwait(false);
            int code = (int)response.StatusCode;
            if (code >= 500)
            {
                _log.LogM(LogLevel.Warn, LogCategory,
                    $"Ratings batch push unreachable: HTTP {code} {response.ReasonPhrase}: {Truncate(failBody, 300)}");
                return RatingPushResult.Unreachable($"HTTP {code} {response.ReasonPhrase}");
            }

            _log.LogM(LogLevel.Warn, LogCategory,
                $"Ratings batch push rejected: HTTP {code} {response.ReasonPhrase}: {Truncate(failBody, 300)}");
            return RatingPushResult.Rejected($"HTTP {code} {response.ReasonPhrase}: {Truncate(failBody, 300)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Warn, LogCategory,
                $"Ratings batch push error: {ex.Message}");
            return RatingPushResult.Unreachable(ex.Message);
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

    // ---- wire-format DTOs ---------------------------------------------------------------------

    /// <summary>Single-rating response body (also matches the schema for the PUT request, minus
    /// the URL parameters). Nullable fields mirror the server's "200 + nulls = no row".</summary>
    private sealed record RatingDto(double? Mu, double? Sigma, uint? GamesPlayed, DateTimeOffset? UpdatedAt);

    /// <summary>Body for <c>POST /api/ratings</c>. Mirrors <c>ratings-batch.schema.json</c>.</summary>
    private sealed record RatingsBatchDto(RatingsBatchEntryDto[] Ratings);

    private sealed record RatingsBatchEntryDto(
        string PlayerName,
        string GameType,
        double Mu,
        double Sigma,
        uint GamesPlayed,
        DateTimeOffset UpdatedAt);

    /// <summary>Response body for <c>POST /api/ratings</c>: <c>{ ok, accepted, skipped }</c>.</summary>
    private sealed record BulkPushResponseDto(bool Ok, int Accepted, int Skipped);
}
