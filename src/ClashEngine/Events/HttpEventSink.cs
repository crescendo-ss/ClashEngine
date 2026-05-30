using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashEngine.Core.Events;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Events;

/// <summary>
/// HTTP-backed <see cref="IEventSink"/>. POSTs each <see cref="EventEnvelope"/> to a configured
/// URL as a single <c>application/json</c> body, authenticated with an <c>X-Api-Key</c> header.
/// Mirrors <see cref="ClashEngine.Stats.HttpMatchUploader"/>'s discipline: <see cref="Emit"/> is
/// non-blocking (hands off to a background worker), never throws, and drops on overflow rather
/// than stalling the mainloop.
/// </summary>
/// <remarks>
/// No retry: on a non-2xx response or a transport error the envelope is logged and dropped. The
/// consumer is expected to tolerate gaps (e.g. a "queue board" self-heals from the next event).
/// Queue events are far more frequent than match uploads, so the buffer is sized generously and
/// drop-warnings are throttled.
/// </remarks>
public sealed class HttpEventSink : IEventSink, IDisposable
{
    private const string LogCategory = nameof(HttpEventSink);

    // camelCase to match schema/event.schema.json; WhenWritingNull drops the unused payload
    // siblings (queue/match/player) and every optional field, keeping the wire form lean.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Max envelopes buffered while the backend is unreachable. Events are small and
    /// bursty (every queue join/leave), so this is far larger than the match-upload queue: a busy
    /// zone can produce dozens of events a second.</summary>
    private const int QueueCapacity = 256;

    private readonly string _url;
    private readonly string _apiKey;
    private readonly ILogManager _log;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    private readonly BlockingCollection<EventEnvelope> _queue =
        new(new ConcurrentQueue<EventEnvelope>(), boundedCapacity: QueueCapacity);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    private long _droppedTotal;

    public HttpEventSink(
        string url,
        string apiKey,
        ILogManager log,
        HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Must be non-empty.", nameof(url));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Must be non-empty.", nameof(apiKey));
        _url = url;
        _apiKey = apiKey;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _http = httpClient ?? new HttpClient { Timeout = requestTimeout ?? TimeSpan.FromSeconds(15) };
        _ownsHttpClient = httpClient is null;

        _worker = Task.Factory.StartNew(
            DrainLoop, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Emit(EventEnvelope envelope)
    {
        if (envelope is null) return;
        try
        {
            if (!_queue.TryAdd(envelope, millisecondsTimeout: 0))
            {
                long dropped = Interlocked.Increment(ref _droppedTotal);
                // Throttle: warn on the first drop and then every 100th, so a downed backend
                // doesn't flood the log with one line per event.
                if (dropped == 1 || dropped % 100 == 0)
                    _log.LogM(LogLevel.Warn, LogCategory,
                        $"Event queue full ({QueueCapacity}); dropped {dropped} event(s) so far. " +
                        "Backend appears unreachable or persistently slow.");
            }
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding() was called during shutdown -- drop silently.
        }
    }

    private void DrainLoop()
    {
        try
        {
            foreach (var envelope in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try { EmitOne(envelope); }
                catch (Exception ex)
                {
                    _log.LogM(LogLevel.Error, LogCategory,
                        $"Unhandled error emitting '{envelope.Type}' event: {ex}");
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private void EmitOne(EventEnvelope envelope)
    {
        string json = JsonSerializer.Serialize(envelope, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, _url);
        request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = _http.Send(request, HttpCompletionOption.ResponseContentRead, _cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                string body;
                try { body = response.Content.ReadAsStringAsync(_cts.Token).Result; }
                catch { body = "<unavailable>"; }
                _log.LogM(LogLevel.Error, LogCategory,
                    $"Event POST failed ('{envelope.Type}'): HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 300)}");
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory, $"Event POST error ('{envelope.Type}'): {ex.Message}");
        }
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
