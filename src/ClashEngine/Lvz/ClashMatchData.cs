using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Stats;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.League;
using SS.Matchmaking.TeamVersus;

namespace ClashEngine.Lvz;

/// <summary>
/// <see cref="IMatchData"/> adapter for a ClashEngine <see cref="ActiveMatch"/>. Lets the
/// SubspaceServer <c>MatchLvz</c> module render statbox / scoreboard / timer for our matches
/// without ClashEngine having to reimplement the rendering itself.
/// </summary>
/// <remarks>
/// <para>This adapter is constructed once per match (by <see cref="MatchLvzAdapter"/>) and lives
/// for the lifetime of the match. <see cref="Teams"/> is materialized eagerly so the consumer
/// gets stable references throughout the match.</para>
///
/// <para>Reads through to live engine state for things that change over time (score, status).
/// Things that are immutable for a match (team layout, match identifier) are snapshotted at
/// construction.</para>
/// </remarks>
internal sealed class ClashMatchData : IMatchData
{
    private readonly ReadOnlyCollection<ITeam> _teamsRO;
    private readonly ClashMatchConfiguration _configuration;
    private readonly string _arenaName;
    private readonly IArenaManager _arenaManager;

    public ClashMatchData(
        ActiveMatch match,
        QueueDefinition queue,
        StatsRecorder? statsRecorder,
        PlayerKeyResolver resolver,
        IArenaManager arenaManager,
        int arenaNumber)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(arenaManager);

        Match = match;
        Queue = queue;
        StatsRecorder = statsRecorder;
        Resolver = resolver;
        _arenaManager = arenaManager;
        _arenaName = queue.MatchArenaName ?? string.Empty;

        // Match identity stable across the whole match. ArenaNumber is constant per arena
        // attach; BoxIdx defaults to 0 since ClashEngine doesn't multi-box yet.
        MatchIdentifier = new MatchIdentifier(MatchType: queue.Name, ArenaNumber: arenaNumber, BoxIdx: 0);

        _configuration = new ClashMatchConfiguration(match, queue);

        // Materialize the team/slot tree eagerly. Each slot holds a back-pointer to its team
        // and to this MatchData, so MatchLvz can navigate the structure without recomputing.
        var teams = new ITeam[match.Teams.Count];
        for (int t = 0; t < match.Teams.Count; t++)
            teams[t] = new ClashTeam(this, t, match.Teams[t]);
        _teamsRO = Array.AsReadOnly(teams);
    }

    /// <summary>The ClashEngine match this adapter wraps. Internal so the team/slot adapters can
    /// reach back through to live state.</summary>
    internal ActiveMatch Match { get; }
    internal QueueDefinition Queue { get; }
    internal StatsRecorder? StatsRecorder { get; }
    internal PlayerKeyResolver Resolver { get; }

    public MatchIdentifier MatchIdentifier { get; }
    public IMatchConfiguration Configuration => _configuration;
    public string ArenaName => _arenaName;
    public Arena? Arena => string.IsNullOrEmpty(_arenaName) ? null : _arenaManager.FindArena(_arenaName);
    public ReadOnlyCollection<ITeam> Teams => _teamsRO;
    public DateTime? Started => Match.StartedAt?.UtcDateTime;
    public LeagueGameInfo? LeagueGame => null;   // ClashEngine doesn't run league games via this adapter
}
