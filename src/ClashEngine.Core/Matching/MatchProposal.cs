using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Matching;

/// <summary>
/// Output of <see cref="Matcher.TryProposeMatch"/>: a concrete match the engine should now form.
/// The matched players have already been removed from every queue they were searching in.
/// </summary>
public sealed record MatchProposal(
    string QueueName,
    MatchShape Shape,
    IReadOnlyList<IReadOnlyList<PlayerKey>> Teams,
    double Quality,
    DateTimeOffset ProposedAt,
    IReadOnlyDictionary<PlayerKey, IReadOnlyList<string>> AlsoRemovedFrom)
{
    /// <summary>
    /// Per-player list of the *other* queues each matched player was searching in besides
    /// <see cref="QueueName"/> (the queue this match formed from). The matcher dequeued the player
    /// from all of them; the engine surfaces a removal for <see cref="QueueName"/> directly and uses
    /// this map to also surface the drops on those other queues. Players in only the proposal queue
    /// are absent from the map (the common case), so it is empty for single-queue searches.
    /// </summary>
    public static readonly IReadOnlyDictionary<PlayerKey, IReadOnlyList<string>> NoOtherQueues =
        new Dictionary<PlayerKey, IReadOnlyList<string>>();
}
