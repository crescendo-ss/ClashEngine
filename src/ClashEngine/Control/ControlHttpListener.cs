using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Control;
using ClashEngine.Stats;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Control;

/// <summary>
/// The inbound half of the control surface: a <see cref="HttpListener"/> an external matchmaking
/// service (web UI, Discord bot) POSTs commands to and pulls state snapshots from. Two routes,
/// both <c>X-Api-Key</c> authenticated:
/// <list type="bullet">
/// <item><c>POST {prefix}commands</c> -- one <c>schema/command.schema.json</c> body, answered
/// with <c>schema/commandresponse.schema.json</c>.</item>
/// <item><c>GET {prefix}state</c> -- the full <c>schema/snapshot.schema.json</c> snapshot, the
/// self-heal companion to the outbound event stream.</item>
/// </list>
/// Requests arrive on listener worker threads; every engine touch is bounced onto the mainloop
/// via <see cref="MainloopDispatcher"/> (the same discipline as <c>?play</c>'s deferred enqueue).
/// Enqueue-flavored commands first await the same rating pre-pull <c>?play</c> performs, so both
/// front-ends feed the matcher identically. Deliberately BCL-only -- no ASP.NET Core inside the
/// plug-in's AssemblyLoadContext.
/// </summary>
public sealed class ControlHttpListener : IDisposable
{
    private const string LogCategory = nameof(ControlHttpListener);

    /// <summary>Ceiling on a command body; a legitimate command is a few hundred bytes.</summary>
    private const long MaxBodyBytes = 256 * 1024;

    /// <summary>How long a request waits for the mainloop before answering 503. The loop ticks
    /// every 500 ms, so multiple seconds of headroom means "wedged", not "busy".</summary>
    private static readonly TimeSpan MainloopTimeout = TimeSpan.FromSeconds(5);

    // camelCase to match the schema files; case-insensitive read so consumers may send either;
    // WhenWritingNull keeps optional response fields off the wire (mirrors HttpEventSink).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _prefix;
    private readonly byte[] _apiKeyUtf8;
    private readonly MatchmakingEngine _engine;
    private readonly ControlCommandDispatcher _dispatcher;
    private readonly RatingsCoordinator _ratingsCoordinator;
    private readonly MainloopDispatcher _mainloop;
    private readonly IClock _clock;
    private readonly ILogManager _log;

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public ControlHttpListener(
        string listenUrl,
        string apiKey,
        MatchmakingEngine engine,
        ControlCommandDispatcher dispatcher,
        RatingsCoordinator ratingsCoordinator,
        MainloopDispatcher mainloop,
        IClock clock,
        ILogManager log)
    {
        if (string.IsNullOrWhiteSpace(listenUrl))
            throw new ArgumentException("Must be non-empty.", nameof(listenUrl));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Must be non-empty.", nameof(apiKey));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _ratingsCoordinator = ratingsCoordinator ?? throw new ArgumentNullException(nameof(ratingsCoordinator));
        _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _prefix = listenUrl.EndsWith('/') ? listenUrl : listenUrl + "/";
        _apiKeyUtf8 = Encoding.UTF8.GetBytes(apiKey);
        _listener.Prefixes.Add(_prefix);
    }

