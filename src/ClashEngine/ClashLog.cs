using System;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine;

/// <summary>
/// ClashEngine-only verbosity threshold. Higher = more chatter.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><see cref="Off"/> -- even <c>Info</c> from the wrapper is suppressed; only
/// <c>Warn</c> / <c>Error</c> get through. Use when ClashEngine is healthy and you want quiet.</description></item>
/// <item><description><see cref="Normal"/> (default) -- lifecycle <c>Info</c> emitted; verbose
/// surfaces silent.</description></item>
/// <item><description><see cref="Verbose"/> -- adds <c>Debug</c>: command inputs/outcomes, engine
/// event translations, orchestrator phase transitions.</description></item>
/// <item><description><see cref="Trace"/> -- adds <c>Trace</c>: every inbound wire event
/// (connect/disconnect/ship/freq/kill), every queue mutation. Loud -- intended for triage of a
/// specific report, not steady-state.</description></item>
/// </list>
/// </remarks>
public enum ClashVerbosity
{
    Off = 0,
    Normal = 1,
    Verbose = 2,
    Trace = 3,
}

/// <summary>
/// Thin wrapper around <see cref="ILogManager"/> that gates <c>Debug</c> / <c>Trace</c>
/// logging behind a runtime-mutable verbosity threshold scoped to ClashEngine. <c>Info</c> /
/// <c>Warn</c> / <c>Error</c> always emit (subject to the host's global level).
/// </summary>
/// <remarks>
/// <para>The <see cref="Level"/> property is mutable so an admin command (<c>?clashlog</c>)
/// can flip verbosity at runtime without an arena reload.</para>
///
/// <para>Hot paths should branch on <see cref="IsDebug"/> / <see cref="IsTrace"/> before
/// concatenating message strings, so message-build cost is paid only when the level is on.</para>
///
/// <para>Pass an existing <see cref="ILogManager"/> for the underlying sink -- every emission
/// goes through it via <see cref="LogLevel.Drivel"/> for verbose and <see cref="LogLevel.Info"/>
/// for the rest, so the host's standard log routing/filtering still applies.</para>
/// </remarks>
public sealed class ClashLog
{
    private readonly ILogManager _log;

    public ClashLog(ILogManager log, ClashVerbosity initial = ClashVerbosity.Normal)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        Level = initial;
    }

    /// <summary>Runtime-mutable verbosity threshold. Default <see cref="ClashVerbosity.Normal"/>.</summary>
    public ClashVerbosity Level { get; set; }

    /// <summary>True when <c>Debug</c> would emit. Use to gate string concat on hot paths.</summary>
    public bool IsDebug => Level >= ClashVerbosity.Verbose;

    /// <summary>True when <c>Trace</c> would emit. Use to gate string concat on hot paths.</summary>
    public bool IsTrace => Level >= ClashVerbosity.Trace;

    public void Trace(string category, string message)
    {
        if (Level >= ClashVerbosity.Trace) _log.LogM(LogLevel.Drivel, category, $"[trace] {message}");
    }

    public void Debug(string category, string message)
    {
        if (Level >= ClashVerbosity.Verbose) _log.LogM(LogLevel.Drivel, category, $"[debug] {message}");
    }

    public void Info(string category, string message)
    {
        if (Level >= ClashVerbosity.Normal) _log.LogM(LogLevel.Info, category, message);
    }

    public void Warn(string category, string message) => _log.LogM(LogLevel.Warn, category, message);

    public void Error(string category, string message) => _log.LogM(LogLevel.Error, category, message);

    public static ClashVerbosity ParseVerbosity(string? raw, ClashVerbosity fallback = ClashVerbosity.Normal)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return raw.Trim().ToLowerInvariant() switch
        {
            "off" or "0" or "none" => ClashVerbosity.Off,
            "normal" or "1" or "info" => ClashVerbosity.Normal,
            "verbose" or "2" or "debug" => ClashVerbosity.Verbose,
            "trace" or "3" or "all" => ClashVerbosity.Trace,
            _ => fallback,
        };
    }
}
