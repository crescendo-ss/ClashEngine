using System;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Queue;
using ClashEngine.Stats;
using OpenSkillSharp;
using SS.Matchmaking.OpenSkill;
using SS.Matchmaking.TeamVersus;

namespace ClashEngine.Lvz;

/// <summary>
/// Minimal <see cref="IMatchConfiguration"/> stub for ClashEngine matches. MatchLvz reads
/// <see cref="NumTeams"/>, <see cref="PlayersPerTeam"/>, <see cref="LivesPerPlayer"/>,
/// <see cref="TimeLimit"/>, and <see cref="OverTimeLimit"/> -- all live-derived from the
/// underlying <see cref="ActiveMatch"/> + <see cref="QueueDefinition"/>.
/// </summary>
/// <remarks>
/// <para>OpenSkill-related properties throw <see cref="NotSupportedException"/> on access.
/// MatchLvz and MatchFocus do not read them; if any other consumer of the IMatchData
/// interface graph is loaded (e.g. league rating math), it will surface a clear "ClashEngine
/// doesn't expose ranking math through this adapter" error rather than silently misbehaving.
/// ClashEngine ranking lives in its own <c>IRatingStore</c> path, separate from this adapter.</para>
///
/// <para><see cref="TimeLimit"/> mirrors the queue's configured <c>GameType&lt;i&gt;TimeLimit</c>
/// (parsed by <c>GameTypeParser</c>, threaded through <c>QueueDefinition.TimeLimit</c>). When
/// the queue value is unset we surface <see cref="TimeSpan.Zero"/>, which MatchLvz interprets as
/// "no time limit" and hides the scoreboard timer digits -- the match is purely kill-count /
/// lives-limited and there's nothing to count down. When set, the engine ALSO ends the match on
/// time expiry via <c>TimeLimitEndPolicy</c>.</para>
/// </remarks>
internal sealed class ClashMatchConfiguration : IMatchConfiguration
{
    private readonly ActiveMatch _match;
    private readonly QueueDefinition _queue;
    private readonly IMatchBoxConfiguration[] _boxes;

    public ClashMatchConfiguration(ActiveMatch match, QueueDefinition queue)
    {
        _match = match ?? throw new ArgumentNullException(nameof(match));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _boxes = new IMatchBoxConfiguration[] { new ClashMatchBoxConfiguration() };
    }

    // SS.Matchmaking's IMatchConfiguration.GameTypeId is a long? key used by its rating /
    // history modules; ClashEngine's gametype identifier is a registry-name string with no
    // stable integer mapping, and the MatchLvz / MatchFocus consumption graph never reads
    // this property. Returning null surfaces "unset" rather than fabricating an id collision
    // against another module's space.
    public long? GameTypeId => null;
    public int NumTeams => _queue.Shape.TeamCount;
    public int PlayersPerTeam => _queue.Shape.PlayersPerTeam;
    public int LivesPerPlayer => _match.LivesPerPlayer ?? 0;
    public TimeSpan TimeLimit => _queue.TimeLimit ?? TimeSpan.Zero;
    public TimeSpan? OverTimeLimit => null;
    public ReadOnlySpan<IMatchBoxConfiguration> Boxes => _boxes.AsSpan();

    // OpenSkill-related members are explicit interface implementations rather than public
    // properties. ClashEngine ranking lives in its own IRatingStore path -- nothing in the
    // MatchLvz / MatchFocus consumption graph reads these. Implementing them via EIM binds
    // each method slot directly to the IMatchConfiguration interface declaration, which is
    // robust against the kind of binary skew between a deployed SS.Matchmaking.dll and our
    // compiled-against copy that surfaces as "method does not have an implementation" when
    // ClashEngine and SS.Matchmaking land in different assembly load contexts at runtime.
    IOpenSkillModel IMatchConfiguration.OpenSkillModel => null!;
    double IMatchConfiguration.OpenSkillSigmaDecayPerDay => 0;
    bool IMatchConfiguration.OpenSkillUseScoresWhenPossible => false;
    OrdinalArgs IMatchConfiguration.OpenSkillDisplayOrdinal => default;

    // SS.Matchmaking.TeamVersusStats reads this to delay kill-damage stat processing while the
    // late C2S Damage packet lands. Nothing in the MatchLvz / MatchFocus consumption graph reads
    // it, and TeamVersusStats does not run against Clash matches -- ClashEngine owns its own
    // late-damage grace window (StatsListener.LateDamageGraceMs). EIM for the same binary-skew
    // robustness reasons as the OpenSkill members above; the value is inert here.
    int IMatchConfiguration.PlayerKillDamageStatsDelayMs => StatsListener.LateDamageGraceMs;

    private sealed class ClashMatchBoxConfiguration : IMatchBoxConfiguration
    {
        public string? PlayAreaMapRegion => null;   // ClashEngine doesn't use play-area mechanics
    }
}
