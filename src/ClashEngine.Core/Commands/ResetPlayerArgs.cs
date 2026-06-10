using System;

namespace ClashEngine.Core.Commands;

/// <summary>
/// Parsed arguments for the operator <c>?resetplayer &lt;player&gt; [--keep-rating]</c> command.
/// Lives in Core (no SS types) so the parsing rules are unit-testable.
/// </summary>
/// <remarks>
/// Subspace player names may contain spaces, so the name is never tokenized: flags are
/// recognized only at the start and end of the argument string, and whatever remains between
/// them — verbatim, interior whitespace included — is the player name. A <c>--</c> token in the
/// middle of the remainder is treated as part of the name, not a flag.
/// </remarks>
/// <param name="PlayerName">The target player name, or <see langword="null"/> when none was
/// supplied (caller should reply with usage).</param>
/// <param name="KeepRating">Whether <c>--keep-rating</c> was passed.</param>
/// <param name="UnknownFlag">The offending token when an unrecognized <c>--flag</c> was found at
/// either end; <see langword="null"/> on success. When set, the other fields are unusable.</param>
public readonly record struct ResetPlayerArgs(string? PlayerName, bool KeepRating, string? UnknownFlag)
{
    public static ResetPlayerArgs Parse(ReadOnlySpan<char> parameters)
    {
        var rest = parameters.Trim();
        bool keepRating = false;

        while (!rest.IsEmpty)
        {
            if (rest.StartsWith("--", StringComparison.Ordinal))
            {
                int sp = rest.IndexOfAny(' ', '\t');
                var flag = sp < 0 ? rest : rest[..sp];
                if (!IsKeepRating(flag)) return new(null, false, flag.ToString());
                keepRating = true;
                rest = sp < 0 ? ReadOnlySpan<char>.Empty : rest[(sp + 1)..].Trim();
                continue;
            }

            int lastSp = rest.LastIndexOfAny(' ', '\t');
            if (lastSp >= 0 && rest[(lastSp + 1)..].StartsWith("--", StringComparison.Ordinal))
            {
                var flag = rest[(lastSp + 1)..];
                if (!IsKeepRating(flag)) return new(null, false, flag.ToString());
                keepRating = true;
                rest = rest[..lastSp].Trim();
                continue;
            }

            break;
        }

        return rest.IsEmpty
            ? new(null, keepRating, null)
            : new(rest.ToString(), keepRating, null);
    }

    private static bool IsKeepRating(ReadOnlySpan<char> flag) =>
        flag.Equals("--keep-rating", StringComparison.OrdinalIgnoreCase);
}
