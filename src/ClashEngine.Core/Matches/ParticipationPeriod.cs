using System;

namespace ClashEngine.Core.Matches;

/// <summary>
/// One window during which a player was actively present in the match. <see cref="Exit"/> is
/// <see langword="null"/> while the player is still present; it is set when they enter grace
/// (spec, leave, disconnect) or when the match ends.
/// </summary>
public readonly record struct ParticipationPeriod(DateTimeOffset Enter, DateTimeOffset? Exit);