    /// <summary>Binds the listener and starts the accept loop. Throws (e.g. address in use)
    /// rather than degrading silently -- the caller decides whether that's fatal.</summary>
    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);

        if (Uri.TryCreate(_prefix, UriKind.Absolute, out var uri) && !uri.IsLoopback)
            _log.LogM(LogLevel.Warn, LogCategory,
                $"ControlListenUrl '{_prefix}' is not loopback; the control surface is reachable " +
                "from the network. Make sure that is intended and the api key is strong.");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                break;   // Stop()/Dispose() aborted the pending accept -- normal shutdown.
            }
            catch (Exception ex)
            {
                _log.LogM(LogLevel.Error, LogCategory, $"Accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleRequestSafeAsync(context));
        }
    }

    private async Task HandleRequestSafeAsync(HttpListenerContext context)
    {
        try
        {
            await HandleRequestAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory, $"Request handling failed: {ex}");
            TryWrite(context, 500, new ControlResponse(
                ControlSchema.Version, null, ControlStatus.Error,
                ControlResults.InternalError, "Unhandled server error."));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;

        if (!IsAuthorized(request))
        {
            TryWrite(context, 401, new ControlResponse(
                ControlSchema.Version, null, ControlStatus.Error,
                ControlResults.InvalidField, "Missing or wrong X-Api-Key."));
            return;
        }

        string path = request.Url?.AbsolutePath ?? string.Empty;
        bool isCommands = path.EndsWith("/commands", StringComparison.OrdinalIgnoreCase);
        bool isState = path.EndsWith("/state", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (isCommands && string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCommandAsync(context).ConfigureAwait(false);
            }
            else if (isState && string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                var snapshot = await _mainloop.RunAsync(
                    () => StateSnapshotBuilder.Build(_engine, _clock.UtcNow), MainloopTimeout)
                    .ConfigureAwait(false);
                TryWrite(context, 200, snapshot);
            }
            else if (isCommands || isState)
            {
                TryWrite(context, 405, new ControlResponse(
                    ControlSchema.Version, null, ControlStatus.Error,
                    ControlResults.InvalidField,
                    isCommands ? "Use POST for /commands." : "Use GET for /state."));
            }
            else
            {
                TryWrite(context, 404, new ControlResponse(
                    ControlSchema.Version, null, ControlStatus.Error,
                    ControlResults.InvalidField, "Unknown path; expected /commands or /state."));
            }
        }
        catch (TimeoutException)
        {
            // The work item may still run later, so this is "result unknown", not "not applied".
            _log.LogM(LogLevel.Warn, LogCategory, "Mainloop did not service a control request in time.");
            TryWrite(context, 503, new ControlResponse(
                ControlSchema.Version, null, ControlStatus.Error,
                ControlResults.InternalError, "Engine busy; result unknown."));
        }
    }

    private async Task HandleCommandAsync(HttpListenerContext context)
    {
        var request = context.Request;
        if (request.ContentLength64 > MaxBodyBytes)
        {
            TryWrite(context, 413, new ControlResponse(
                ControlSchema.Version, null, ControlStatus.Error,
                ControlResults.InvalidField, "Body too large."));
            return;
        }

        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = await reader.ReadToEndAsync(_cts.Token).ConfigureAwait(false);

        ControlCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<ControlCommand>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            TryWrite(context, 400, new ControlResponse(
                ControlSchema.Version, null, ControlStatus.Error,
                ControlResults.InvalidField, $"Malformed JSON: {ex.Message}"));
            return;
        }
        if (command is null)
        {
            TryWrite(context, 400, new ControlResponse(
                ControlSchema.Version, null, ControlStatus.Error,
                ControlResults.InvalidField, "Empty body."));
            return;
        }

        // Same flow as ?play: resolve who the command drags into matchmaking (mainloop read),
        // await their rating pulls off-thread (no-op when cached), then execute on the mainloop.
        var seedPlan = await _mainloop.RunAsync(
            () => _dispatcher.PlanRatingSeed(command), MainloopTimeout).ConfigureAwait(false);
        if (seedPlan is not null)
        {
            var pulls = new Task[seedPlan.Players.Count];
            for (int i = 0; i < pulls.Length; i++)
                pulls[i] = _ratingsCoordinator.EnsurePulledAsync(seedPlan.Players[i], seedPlan.GameType);
            try { await Task.WhenAll(pulls).ConfigureAwait(false); }
            catch
            {
                // EnsurePulledAsync is contracted not to throw; if a future impl regresses, the
                // command still executes against whatever ratings are cached (?play does the same).
            }
        }

        var response = await _mainloop.RunAsync(
            () => _dispatcher.Execute(command, _clock.UtcNow), MainloopTimeout).ConfigureAwait(false);
        TryWrite(context, 200, response);
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        string? presented = request.Headers["X-Api-Key"];
        if (string.IsNullOrEmpty(presented)) return false;
        byte[] presentedUtf8 = Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(presentedUtf8, _apiKeyUtf8);
    }

    private void TryWrite<T>(HttpListenerContext context, int statusCode, T payload)
    {
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            var response = context.Response;
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes);
            response.Close();
        }
        catch (Exception ex)
        {
            // Client hung up mid-write, or the listener is shutting down -- nothing to salvage.
            _log.LogM(LogLevel.Drivel, LogCategory, $"Response write failed: {ex.Message}");
            try { context.Response.Abort(); } catch { /* already gone */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* not started */ }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(5)); } catch { /* swallow */ }
        try { _listener.Close(); } catch { /* already closed */ }
        _cts.Dispose();
    }
}
