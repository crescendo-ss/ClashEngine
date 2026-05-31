using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClashEngine.Adapter;
using ClashEngine.Config;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Eligibility;
using ClashEngine.Core.Groups;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Queue;
using ClashEngine.Orchestration;
using ClashEngine.Stats;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Commands;

/// <summary>
/// Player commands for matchmaking and groups. Each handler runs on the mainloop thread (the
/// command manager invokes from chat/input which already lands there).
/// </summary>
public sealed class MatchmakingCommands
{
    private const string LogCategory = nameof(MatchmakingCommands);

    private readonly MatchmakingEngine _engine;
    private readonly ICommandManager _commands;
    private readonly IChat _chat;
    private readonly IClock _clock;
    private readonly PlayerKeyResolver _resolver;
    private readonly IConfigManager _config;
    private readonly ClashLog _log;
    private readonly MatchOrchestratorRegistry _orchestrators;
    private readonly RatingsCoordinator _ratingsCoordinator;
    private readonly IMainloop _mainloop;

    private static readonly string[] Tiers = { "competitive", "casual" };

    public MatchmakingCommands(
        MatchmakingEngine engine,
        ICommandManager commands,
        IChat chat,
        IClock clock,
        PlayerKeyResolver resolver,
        IConfigManager config,
        ClashLog log,
        MatchOrchestratorRegistry orchestrators,
        RatingsCoordinator ratingsCoordinator,
        IMainloop mainloop)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _orchestrators = orchestrators ?? throw new ArgumentNullException(nameof(orchestrators));
        _ratingsCoordinator = ratingsCoordinator ?? throw new ArgumentNullException(nameof(ratingsCoordinator));
        _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
    }

    /// <summary>
    /// Registers commands that should be reachable from any arena -- specifically the
    /// queue-entry commands so a player in a generic/lobby arena can opt in without first
    /// travelling to a match arena. Pair with <see cref="UnregisterGlobal"/>.
    /// </summary>
    public void RegisterGlobal()
    {
        // Naming notes:
        //   ?play  -- entry-point queue command; renamed from ?next to avoid the global
        //             collision with MatchmakingQueues.next.
        _commands.AddCommand("play", Next, helpText:
            "?play <queue name> -- Queue for the next ClashEngine match. Multiple space-separated " +
            "words are joined with '_' (e.g. ?play casual 4v4 looks up 'casual_4v4').");
        _commands.AddCommand("queue", Queue, helpText:
            "?queue [name] -- List all queues defined for this arena, or inspect a single queue's " +
            "waiting players. Multi-word lookup joins with '_' (e.g. ?queue casual 4v4).");
        _commands.AddCommand("connect", Connect, helpText:
            "?connect discord <alias> -- Link your in-game name to a Discord alias so the Discord " +
            "bot can DM you about queues and matches. The alias is relayed to the bot service, " +
            "which performs the actual link -- ClashEngine itself stores nothing.");
        _commands.AddCommand("autoqueue", AutoQueue, helpText:
            "?autoqueue [on|off] -- View or set auto-queue. When on, you're automatically put back " +
            "into a match's queue when it ends. Persists across sessions. Auto-disabled if you're " +
            "flagged AFK.");
    }

    public void UnregisterGlobal()
    {
        _commands.RemoveCommand("play", Next);
        _commands.RemoveCommand("queue", Queue);
        _commands.RemoveCommand("connect", Connect);
        _commands.RemoveCommand("autoqueue", AutoQueue);
    }

    /// <summary>
    /// Registers commands that only make sense inside a ClashEngine match arena -- ?cancel,
    /// ?return, party/group commands, ?forgive, etc. Called from
    /// <see cref="ClashModule"/>'s <c>AttachModuleAsync</c>; the arena scope keeps these out
    /// of the global ?man listing in arenas that haven't attached the module.
    /// </summary>
    public void RegisterArena(Arena arena)
    {
        // ?accept and ?cancel are short bare names that other modules (e.g. CaptainsMatch)
        // may also register per-arena. Arena-scoping makes any collision arena-local rather
        // than zone-wide.
        // ?party -- group invite command; renamed from ?group to avoid the collision with
        //          PlayerGroups.group when both modules end up in the same arena.
        _commands.AddCommand("rating", Rating, arena, helpText:
            "?rating -- Show your skill rating per game type.");
        _commands.AddCommand("cancel", Cancel, arena, helpText:
            "?cancel -- Leave every ClashEngine matchmaking queue.");
        _commands.AddCommand("return", Return, arena, helpText:
            "?return -- Rejoin the match you were specced from. Bypasses the per-match freq lock " +
            "by placing you directly on your assigned ship and team freq.");
        _commands.AddCommand("party", Group, arena, helpText:
            "?party -- List the members of your current party. " +
            "?party player1[,player2,...] -- Invite one or more players to your ClashEngine group.");
        _commands.AddCommand("accept", Accept, arena, helpText:
            "?accept [inviter] -- Accept a pending ClashEngine group invitation. Inviter is optional when only one is pending.");
        _commands.AddCommand("decline", Decline, arena, helpText:
            "?decline [inviter] -- Decline a pending ClashEngine invitation.");
        _commands.AddCommand("leaveparty", LeaveParty, arena, helpText:
            "?leaveparty -- Leave your current party. If you're the leader of a closed party, the party disbands.");
        _commands.AddCommand("partymode", PartyMode, arena, helpText:
            "?partymode [open|closed] -- View or change your party's mode. Closed parties have a leader who controls invites.");
        _commands.AddCommand("forgive", Veto, arena, helpText:
            "?forgive <player> -- Vote to overturn a pending griefing penalty for a match participant.");
        _commands.AddCommand("clashlog", ClashLogCmd, arena, helpText:
            "?clashlog [off|normal|verbose|trace] -- Show or set ClashEngine debug verbosity. " +
            "Affects only ClashEngine's verbose/trace logging; the host's global level still gates Info/Warn/Error.");
        _commands.AddCommand("clashreset", ClashReset, arena, helpText:
            "?clashreset <player> [--keep-rating] -- Operator: wipe a player's matchmaking state " +
            "(penalties, queues, party, active match, pending griefing votes, and rating). " +
            "Pass --keep-rating to preserve their rating rows.");
        _commands.AddCommand("helpclash", HelpClash, arena, helpText:
            "?helpclash -- List ClashEngine player commands and what they do.");
    }

    public void UnregisterArena(Arena arena)
    {
        _commands.RemoveCommand("rating", Rating, arena);
        _commands.RemoveCommand("cancel", Cancel, arena);
        _commands.RemoveCommand("return", Return, arena);
        _commands.RemoveCommand("party", Group, arena);
        _commands.RemoveCommand("accept", Accept, arena);
        _commands.RemoveCommand("decline", Decline, arena);
        _commands.RemoveCommand("leaveparty", LeaveParty, arena);
        _commands.RemoveCommand("partymode", PartyMode, arena);
        _commands.RemoveCommand("forgive", Veto, arena);
        _commands.RemoveCommand("clashlog", ClashLogCmd, arena);
        _commands.RemoveCommand("clashreset", ClashReset, arena);
        _commands.RemoveCommand("helpclash", HelpClash, arena);
    }

    /// <summary>Records who issued what command, with the player's current eligibility state
    /// snapshotted at the time. The combination is enough to triage "I tried to queue and it
    /// said InMatch" reports after the fact.</summary>
    private void LogCommand(string cmd, Player player, ReadOnlySpan<char> parameters)
    {
        if (!_log.IsDebug) return;
        var elig = _resolver.KeyOf(player) is PlayerKey k
            ? _engine.CheckEligibility(k).Status.ToString()
            : "?unresolved";
        _log.Debug(LogCategory, $"?{cmd} by {player.Name ?? "(no-name)"} (eligibility={elig}) args=\"{parameters.ToString()}\"");
    }

    // ---- ?play <queue tokens...>
    //
    // Tight contract: split the args on ASCII whitespace, drop empty runs, join with '_'. The
    // result is the queue's BaseName, which we qualify with the player's current arena before
    // looking it up. Examples:
    //   ?play 4v4              -> "4v4"
    //   ?play casual 4v4       -> "casual_4v4"
    //   ?play casual_4v4       -> "casual_4v4"   (single token; no join needed)
    //   ?play   CASUAL    4v4  -> "CASUAL_4v4"   (whitespace collapsed; lookup is case-insensitive)
    // Empty args fall back to the arena's [ClashEngine] DefaultQueue.

    private void Next(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("play", player, parameters);
        var key = _resolver.KeyOf(player);
        if (key is not PlayerKey k) return;

        var arena = player.Arena;
        if (arena is null)
        {
            _chat.SendMessage(player, "Join an arena before queueing.");
            return;
        }

        var requestedBaseName = QueueRegistry.JoinTokensToQueueName(parameters);
        if (requestedBaseName.Length == 0)
        {
            // Fall back to the arena's [ClashEngine] DefaultQueue when no tokens supplied.
            var defaultQueue = arena.Cfg is { } cfg
                ? MatchmakingConfig.DefaultQueueForArena(_config, cfg)
                : null;
            if (string.IsNullOrWhiteSpace(defaultQueue))
            {
                _chat.SendMessage(player, "Usage: ?play <queue name>. Type ?queue to see available queues.");
                return;
            }
            requestedBaseName = defaultQueue.Trim();
        }

        var qualified = QueueRegistry.QualifyName(arena.BaseName, requestedBaseName);
        if (!_engine.Queues.TryGet(qualified, out var def))
        {
            _chat.SendMessage(player, $"Queue '{requestedBaseName}' is not defined for this arena. Type ?queue to see what's available here.");
            return;
        }
        string resolvedName = qualified;
        string gameTypeName = def.GameType;

        // Resolve the member set up front so we know which (player, gameType) pulls to await.
        // A solo queue only seeds the issuer; a group seeds every member so the matcher
        // sees fresh ratings for the whole party when it forms a match.
        var groupId = _engine.Groups.GroupOf(k);
        IReadOnlyList<PlayerKey>? partyMembers = null;
        PlayerKey[] membersToSeed;
        if (groupId is GroupId g)
        {
            var members = new List<PlayerKey>();
            foreach (var m in _engine.Groups.MembersOf(g)) members.Add(m);
            partyMembers = members;
            membersToSeed = members.ToArray();
        }
        else
        {
            membersToSeed = new[] { k };
        }

        // Defer the enqueue: await each member's rating pull (no-op if local already has a
        // row, awaits the connect-time task if one's in flight, fires a fresh pull if neither),
        // then bounce the enqueue + reply back onto the mainloop. The handler returns
        // immediately; queueing chat-replies just arrive a couple of pull-roundtrips later.
        var dispatch = new EnqueueDispatch(player, k, resolvedName, groupId, partyMembers);
        _ = Task.Run(async () =>
        {
            try
            {
                if (membersToSeed.Length == 1)
                {
                    await _ratingsCoordinator.EnsurePulledAsync(membersToSeed[0], gameTypeName).ConfigureAwait(false);
                }
                else
                {
                    var awaits = new Task[membersToSeed.Length];
                    for (int i = 0; i < membersToSeed.Length; i++)
                        awaits[i] = _ratingsCoordinator.EnsurePulledAsync(membersToSeed[i], gameTypeName);
                    await Task.WhenAll(awaits).ConfigureAwait(false);
                }
            }
            catch
            {
                // EnsurePulledAsync is contracted not to throw, but if a future impl regresses
                // we still want the enqueue to run -- the matcher will just use whatever
                // values are currently in the cache, which is the prior-to-this-feature
                // behavior.
            }
            _mainloop.QueueMainWorkItem(DispatchEnqueue, dispatch);
        });
    }

    /// <summary>
    /// Mainloop-side resumption of <see cref="Next"/> after the pull await finished. Runs
    /// the actual <see cref="MatchmakingEngine.TryEnqueue"/> call, logs, and replies. Reads
    /// <see cref="_engine"/> through a local null-check so a dispatch that fires after the
    /// module unloaded doesn't NRE in the work-item queue.
    /// </summary>
    private void DispatchEnqueue(EnqueueDispatch s)
    {
        var now = _clock.UtcNow;
        EnqueueResult result;
        if (s.GroupId is GroupId g)
            result = _engine.TryEnqueueGroup(s.PartyMembers!, s.ResolvedName, now, out _, existingGroup: g, initiator: s.Key);
        else
            result = _engine.TryEnqueue(s.Key, s.ResolvedName, now);

        if (_log.IsDebug)
            _log.Debug(LogCategory, $"?play {s.Key.Name} -> queue '{s.ResolvedName}' result={result}" +
                (s.GroupId is GroupId gg ? $" (group {gg})" : ""));
        ReplyForEnqueue(s.Player, s.Key, s.ResolvedName, result, s.PartyMembers);
    }

    /// <summary>
    /// Captures everything <see cref="DispatchEnqueue"/> needs from <see cref="Next"/>'s
    /// validation phase. Passed verbatim through the mainloop work-item queue.
    /// </summary>
    private sealed record EnqueueDispatch(
        Player Player,
        PlayerKey Key,
        string ResolvedName,
        GroupId? GroupId,
        IReadOnlyList<PlayerKey>? PartyMembers);

    private void ReplyForEnqueue(
        Player player,
        PlayerKey key,
        string queueName,
        EnqueueResult result,
        IReadOnlyList<PlayerKey>? partyMembers = null)
    {
        // queueName comes through qualified ("lobby/3v3comp"); the registry's Label is the
        // operator-chosen pretty string (falls back to BaseName when no Label is set).
        string display = _engine.Queues.TryGet(queueName, out var def) ? def.Label : queueName;
        var msg = result switch
        {
            EnqueueResult.Ok => null,   // OnQueueAdded telemetry already replied
            EnqueueResult.UnknownQueue => $"Queue '{display}' not found.",
            EnqueueResult.NotConnected => "You aren't connected.",
            EnqueueResult.InMatch => "You're already in a match.",
            EnqueueResult.InTimeout => RenderInTimeoutMessage(key, partyMembers),
            EnqueueResult.AlreadyQueued => $"You're already in '{display}'.",
            EnqueueResult.AlreadyQueuedRefreshed => $"You're already in '{display}' -- refreshed your spot, so you won't be removed for inactivity.",
            EnqueueResult.GroupTooLarge => $"Your group is too large for '{display}'.",
            _ => null,
        };
        if (msg is not null) _chat.SendMessage(player, msg);
    }


    /// <summary>
    /// Builds the in-timeout reply. For solo enqueues (<paramref name="partyMembers"/> null) the
    /// caller is the offender and the reply renders their own remaining timeout. For party
    /// enqueues, scans the full membership and lists every player still in timeout (with each
    /// one's remaining duration), since the blocker is often a teammate the caller didn't know
    /// about.
    /// </summary>
    private string RenderInTimeoutMessage(PlayerKey caller, IReadOnlyList<PlayerKey>? partyMembers)
    {
        if (partyMembers is null)
            return RenderSelfTimeout(caller);

        var now = _clock.UtcNow;
        var offenders = new List<(PlayerKey Player, TimeSpan? Remaining)>();
        foreach (var m in partyMembers)
        {
            var elig = _engine.CheckEligibility(m);
            if (elig.Status != Core.Eligibility.EligibilityStatus.InTimeout) continue;
            TimeSpan? remaining = null;
            if (elig.TimeoutUntil is { } until)
            {
                var r = until - now;
                if (r > TimeSpan.Zero) remaining = r;
            }
            offenders.Add((m, remaining));
        }

        if (offenders.Count == 0)
            return "Your party can't queue: a member is serving a queue-timeout penalty.";

        if (offenders.Count == 1 && offenders[0].Player.Equals(caller))
            return RenderSelfTimeout(caller);

        var parts = new List<string>(offenders.Count);
        foreach (var (p, remaining) in offenders)
        {
            parts.Add(remaining is { } r
                ? $"{p.Name} ({HumanDuration.Humanize(r)} remaining)"
                : p.Name);
        }
        return $"Your party can't queue -- on timeout: {string.Join(", ", parts)}.";
    }

    private string RenderSelfTimeout(PlayerKey caller)
    {
        var elig = _engine.CheckEligibility(caller);
        if (elig.Status == Core.Eligibility.EligibilityStatus.InTimeout && elig.TimeoutUntil is { } until)
        {
            var remaining = until - _clock.UtcNow;
            if (remaining > TimeSpan.Zero)
                return $"You're serving a queue-timeout penalty -- {HumanDuration.Humanize(remaining)} remaining.";
        }
        return "You're serving a queue-timeout penalty.";
    }

    // ---- ?queue [name]

    private void Queue(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("queue", player, parameters);
        var arena = player.Arena;
        if (arena is null)
        {
            _chat.SendMessage(player, "Join an arena to see its queues.");
            return;
        }

        var requestedBaseName = QueueRegistry.JoinTokensToQueueName(parameters);
        if (requestedBaseName.Length == 0)
        {
            ListAllQueues(player, arena);
            return;
        }

        if (!_engine.Queues.TryGet(QueueRegistry.QualifyName(arena.BaseName, requestedBaseName), out var def))
        {
            _chat.SendMessage(player, $"Queue '{requestedBaseName}' is not defined for this arena. Type ?queue to see what's available here.");
            return;
        }

        ShowQueueDetail(player, def, _clock.UtcNow);
    }

    private void ListAllQueues(Player player, Arena arena)
    {
        var defs = new List<QueueDefinition>();
        foreach (var d in _engine.Queues.Definitions)
        {
            if (string.Equals(d.OwnerArenaName, arena.BaseName, StringComparison.OrdinalIgnoreCase))
                defs.Add(d);
        }
        defs.Sort((a, b) =>
            string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        if (defs.Count == 0)
        {
            _chat.SendMessage(player, "No queues are configured for this arena.");
            return;
        }

        _chat.SendMessage(player, $"Queues ({defs.Count}):");
        foreach (var d in defs)
        {
            var shape = d.Shape;
            // Spell the shape out so the "v" in queue labels (versus) is never confused with the
            // team/per-team split. TeamCount is always >= 2; PlayersPerTeam can be 1 (e.g. 1v1 ->
            // "2 teams of 1 player").
            var shapeTag = $"{shape.TeamCount} teams of {shape.PlayersPerTeam} " +
                (shape.PlayersPerTeam == 1 ? "player" : "players");
            int waiting = d.Queue.Snapshot().Count;
            _chat.SendMessage(player, $"  {d.Label} [{shapeTag}]: {waiting} waiting");
        }
        _chat.SendMessage(player, "Use ?queue <name> to see who is waiting; ?play <name> to join.");
    }

    private void ShowQueueDetail(Player player, QueueDefinition def, DateTimeOffset now)
    {
        var snap = def.Queue.Snapshot();
        if (snap.Count == 0)
        {
            _chat.SendMessage(player, $"{def.Label}: empty.");
            return;
        }
        _chat.SendMessage(player, $"{def.Label}: {snap.Count} player(s) waiting.");
        for (int i = 0; i < snap.Count; i++)
        {
            var entry = snap[i];
            var wait = now - entry.EnqueuedAt;
            _chat.SendMessage(player, $"  {entry.Player.Name} ({Format(wait)})");
        }
    }

    // ---- ?rating

    private void Rating(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("rating", player, parameters);
        if (_resolver.KeyOf(player) is not PlayerKey k) return;

        bool any = false;
        foreach (var entry in _engine.Ratings.Snapshot())
        {
            if (entry.Player != k) continue;
            any = true;
            _chat.SendMessage(player,
                $"GameType {entry.GameType}: rating={(int)Math.Round(entry.Value.Ordinal * 10.0, MidpointRounding.AwayFromZero)} " +
                $"({entry.Value.GamesPlayed} games)");
        }
        if (!any) _chat.SendMessage(player, "You haven't completed any rated matches yet.");
    }

    // ---- ?cancel

    private void Cancel(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("cancel", player, parameters);
        if (_resolver.KeyOf(player) is not PlayerKey k) return;

        var now = _clock.UtcNow;

        // Mirror ?play's group-vs-solo dispatch: ?play routes a grouped caller through
        // TryEnqueueGroup (atomic), so ?cancel must dequeue every party member to keep the
        // queue entry consistent. The matcher tracks per-player entries and won't cascade.
        if (_engine.Groups.GroupOf(k) is GroupId g)
        {
            var members = new List<PlayerKey>(_engine.Groups.MembersOf(g));
            int totalRemoved = 0;
            foreach (var m in members)
            {
                var removedFor = _engine.DequeueEverywhere(m, now);
                totalRemoved += removedFor.Count;
                if (_log.IsDebug)
                    _log.Debug(LogCategory, $"?cancel(party) {m.Name} removed from {removedFor.Count} queue(s): [{string.Join(",", removedFor)}]");
            }

            if (totalRemoved == 0)
            {
                _chat.SendMessage(player, "Your party isn't in any queue.");
                return;
            }

            // Notify the rest of the party. Caller stays silent on success, matching the
            // solo path's existing convention.
            foreach (var m in members)
            {
                if (m.Equals(k)) continue;
                if (_resolver.Resolve(m) is { } pp)
                    _chat.SendMessage(pp, $"{k.Name} cancelled the party's queue.");
            }
            return;
        }

        var removed = _engine.DequeueEverywhere(k, now);
        if (_log.IsDebug)
            _log.Debug(LogCategory, $"?cancel {k.Name} removed from {removed.Count} queue(s): [{string.Join(",", removed)}]");
        if (removed.Count == 0) _chat.SendMessage(player, "You weren't in any queue.");
    }

    // ---- ?connect discord <alias>

    /// <summary>
    /// Relays a Discord-account-link request to the external event-stream consumer. ClashEngine is
    /// identity-agnostic: it neither stores nor validates the alias -- it just emits a
    /// <c>player.discord_link_requested</c> event (via the engine telemetry) so the Discord bot
    /// service can perform the link and opt-in. The success ack is sent by
    /// <see cref="EngineEventListener.OnDiscordLinkRequested"/>; this handler only replies on
    /// usage errors.
    /// </summary>
    private void Connect(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("connect", player, parameters);
        if (_resolver.KeyOf(player) is not PlayerKey k) return;

        const string Usage = "Usage: ?connect discord <your Discord alias>";

        var rest = parameters.Trim();
        // First token selects the service ("discord"); the remainder is the alias, which may
        // contain spaces (Discord display names do).
        int sp = rest.IndexOf(' ');
        ReadOnlySpan<char> service = sp < 0 ? rest : rest[..sp];
        ReadOnlySpan<char> alias = sp < 0 ? ReadOnlySpan<char>.Empty : rest[(sp + 1)..].Trim();

        if (!service.Equals("discord", StringComparison.OrdinalIgnoreCase) || alias.IsEmpty)
        {
            _chat.SendMessage(player, Usage);
            return;
        }

        const int MaxAliasLength = 64;
        if (alias.Length > MaxAliasLength)
        {
            _chat.SendMessage(player, $"That Discord alias is too long (max {MaxAliasLength} characters).");
            return;
        }

        if (!_engine.RequestDiscordLink(k, alias.ToString(), _clock.UtcNow))
            _chat.SendMessage(player, Usage);
    }

    // ---- ?return

    private void Return(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("return", player, parameters);
        if (_resolver.KeyOf(player) is not PlayerKey k) return;

        var orchestrator = _orchestrators.OrchestratorFor(k);
        if (orchestrator is null)
        {
            _chat.SendMessage(player, "You aren't in an active match.");
            return;
        }

        var result = orchestrator.TryReturn(k);
        if (_log.IsDebug)
            _log.Debug(LogCategory, $"?return {k.Name} match={orchestrator.MatchId:N} result={result}");

        var msg = result switch
        {
            MatchOrchestrator.ReturnResult.Placed => null,
            MatchOrchestrator.ReturnResult.AlreadyActive => "You're already on a ship.",
            MatchOrchestrator.ReturnResult.NotInArena => "Return to your match arena first, then ?return.",
            MatchOrchestrator.ReturnResult.KnockedOut => "You're out of lives -- can't rejoin this match.",
            MatchOrchestrator.ReturnResult.MatchEnded => "That match has ended.",
            MatchOrchestrator.ReturnResult.UnresolvedPlayer => null,
            _ => null,
        };
        if (msg is not null) _chat.SendMessage(player, msg);
    }

    // ---- ?party player1,player2,...

    private void Group(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("party", player, parameters);
        if (_resolver.KeyOf(player) is not PlayerKey k) return;

        var arg = parameters.Trim().ToString();
        if (arg.Length == 0)
        {
            ShowPartyMembers(player, k);
            return;
        }

        var now = _clock.UtcNow;
        foreach (var raw in arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Resolve before inviting so a typo or offline name produces explicit feedback
            // instead of a ghost ledger entry. ResolveFuzzy tries exact, then unique prefix,
            // then unique Damerau-Levenshtein within the inviter's arena -- ambiguous matches
            // are rejected so we don't quietly invite the wrong person.
            var fuzzy = _resolver.ResolveFuzzy(raw, player.Arena);
            if (fuzzy.Player is null || _resolver.KeyOf(fuzzy.Player) is not PlayerKey inviteeKey)
            {
                _chat.SendMessage(player, fuzzy.Match.Kind == NameMatchKind.Ambiguous
                    ? $"'{raw}' is ambiguous -- matches {FormatCandidates(fuzzy.Match.Candidates)}. Be more specific."
                    : $"No player named '{raw}' is online.");
                continue;
            }
            var displayName = inviteeKey.Name;
            if (_log.IsDebug && fuzzy.Match.Kind != NameMatchKind.Exact)
                _log.Debug(LogCategory, $"?party {k.Name} resolved \"{raw}\" -> {displayName} via {fuzzy.Match.Kind}");

            var result = _engine.InviteToGroup(k, inviteeKey, now);
            if (_log.IsDebug)
                _log.Debug(LogCategory, $"?party {k.Name} -> {displayName} result={result}");
            var msg = result switch
            {
                InviteResult.Sent => $"Invitation sent to {displayName}.",
                InviteResult.SelfInvite => "You can't invite yourself.",
                InviteResult.AlreadyInGroup => $"{displayName} is already in your group.",
                InviteResult.InviteeBusy => $"{displayName} is already in another group.",
                InviteResult.AlreadyInvited => $"{displayName} already has a pending invitation from you.",
                InviteResult.NotLeader => "Only the party leader can invite (party is closed).",
                _ => null,
            };
            if (msg is not null) _chat.SendMessage(player, msg);
        }
    }

    /// <summary>Sends the caller a snapshot of their current party: mode, member count, and the
    /// member names. In closed parties the leader is annotated with "(leader)" so members know
    /// who controls invites.</summary>
    private void ShowPartyMembers(Player player, PlayerKey self)
    {
        if (_engine.Groups.GroupOf(self) is not GroupId group)
        {
            _chat.SendMessage(player, "You're not in a party. Use ?party <player> to invite someone.");
            return;
        }

        var members = _engine.Groups.MembersOf(group);
        var mode = _engine.Groups.ModeOf(group);
        var leader = mode == GroupMode.Closed ? _engine.Groups.LeaderOf(group) : null;

        _chat.SendMessage(player,
            $"Party ({mode.ToString().ToLowerInvariant()}, {members.Count} member{(members.Count == 1 ? "" : "s")}):");
        foreach (var m in members.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            string suffix = leader is { } l && l.Equals(m) ? " (leader)" : "";
            _chat.SendMessage(player, $"  {m.Name}{suffix}");
        }
    }

    // ---- ?accept [inviter]

    private void Accept(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("accept", player, parameters);
        if (_resolver.KeyOf(player) is not PlayerKey k) return;

        var arg = parameters.Trim().ToString();
        PlayerKey? inviter = arg.Length > 0 ? new PlayerKey(arg) : null;

        var result = _engine.AcceptInvite(k, inviter, _clock.UtcNow, out var groupId);
        if (_log.IsDebug)
            _log.Debug(LogCategory, $"?accept {k.Name} (inviter={inviter?.Name ?? "*"}) result={result} group={groupId}");
        var msg = result switch
        {
            AcceptResult.Joined => $"You joined the group ({_engine.Groups.MembersOf(groupId).Count} members).",
            AcceptResult.NoSuchInvite => $"No invitation from {arg}.",
            AcceptResult.NoPendingInvite => "You have no pending invitations.",
            AcceptResult.AmbiguousMustSpecify => "Multiple pending invitations. Use ?accept <inviter>.",
            AcceptResult.InviteExpired => "That invitation has expired.",
            AcceptResult.AlreadyInGroup => "You're already in a group. Use ?cancel and leave first.",
            _ => null,
        };
        if (msg is not null) _chat.SendMessage(player, msg);
    }

    // ---- ?decline [inviter]

    private void Decline(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("decline", player, parameters);
        if (_resolver.KeyOf(player) is not PlayerKey k) return;

        var arg = parameters.Trim().ToString();
        PlayerKey? inviter = arg.Length > 0 ? new PlayerKey(arg) : null;

        var result = _engine.DeclineInvite(k, inviter, _clock.UtcNow);
        if (_log.IsDebug)
            _log.Debug(LogCategory, $"?decline {k.Name} (inviter={inviter?.Name ?? "*"}) result={result}");
        var msg = result switch
        {
            DeclineResult.Declined => "Invitation declined.",
            DeclineResult.NoSuchInvite => $"No invitation from {arg}.",
            DeclineResult.NoPendingInvite => "You have no pending invitations.",
            DeclineResult.AmbiguousMustSpecify => "Multiple pending invitations. Use ?decline <inviter>.",
            _ => null,
        };
        if (msg is not null) _chat.SendMessage(player, msg);
    }

    // ---- ?leaveparty

    private void LeaveParty(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("leaveparty", player, parameters);
        if (_resolver.KeyOf(player) is not PlayerKey k) return;

        // Read mode + leadership BEFORE Leave(), since Leave clears those fields. We need them
        // to phrase the reply ("you disbanded the party") and to chat-notify surviving members
        // for the non-leader-leave case (telemetry only fires on disbandments).
        var groupId = _engine.Groups.GroupOf(k);
        bool wasLeader = groupId is GroupId g0 && _engine.Groups.IsLeader(k, g0);
        bool wasClosed = groupId is GroupId g1 && _engine.Groups.ModeOf(g1) == GroupMode.Closed;
        var preMembers = groupId is GroupId g2
            ? new List<PlayerKey>(_engine.Groups.MembersOf(g2))
            : new List<PlayerKey>();

        bool changed = _engine.LeaveGroup(k, _clock.UtcNow);
        if (!changed)
        {
            _chat.SendMessage(player, "You're not in a party.");
            return;
        }

        if (wasClosed && wasLeader)
        {
            // Disband telemetry fired the disband notice to peers. Inform the leader only.
            _chat.SendMessage(player, "You disbanded the party (you were the leader).");
        }
        else
        {
            _chat.SendMessage(player, "You left the party.");
            // Telemetry only fires for full disbandments; for a non-leader leave on a surviving
            // (or open) group, we notify the still-grouped peers directly here.
            foreach (var peer in preMembers)
            {
                if (peer.Equals(k)) continue;
                if (_engine.Groups.GroupOf(peer) is not null   // still in a group
                    && _resolver.Resolve(peer) is { } pp)
                {
                    _chat.SendMessage(pp, $"{k.Name} left the party.");
                }
            }
        }
    }

    // ---- ?partymode [open|closed]

    private void PartyMode(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("partymode", player, parameters);
        if (_resolver.KeyOf(player) is not PlayerKey k) return;

        var arg = parameters.Trim().ToString().ToLowerInvariant();

        if (arg.Length == 0)
        {
            // No-arg form: report the player's current party mode (if any).
            if (_engine.Groups.GroupOf(k) is GroupId g)
            {
                _chat.SendMessage(player, $"Your party is {_engine.Groups.ModeOf(g).ToString().ToLowerInvariant()}.");
            }
            else
            {
                _chat.SendMessage(player, "You're not in a party.");
            }
            return;
        }

        GroupMode? requested = arg switch
        {
            "open" => GroupMode.Open,
            "closed" => GroupMode.Closed,
            _ => null,
        };
        if (requested is not GroupMode mode)
        {
            _chat.SendMessage(player, "Usage: ?partymode [open|closed]");
            return;
        }

        var result = _engine.SetGroupMode(k, mode, _clock.UtcNow);
        if (_log.IsDebug)
            _log.Debug(LogCategory, $"?partymode {k.Name} -> {mode} result={result}");

        switch (result)
        {
            case SetModeResult.NotInGroup:
                _chat.SendMessage(player, "You're not in a party.");
                return;
            case SetModeResult.NotLeader:
                _chat.SendMessage(player, "Only the party leader can change a closed party's mode.");
                return;
            case SetModeResult.Unchanged:
                _chat.SendMessage(player, $"Party is already {mode.ToString().ToLowerInvariant()}.");
                return;
            case SetModeResult.Changed:
                BroadcastModeChange(k, mode);
                return;
        }
    }

    /// <summary>Tells every member of <paramref name="caller"/>'s party that the mode flipped.
    /// In closed mode the caller is the new leader (set by <see cref="GroupRegistry.SetMode"/>).</summary>
    private void BroadcastModeChange(PlayerKey caller, GroupMode newMode)
    {
        if (_engine.Groups.GroupOf(caller) is not GroupId g) return;
        string suffix = newMode == GroupMode.Closed ? $", led by {caller.Name}" : "";
        string msg = $"Party is now {newMode.ToString().ToLowerInvariant()}{suffix}.";
        foreach (var member in _engine.Groups.MembersOf(g))
        {
            if (_resolver.Resolve(member) is { } p)
                _chat.SendMessage(p, msg);
        }
    }

    // ---- ?autoqueue [on|off]

    private void AutoQueue(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("autoqueue", player, parameters);
        if (_resolver.KeyOf(player) is not PlayerKey k) return;

        var arg = parameters.Trim().ToString().ToLowerInvariant();

        // No arg => query and report the current (persisted) state. on/off => set, then report.
        if (arg.Length != 0)
        {
            bool? requested = arg switch
            {
                "on" or "1" or "yes" or "enable" => true,
                "off" or "0" or "no" or "disable" => false,
                _ => null,
            };
            if (requested is not bool enable)
            {
                _chat.SendMessage(player, "Usage: ?autoqueue [on|off]");
                return;
            }
            _engine.AutoQueue.Set(k, enable);
            if (_log.IsDebug)
                _log.Debug(LogCategory, $"?autoqueue {k.Name} -> {(enable ? "on" : "off")}");
        }

        // Always report the resulting state. The "is now ON" / "is now OFF" wording is the contract
        // the bot harness (ClashRig RegressionZone) keys on -- keep it intact when editing.
        _chat.SendMessage(player, _engine.AutoQueue.IsEnabled(k)
            ? "Auto-queue is now ON -- after each match you'll be put back into its queue " +
              "automatically. Use ?autoqueue off to stop, or ?cancel to leave a queue."
            : "Auto-queue is now OFF -- you won't be put back into the queue after matches.");
    }

    // ---- ?forgive <player>

    private void Veto(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("forgive", player, parameters);
        if (_resolver.KeyOf(player) is not PlayerKey voter) return;

        var arg = parameters.Trim().ToString();
        if (arg.Length == 0)
        {
            _chat.SendMessage(player, "Usage: ?forgive <player>");
            return;
        }
        var targetKey = new PlayerKey(arg);

        Guid matchId = Guid.Empty;
        foreach (var kvp in _engine.PendingGriefingPenalties)
        {
            if (kvp.Key.Target == targetKey && kvp.Value.EligibleVoters.Contains(voter))
            {
                matchId = kvp.Key.MatchId;
                break;
            }
        }
        if (matchId == Guid.Empty)
        {
            _chat.SendMessage(player, $"No pending griefing penalty for {arg} that you can forgive.");
            return;
        }

        var result = _engine.Veto(matchId, targetKey, voter, _clock.UtcNow);
        if (_log.IsDebug)
            _log.Debug(LogCategory, $"?forgive {voter.Name} -> {targetKey.Name} match={matchId:N} result={result}");
        var msg = result switch
        {
            VetoResult.RecordedNeedMore => null,   // listener already messaged
            VetoResult.PenaltyRescinded => null,   // listener already messaged
            VetoResult.AlreadyVoted => "You already voted.",
            VetoResult.NotEligible => "You're not eligible to forgive that penalty.",
            VetoResult.NoPendingPenalty => "No pending penalty.",
            VetoResult.WindowExpired => "The forgiveness window has closed.",
            _ => null,
        };
        if (msg is not null) _chat.SendMessage(player, msg);
    }

    // ---- ?clashreset <player> [--keep-rating]

    /// <summary>
    /// Operator-only reset. Parses a required player name and an optional --keep-rating flag,
    /// hands them to <see cref="MatchmakingEngine.ResetPlayer"/>, then renders a single-line
    /// summary of what was actually cleared. Always Info-logs the action with the operator's
    /// name -- this is moderation, useful to audit even outside debug verbosity.
    /// </summary>
    private void ClashReset(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("clashreset", player, parameters);

        var args = parameters.Trim();
        if (args.IsEmpty)
        {
            _chat.SendMessage(player, "Usage: ?clashreset <player> [--keep-rating]");
            return;
        }

        // Tokenize on whitespace. The flag may come before or after the player name; everything
        // else is the player name (only one positional token allowed).
        bool keepRating = false;
        string? targetName = null;
        foreach (var raw in args.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(raw, "--keep-rating", StringComparison.OrdinalIgnoreCase))
            {
                keepRating = true;
            }
            else if (raw.StartsWith("--", StringComparison.Ordinal))
            {
                _chat.SendMessage(player, $"Unknown flag '{raw}'. Supported: --keep-rating.");
                return;
            }
            else if (targetName is null)
            {
                targetName = raw;
            }
            else
            {
                _chat.SendMessage(player, "Usage: ?clashreset <player> [--keep-rating]");
                return;
            }
        }

        if (targetName is null)
        {
            _chat.SendMessage(player, "Usage: ?clashreset <player> [--keep-rating]");
            return;
        }

        var targetKey = new PlayerKey(targetName);
        var summary = _engine.ResetPlayer(targetKey, _clock.UtcNow, keepRating);

        // Audit log -- always emitted (not gated on _log.IsDebug) so operators have a paper
        // trail even with verbosity at Normal. Mirrors ?clashlog's "always log this" pattern.
        _log.Info(LogCategory,
            $"?clashreset by {player.Name ?? "(no-name)"}: target={targetKey.Name} keepRating={keepRating} " +
            $"queues={summary.RemovedFromQueues} group={summary.LeftGroup} match={summary.RemovedFromMatch} " +
            $"penalties={summary.PenaltyEventsCleared} ratings={summary.RatingsCleared} " +
            $"pendingGriefs={summary.PendingGriefsCleared}");

        var parts = new List<string>();
        if (summary.PenaltyEventsCleared > 0) parts.Add($"cleared {summary.PenaltyEventsCleared} penalty event(s)");
        if (summary.RatingsCleared > 0) parts.Add($"cleared {summary.RatingsCleared} rating row(s)");
        if (summary.RemovedFromQueues > 0) parts.Add($"removed from {summary.RemovedFromQueues} queue(s)");
        if (summary.LeftGroup) parts.Add("left party");
        if (summary.RemovedFromMatch) parts.Add("evicted from active match");
        if (summary.PendingGriefsCleared > 0) parts.Add($"dropped {summary.PendingGriefsCleared} pending griefing penalty(ies)");

        string suffix = keepRating ? " (rating preserved)" : "";
        if (parts.Count == 0)
            _chat.SendMessage(player, $"?clashreset {targetKey.Name}: no persistent data found{suffix}.");
        else
            _chat.SendMessage(player, $"?clashreset {targetKey.Name}: {string.Join(", ", parts)}{suffix}.");
    }

    // ---- ?clashlog [off|normal|verbose|trace]

    private void ClashLogCmd(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        var arg = parameters.Trim();
        if (arg.IsEmpty)
        {
            _chat.SendMessage(player, $"ClashEngine verbosity = {_log.Level}.");
            return;
        }
        var raw = arg.ToString();
        var parsed = ClashLog.ParseVerbosity(raw, fallback: _log.Level);
        // ParseVerbosity returns the fallback for unknown values; detect by re-checking that the
        // user actually meant something parseable (so a typo doesn't silently keep the old level).
        if (parsed == _log.Level && !IsKnownLevel(raw))
        {
            _chat.SendMessage(player, $"Unknown verbosity '{raw}'. Use off | normal | verbose | trace.");
            return;
        }
        var prev = _log.Level;
        _log.Level = parsed;
        _chat.SendMessage(player, $"ClashEngine verbosity {prev} -> {parsed}.");
        // Always log this -- it's an admin operation, useful even at Off.
        _log.Info(LogCategory, $"Verbosity changed by {player.Name ?? "(no-name)"}: {prev} -> {parsed}");
    }

    private static bool IsKnownLevel(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        return s is "off" or "0" or "none"
            or "normal" or "1" or "info"
            or "verbose" or "2" or "debug"
            or "trace" or "3" or "all";
    }

    // ---- ?helpclash

    /// <summary>
    /// Player-facing reference for ClashEngine commands. Each row is a single chat line so the
    /// monospace SS chat font keeps the columns aligned. Deliberately omits ?clashlog -- that's
    /// an operator command, not for end users.
    /// </summary>
    private void HelpClash(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        // (command, description). Keep the longest "command" string under ~36 chars; the SS
        // chat line wraps around column 80 and we want the description visible.
        var rows = new (string Cmd, string Desc)[]
        {
            ("?play <queue name>",           "Queue for the next match. Spaces map to underscores in the lookup."),
            ("?cancel",                      "Leave every ClashEngine queue."),
            ("?return",                      "Rejoin the match you were specced from."),
            ("?queue [name]",                "List all queues (no arg) or show who is waiting in <name>."),
            ("?rating",                      "Show your skill rating per game type."),
            ("?connect discord <alias>",     "Relay a request to link your name to a Discord alias (the bot confirms)."),
            ("?autoqueue [on|off]",          "Auto re-queue into a match's queue when it ends (persists; off if flagged AFK)."),
            ("?chart",                       "Show the live scoreboard for your match (or one you're spectating)."),
            ("?party",                       "List your current party's members (leader marked if closed)."),
            ("?party <p1>[,<p2>,...]",       "Invite one or more players to your party."),
            ("?accept [inviter]",            "Accept a pending party invitation."),
            ("?decline [inviter]",           "Decline a pending party invitation."),
            ("?leaveparty",                  "Leave your party (leader of a closed party = disband)."),
            ("?partymode [open|closed]",     "View or change your party's invite-control mode."),
            ("?forgive <player>",            "Vote to overturn a pending griefing penalty."),
        };

        // Pad the command column to the longest row's length so every description starts at the
        // same x in the chat client.
        int maxCmd = 0;
        for (int i = 0; i < rows.Length; i++)
            if (rows[i].Cmd.Length > maxCmd) maxCmd = rows[i].Cmd.Length;

        _chat.SendMessage(player, "ClashEngine commands:");
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            _chat.SendMessage(player, $"  {row.Cmd.PadRight(maxCmd)}   {row.Desc}");
        }
    }

    private static string Format(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{(int)ts.TotalSeconds}s";
        return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
    }

    /// <summary>Renders an ambiguity message's candidate list, capping at <paramref name="max"/>
    /// so a wide prefix collision (e.g. "?party C" in a busy arena) doesn't blow past the chat
    /// line length and get truncated by the client.</summary>
    private static string FormatCandidates(IReadOnlyList<string> names, int max = 5)
    {
        if (names.Count <= max) return string.Join(", ", names);
        var head = new string[max];
        for (int i = 0; i < max; i++) head[i] = names[i];
        return $"{string.Join(", ", head)}, ... (+{names.Count - max} more)";
    }
}
