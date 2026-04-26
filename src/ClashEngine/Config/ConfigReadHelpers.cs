using System;
using System.Globalization;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Config;

/// <summary>Small typed-read helpers over <see cref="IConfigManager"/> for the parser layer.
/// Each helper distinguishes "missing" from "explicit 0" / parse failure so the parsers can
/// decide how to default.</summary>
internal static class ConfigReadHelpers
{
    /// <summary>Returns the int value at <paramref name="key"/>, or <see langword="null"/> if the
    /// key is missing. Uses <see cref="int.MinValue"/> as a sentinel so an explicit <c>0</c> in
    /// the conf is honored (rather than collapsing into "missing").</summary>
    public static int? TryReadInt(IConfigManager config, string key)
    {
        const int Sentinel = int.MinValue;
        int v = config.GetInt(config.Global, ConfigConstants.Section, key, Sentinel);
        return v == Sentinel ? null : v;
    }

    /// <summary>Returns the double value at <paramref name="key"/>, or <see langword="null"/>
    /// if the key is missing or unparseable. <see cref="IConfigManager"/> only exposes string
    /// reads for floats, so we parse manually.</summary>
    public static double? TryReadDouble(IConfigManager config, string key)
    {
        var s = config.GetStr(config.Global, ConfigConstants.Section, key);
        if (string.IsNullOrWhiteSpace(s)) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>Returns the <see cref="TimeSpan"/> at <paramref name="key"/>. Accepts
    /// <c>HH:MM:SS</c>, <c>MM:SS</c>, or plain integer seconds. Logs a warn if the value is
    /// present but unparseable; returns <see langword="null"/> for missing or unparseable.</summary>
    public static TimeSpan? TryReadTimeSpan(IConfigManager config, string key, ClashLog? log, string contextPrefix)
    {
        var s = config.GetStr(config.Global, ConfigConstants.Section, key);
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts)) return ts;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);
        log?.Warn(ConfigConstants.LogCategory,
            $"{contextPrefix}: could not parse '{s}' for {key} as HH:MM:SS or seconds; using default.");
        return null;
    }
}
