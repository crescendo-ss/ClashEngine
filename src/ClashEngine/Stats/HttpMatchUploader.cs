using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashEngine.Core.Stats;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// HTTP-based <see cref="IMatchUploader"/>. POSTs each finalized match envelope to a configured
/// URL as <c>multipart/form-data</c> with two parts: <c>metadata</c> (the JSON payload) and
/// <c>recording</c> (the raw <c>.replay</c> file, when one is attached). Authenticates with an
/// <c>X-Api-Key</c> header.
/// </summary>
/// <remarks>
/// <para><see cref="Upload"/> is non-blocking -- it enqueues the payload on a background worker
/// so the mainloop never waits on HTTP. The worker drains serially.</para>
///
/// <para>If <c>RecordingPath</c> is set on the payload, the worker first waits for
/// <see cref="_isRecordingReady"/> to return <see langword="true"/> for the match id (i.e. the
/// replay module has flushed the file to disk). If the predicate doesn't go true within
/// <see cref="_readinessTimeout"/>, the worker proceeds anyway with metadata only.</para>
///
/// <para>On a 2xx response, the worker invokes <see cref="_onUploadSuccess"/> with the match
/// id -- the host hooks this to delete the local recording file. On non-2xx or exception, the
/// payload is logged and dropped (no automatic retry; the file is left for manual or batched
/// recovery).</para>
/// </remarks>
public sealed class HttpMatchUploader : IMatchUploader, IDisposable
{
    private const string LogCategory = nameof(HttpMatchUploader);

    // The match upload schema (schema/match.schema.json) uses camelCase property names; without
    // a naming policy System.Text.Json emits PascalCase, which the server rejects with HTTP 422
    // ("must have required property 'schemaVersion'", etc.). DictionaryKeyPolicy is intentionally
    // NOT set: perWeapon / itemUses / wastedItems use PascalCase enum names ("Bullet", "Repel",
    // ...) that match what ItemKind.ToString() / WeaponKind.ToString() already produce.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _url;
    private readonly string _apiKey;
    private readonly ILogManager _log;
    private readonly Func<Guid, bool>? _isRecordingReady;
    private readonly Action<Guid>? _onUploadSuccess;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _readinessTimeout;
    private readonly TimeSpan _readinessPoll;

    /// <summary>Maximum payloads buffered while the backend is unreachable. Hit when uploads
    /// stall (network down, server 5xx). Beyond this, new payloads are dropped + logged rather
    /// than blocking the producer (which is the mainloop). Picked large enough that a healthy
    /// zone never trips it: 64 finalized matches is hours of play.</summary>
    private const int QueueCapacity = 64;

    private readonly BlockingCollection<MatchPayload> _queue =
        new(new ConcurrentQueue<MatchPayload>(), boundedCapacity: QueueCapacity);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public HttpMatchUploader(
        string url,
        string apiKey,
        ILogManager log,
        Func<Guid, bool>? isRecordingReady = null,
        Action<Guid>? onUploadSuccess = null,
        HttpClient? httpClient = null,
        TimeSpan? readinessTimeout = null,
        TimeSpan? readinessPollInterval = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Must be non-empty.", nameof(url));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Must be non-empty.", nameof(apiKey));
        _url = url;
        _apiKey = apiKey;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _isRecordingReady = isRecordingReady;
        _onUploadSuccess = onUploadSuccess;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _ownsHttpClient = httpClient is null;
        _readinessTimeout = readinessTimeout ?? TimeSpan.FromMinutes(5);
        _readinessPoll = readinessPollInterval ?? TimeSpan.FromSeconds(1);

