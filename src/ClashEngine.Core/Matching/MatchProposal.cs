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
    DateTimeOffset ProposedAt);
