using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashEngine.Core.GameType;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// HTTP-based <see cref="IGameTypeRegistrar"/>. POSTs each parsed gametype as JSON to the
/// stats server's <c>POST /api/gametypes</c> endpoint and reports the server's accept/reject
/// decision back to the caller. Used by <see cref="ClashEngine.ClashModule"/> to gate the
/// local <c>GameTypeRegistry</c> on stats-server acceptance.
/// </summary>
/// <remarks>
/// <para>The registration URL is derived from the host's <c>[ClashEngine] UploadUrl</c> --
/// the same key that drives match uploads. The convention is "replace the trailing
/// <c>/matches</c> segment with <c>/gametypes</c>"; if <c>UploadUrl</c> doesn't end in
/// <c>/matches</c>, the registrar falls back to appending <c>/gametypes</c> to the URL
/// (stripping a trailing slash if present). See <see cref="DeriveRegistrationUrl"/>.</para>
///
/// <para>Authentication mirrors <see cref="HttpMatchUploader"/>: the same
/// <c>UploadApiKey</c> is sent as <c>X-Api-Key</c>.</para>
///
/// <para>Status mapping: 2xx -> <see cref="RegistrationStatus.Accepted"/>; 4xx ->
/// <see cref="RegistrationStatus.Rejected"/> (the body is captured in <c>Detail</c>);
/// 5xx / network errors / timeout -> <see cref="RegistrationStatus.Unreachable"/>. The
/// caller decides what to do with rejected / unreachable -- this class is pure transport.</para>
/// </remarks>
public sealed class HttpGameTypeRegistrar : IGameTypeRegistrar, IDisposable
{
    private const string LogCategory = nameof(HttpGameTypeRegistrar);

    // Same camelCase policy as HttpMatchUploader -- the gametype.schema.json properties are
    // camelCase (name / label / description / metadata / originArena).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _url;
    private readonly string _apiKey;
    private readonly ILogManager _log;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _requestTimeout;

    public HttpGameTypeRegistrar(
        string registrationUrl,
        string apiKey,
        ILogManager log,
        HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _url = registrationUrl;
        _apiKey = apiKey;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        _http = httpClient ?? new HttpClient { Timeout = _requestTimeout };
        _ownsHttpClient = httpClient is null;
    }

    public async Task<RegistrationResult> TryRegisterAsync(
        GameTypeRegistration registration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        string json;
        try { json = JsonSerializer.Serialize(registration, JsonOptions); }
        catch (Exception ex)
        {
            // Local bug -- malformed gametype -- not a server problem; classify as Rejected
            // so the operator sees it surfaced rather than silently retried.
            return RegistrationResult.Rejected($"serialize failed: {ex.Message}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _url);
        request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage? response = null;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _log.LogM(LogLevel.Info, LogCategory,
                    $"Registered gametype '{registration.Name}' from arena '{registration.OriginArena}' " +
                    $"({(int)response.StatusCode}).");
                return RegistrationResult.Accepted();
            }

            string body = await SafeReadBody(response, ct).ConfigureAwait(false);
            int code = (int)response.StatusCode;
            if (code >= 500)
            {
                _log.LogM(LogLevel.Warn, LogCategory,
                    $"Stats server unreachable for gametype '{registration.Name}': HTTP {code} {response.ReasonPhrase}: {Truncate(body, 300)}");
                return RegistrationResult.Unreachable($"HTTP {code} {response.ReasonPhrase}");
            }

            _log.LogM(LogLevel.Warn, LogCategory,
                $"Gametype '{registration.Name}' rejected: HTTP {code} {response.ReasonPhrase}: {Truncate(body, 300)}");
            return RegistrationResult.Rejected($"HTTP {code} {response.ReasonPhrase}: {Truncate(body, 300)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // caller is shutting us down
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Warn, LogCategory,
                $"Stats server unreachable while registering gametype '{registration.Name}': {ex.Message}");
            return RegistrationResult.Unreachable(ex.Message);
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
    /// Derives the gametype-registration URL from the configured match <c>UploadUrl</c>.
    /// Convention: the stats server exposes <c>/api/matches</c> for match envelopes and
    /// <c>/api/gametypes</c> for gametype registration on the same host + base path.
    /// </summary>
    /// <remarks>
    /// <para>If <paramref name="uploadUrl"/> ends with <c>/matches</c> (case-insensitive),
    /// the trailing segment is replaced with <c>/gametypes</c>. Otherwise <c>/gametypes</c>
    /// is appended (after stripping any trailing slash).</para>
    /// <para>Returns <see langword="null"/> when <paramref name="uploadUrl"/> is null or
    /// whitespace -- the host's fail-closed policy then rejects every gametype registration
    /// attempt (because no UploadUrl means no stats server is configured).</para>
    /// </remarks>
    public static string? DeriveRegistrationUrl(string? uploadUrl)
    {
        if (string.IsNullOrWhiteSpace(uploadUrl)) return null;
        var trimmed = uploadUrl.TrimEnd();

        const string MatchesSuffix = "/matches";
        if (trimmed.EndsWith(MatchesSuffix, StringComparison.OrdinalIgnoreCase))
            return trimmed.Substring(0, trimmed.Length - MatchesSuffix.Length) + "/gametypes";

        return trimmed.TrimEnd('/') + "/gametypes";
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