        _worker = Task.Factory.StartNew(
            DrainLoop, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Upload(MatchPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        // Non-blocking add: TryAdd with timeout 0. If the bounded queue is full (backend
        // unreachable or persistently slow), we log + drop the payload rather than blocking
        // the mainloop on Add. CompleteAdding during shutdown also surfaces here as false.
        try
        {
            if (!_queue.TryAdd(payload, millisecondsTimeout: 0))
            {
                _log.LogM(LogLevel.Warn, LogCategory,
                    $"Upload queue full ({QueueCapacity}); dropping match {payload.MatchId:N}. " +
                    "Backend appears unreachable or persistently slow.");
            }
        }
        catch (InvalidOperationException)
        {
            // _queue.CompleteAdding() was called during shutdown -- drop silently.
        }
    }

    private void DrainLoop()
    {
        try
        {
            foreach (var payload in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    UploadOne(payload);
                }
                catch (Exception ex)
                {
                    _log.LogM(LogLevel.Error, LogCategory,
                        $"Unhandled error uploading match {payload.MatchId:N}: {ex}");
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private void UploadOne(MatchPayload payload)
    {
        bool hasRecording = !string.IsNullOrEmpty(payload.RecordingPath);
        if (hasRecording && _isRecordingReady is not null)
        {
            var deadline = DateTimeOffset.UtcNow + _readinessTimeout;
            while (!_isRecordingReady(payload.MatchId))
            {
                if (_cts.IsCancellationRequested) return;
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    _log.LogM(LogLevel.Warn, LogCategory,
                        $"Recording for match {payload.MatchId:N} not ready after {_readinessTimeout}; uploading metadata only.");
                    hasRecording = false;
                    break;
                }
                try { Task.Delay(_readinessPoll, _cts.Token).Wait(); }
                catch { return; }
            }
        }

        // Re-check existence: even if predicate said ready, the file could have been removed.
        string? filePath = hasRecording ? payload.RecordingPath : null;
        if (filePath is not null && !File.Exists(filePath))
        {
            _log.LogM(LogLevel.Warn, LogCategory,
                $"Recording {filePath} for match {payload.MatchId:N} missing on disk; uploading metadata only.");
            filePath = null;
        }

        string json = JsonSerializer.Serialize(payload, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, _url);
        request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);

        using var content = new MultipartFormDataContent();
        var jsonContent = new StringContent(json, Encoding.UTF8, "application/json");
        content.Add(jsonContent, "metadata");

        FileStream? recordingStream = null;
        if (filePath is not null)
        {
            try
            {
                recordingStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var streamContent = new StreamContent(recordingStream);
                streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Add(streamContent, "recording", Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                recordingStream?.Dispose();
                recordingStream = null;
                filePath = null;
                _log.LogM(LogLevel.Warn, LogCategory,
                    $"Failed to open recording {payload.RecordingPath} for match {payload.MatchId:N}: {ex.Message}; uploading metadata only.");
            }
        }

        request.Content = content;

        // Defer the success callback (which deletes the replay file) until AFTER the stream
        // is disposed in the finally block. Otherwise the file handle is still open at the
        // moment _onUploadSuccess runs and the delete races with HttpClient's stream cleanup,
        // failing with "The process cannot access the file ... because it is being used by
        // another process." on Windows.
        bool succeeded = false;
        try
        {
            using var response = _http.Send(request, HttpCompletionOption.ResponseContentRead, _cts.Token);
            if (response.IsSuccessStatusCode)
            {
                _log.LogM(LogLevel.Info, LogCategory,
                    $"Uploaded match {payload.MatchId:N} ({(int)response.StatusCode}, recording={(filePath is null ? "no" : "yes")}).");
                succeeded = true;
            }
            else
            {
                string body;
                try { body = response.Content.ReadAsStringAsync(_cts.Token).Result; }
                catch { body = "<unavailable>"; }
                _log.LogM(LogLevel.Error, LogCategory,
                    $"Upload failed for match {payload.MatchId:N}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 500)}");
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory,
                $"Upload error for match {payload.MatchId:N}: {ex.Message}");
        }
        finally
        {
            recordingStream?.Dispose();
        }

        if (succeeded) _onUploadSuccess?.Invoke(payload.MatchId);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";

    public void Dispose()
    {
        _queue.CompleteAdding();
        _cts.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(5)); } catch { /* swallow */ }
        _cts.Dispose();
        _queue.Dispose();
        if (_ownsHttpClient) _http.Dispose();
    }
}
