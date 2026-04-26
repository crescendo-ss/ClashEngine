using System;
using System.Collections.Generic;

namespace ClashEngine.Core.Matches;

/// <summary>
/// Combines two or more <see cref="IMatchEndPolicy"/> instances; the match ends as soon as ANY
/// of them fires. Lets a game type declare multiple win conditions ("first to 30 kills OR
/// higher score at 20 minutes") without each policy needing to know about the others.
/// </summary>
/// <remarks>
/// <para>On every <see cref="CheckOutcome"/> call, the children are evaluated in registration
/// order and the first non-null outcome wins. If two policies happen to fire on the same tick,
/// the one earlier in the list takes precedence -- the registration order is a tiebreaker. In
/// practice the policies typically agree on the winner anyway (whichever team triggered the
/// kill-count is also leading by score), but the tiebreaker is deterministic.</para>
///
/// <para>An empty policy list never ends the match. At least one child policy is required.</para>
/// </remarks>
public sealed class CompositeEndPolicy : IMatchEndPolicy
{
    private readonly IReadOnlyList<IMatchEndPolicy> _policies;

    public CompositeEndPolicy(params IMatchEndPolicy[] policies)
        : this((IReadOnlyList<IMatchEndPolicy>)policies) { }

    public CompositeEndPolicy(IReadOnlyList<IMatchEndPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        if (policies.Count == 0)
            throw new ArgumentException("At least one policy is required.", nameof(policies));
        for (int i = 0; i < policies.Count; i++)
            if (policies[i] is null)
                throw new ArgumentException($"policies[{i}] is null.", nameof(policies));
        _policies = policies;
    }

    public IReadOnlyList<IMatchEndPolicy> Policies => _policies;

    public MatchOutcome? CheckOutcome(ActiveMatch match, DateTimeOffset at)
    {
        for (int i = 0; i < _policies.Count; i++)
        {
            var outcome = _policies[i].CheckOutcome(match, at);
            if (outcome is not null) return outcome;
        }
        return null;
    }
}
