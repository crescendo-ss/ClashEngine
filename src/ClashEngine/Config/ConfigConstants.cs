namespace ClashEngine.Config;

/// <summary>Shared constants for the <c>[ClashEngine]</c> config section so every parser /
/// helper class agrees on the section name and the log category used for parse warnings.</summary>
internal static class ConfigConstants
{
    public const string Section = "ClashEngine";

    /// <summary>Log category for every parse-time warning. Stable across the parser split so
    /// existing log filters keep working.</summary>
    public const string LogCategory = "MatchmakingConfig";
}
