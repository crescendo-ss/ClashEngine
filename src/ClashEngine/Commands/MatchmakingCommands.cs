using System;
using System.Collections.Generic;
using System.Linq;
using ClashEngine.Adapter;
using ClashEngine.Config;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Eligibility;
using ClashEngine.Core.Groups;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Queue;
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

    private static readonly string[] Tiers = { "competitive", "casual" };

    public MatchmakingCommands(
        MatchmakingEngine engine,
        ICommandManager commands,
        IChat chat,
        IClock clock,
        PlayerKeyResolver resolver,
        IConfigManager config,
        ClashLog log)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void Register()
    {
        // Naming notes:
        //   ?play  -- entry-point queue command; renamed from ?next to avoid the global
        //             collision with MatchmakingQueues.next.
        //   ?party -- group invite command; renamed from ?group to avoid the global
        //             collision with PlayerGroups.group.
        //   ?accept and ?cancel -- short bare names retained. CaptainsMatch also registers
        //             these per-arena, but ClashEngine matches run in their own configured
        //             [ClashEngine] MatchArena, so the collision is harmless in practice.
        _commands.AddCommand("play", Next, helpText:
            "?play [comp|casual] <queue> -- Queue for the next ClashEngine match. \"comp\" is the default tier.");
        _commands.AddCommand("queue", Queue, helpText:
            "?queue [name] -- List all queues, or inspect a single queue's waiting players. " +
            "If <name> has no exact match, both <name>_competitive and <name>_casual are tried.");
        _commands.AddCommand("rating", Rating, helpText:
            "?rating -- Show your skill rating per game type.");
        _commands.AddCommand("cancel", Cancel, helpText:
            "?cancel -- Leave every ClashEngine matchmaking queue.");
        _commands.AddCommand("party", Group, helpText:
            "?party -- List the members of your current party. " +
            "?party player1[,player2,...] -- Invite one or more players to your ClashEngine group.");
        _commands.AddCommand("accept", Accept, helpText:
            "?accept [inviter] -- Accept a pending ClashEngine group invitation. Inviter is optional when only one is pending.");
        _commands.AddCommand("decline", Decline, helpText:
            "?decline [inviter] -- Decline a pending ClashEngine invitation.");
        _commands.AddCommand("leaveparty", LeaveParty, helpText:
            "?leaveparty -- Leave your current party. If you're the leader of a closed party, the party disbands.");
        _commands.AddCommand("partymode", PartyMode, helpText:
            "?partymode [open|closed] -- View or change your party's mode. Closed parties have a leader who controls invites.");
        _commands.AddCommand("veto", Veto, helpText:
            "?veto <player> -- Vote to overturn a pending griefing penalty for a match participant.");
        _commands.AddCommand("clashlog", ClashLogCmd, helpText:
            "?clashlog [off|normal|verbose|trace] -- Show or set ClashEngine debug verbosity. " +
            "Affects only ClashEngine's verbose/trace logging; the host's global level still gates Info/Warn/Error.");
        _commands.AddCommand("helpclash", HelpClash, helpText:
            "?helpclash -- List ClashEngine player commands and what they do.");
    }

    public void Unregister()
    {
        _commands.RemoveCommand("play", Next);
        _commands.RemoveCommand("queue", Queue);
        _commands.RemoveCommand("rating", Rating);
        _commands.RemoveCommand("cancel", Cancel);
        _commands.RemoveCommand("party", Group);
        _commands.RemoveCommand("accept", Accept);
        _commands.RemoveCommand("decline", Decline);
        _commands.RemoveCommand("leaveparty", LeaveParty);
        _commands.RemoveCommand("partymode", PartyMode);
        _commands.RemoveCommand("veto", Veto);
        _commands.RemoveCommand("clashlog", ClashLogCmd);
        _commands.RemoveCommand("helpclash", HelpClash);
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

    // ---- ?play [comp|casual] <queue_type>

    private void Next(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("play", player, parameters);
        var key = _resolver.KeyOf(player);
        if (key is not PlayerKey k) return;

        // Parse: optional first token is tier; required (last) token is queue type.
        string tier = "competitive";
        string queueType;

        var args = parameters.Trim();
        int spaceIdx = args.IndexOf(' ');
        if (spaceIdx >= 0)
        {
            var first = args[..spaceIdx].ToString();
            if (string.Equals(first, "comp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(first, "competitive", StringComparison.OrdinalIgnoreCase))
            {
                tier = "competitive";
            }
            else if (string.Equals(first, "casual", StringComparison.OrdinalIgnoreCase))
            {
                tier = "casual";
            }
            else
            {
                _chat.SendMessage(player, $"Unknown tier '{first}'. Use 'comp' or 'casual'.");
                return;
            }
            queueType = args[(spaceIdx + 1)..].Trim().ToString();
        }
        else
        {
            queueType = args.ToString();
        }

        if (queueType.Length == 0)
        {
            // Fall back to the arena's [ClashEngine] DefaultQueue when no queue is supplied.
            var arena = player.Arena;
            var arenaConf = arena?.Cfg;
            var defaultQueue = arenaConf is not null
                ? MatchmakingConfig.DefaultQueueForArena(_config, arenaConf)
                : null;
            if (string.IsNullOrWhiteSpace(defaultQueue))
            {
                _chat.SendMessage(player, "Usage: ?play [comp|casual] <queue type>. Type ?queue to see available queues.");
                return;
            }
            queueType = defaultQueue.Trim();
        }

        // Resolution: try `{type}_{tier}` first, then `{type}`.
        var tieredName = $"{queueType}_{tier}";
        var resolvedName = _engine.Queues.Contains(tieredName) ? tieredName
                          : _engine.Queues.Contains(queueType) ? queueType
                          : null;
        if (resolvedName is null)
        {
            _chat.SendMessage(player, $"Queue '{queueType}' not found. Type ?queue to see available queues.");
            return;
        }

        var now = _clock.UtcNow;
        var groupId = _engine.Groups.GroupOf(k);
        EnqueueResult result;
        if (groupId is GroupId g)
        {
            var members = new System.Collections.Generic.List<PlayerKey>();
            foreach (var m in _engine.Groups.MembersOf(g)) members.Add(m);
            result = _engine.TryEnqueueGroup(members, resolvedName, now, out _, existingGroup: g);
        }
        else
        {
            result = _engine.TryEnqueue(k, resolvedName, now);
        }
        if (_log.IsDebug)
            _log.Debug(LogCategory, $"?play {k.Name} -> queue '{resolvedName}' result={result}" +
                (groupId is GroupId gg ? $" (group {gg})" : ""));
        ReplyForEnqueue(player, resolvedName, result);
    }

    private void ReplyForEnqueue(Player player, string queueName, EnqueueResult result)
    {
        var msg = result switch
        {
            EnqueueResult.Ok => null,   // OnQueueAdded telemetry already replied
            EnqueueResult.UnknownQueue => $"Queue '{queueName}' not found.",
            EnqueueResult.NotConnected => "You aren't connected.",
            EnqueueResult.InMatch => "You're already in a match.",
            EnqueueResult.InTimeout => "You're serving a queue-timeout penalty.",
            EnqueueResult.AlreadyQueued => $"You're already in '{queueName}'.",
            EnqueueResult.GroupTooLarge => $"Your group is too large for '{queueName}'.",
            _ => null,
        };
        if (msg is not null) _chat.SendMessage(player, msg);
    }

    // ---- ?queue [name]

    private void Queue(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("queue", player, parameters);
        var queueName = parameters.Trim().ToString();
        if (queueName.Length == 0)
        {
            ListAllQueues(player);
            return;
        }

        // Resolution mirrors ?play: try the literal name first; if that misses, try the
        // tier-suffixed pair so `?queue 1v1` finds 1v1_competitive + 1v1_casual.
        var matches = new List<QueueDefinition>();
        if (_engine.Queues.TryGet(queueName, out var literal))
        {
            matches.Add(literal);
        }
        else
        {
            if (_engine.Queues.TryGet($"{queueName}_competitive", out var comp)) matches.Add(comp);
            if (_engine.Queues.TryGet($"{queueName}_casual", out var cas)) matches.Add(cas);
        }

        if (matches.Count == 0)
        {
            _chat.SendMessage(player, $"Queue '{queueName}' not found. Type ?queue to see available queues.");
            return;
        }

        var now = _clock.UtcNow;
        for (int i = 0; i < matches.Count; i++) ShowQueueDetail(player, matches[i], now);
    }

    private void ListAllQueues(Player player)
    {
        var defs = _engine.Queues.Definitions
            .OrderBy(d => SplitTierSuffix(d.Name).Base, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (defs.Count == 0)
        {
            _chat.SendMessage(player, "No queues are configured.");
            return;
        }

        // Group by base name (with tier suffix stripped) so paired queues render as one row.
        var grouped = new Dictionary<string, List<QueueDefinition>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var d in defs)
        {
            var (baseName, _) = SplitTierSuffix(d.Name);
            if (!grouped.TryGetValue(baseName, out var bucket))
            {
                bucket = new List<QueueDefinition>();
                grouped[baseName] = bucket;
                order.Add(baseName);
            }
            bucket.Add(d);
        }

        _chat.SendMessage(player, $"Queues ({defs.Count}):");
        foreach (var baseName in order)
        {
            var bucket = grouped[baseName];
            var shape = bucket[0].Shape;
            var shapeTag = $"{shape.TeamCount}v{shape.PlayersPerTeam}";

            // Shows each tier (or "no tier") with its current waiting count.
            var parts = new List<string>(bucket.Count);
            foreach (var d in bucket)
            {
                var (_, suffix) = SplitTierSuffix(d.Name);
                var tierLabel = suffix ?? d.Tier.ToString().ToLowerInvariant();
                int waiting = d.Queue.Snapshot().Count;
                parts.Add($"{tierLabel} ({waiting} waiting)");
            }
            _chat.SendMessage(player, $"  {baseName} [{shapeTag}]: {string.Join(", ", parts)}");
        }
        _chat.SendMessage(player, "Use ?queue <name> to see who is waiting; ?play [comp|casual] <name> to join.");
    }

    private void ShowQueueDetail(Player player, QueueDefinition def, DateTimeOffset now)
    {
        // Render the suffixed name as "<base> (<tier>)" -- e.g. "4v4_competitive" -> "4v4 (competitive)" --
        // so a ?queue 4v4 lookup that resolves to two rows reads as the same base with parenthesized tiers.
        var (baseName, suffix) = SplitTierSuffix(def.Name);
        string display = suffix is null ? def.Name : $"{baseName} ({suffix})";

        var snap = def.Queue.Snapshot();
        if (snap.Count == 0)
        {
            _chat.SendMessage(player, $"{display}: empty.");
            return;
        }
        _chat.SendMessage(player, $"{display}: {snap.Count} player(s) waiting.");
        for (int i = 0; i < snap.Count; i++)
        {
            var entry = snap[i];
            var wait = now - entry.EnqueuedAt;
            _chat.SendMessage(player, $"  {entry.Player.Name} ({Format(wait)})");
        }
    }

    /// <summary>Splits a registered queue name into its base type and matchmaking tier suffix.
    /// Returns <c>(name, null)</c> when neither <c>_competitive</c> nor <c>_casual</c> is present.
    /// Used by the listing/lookup paths so paired queues render as one row keyed on the base.</summary>
    private static (string Base, string? Suffix) SplitTierSuffix(string name)
    {
        const string comp = "_competitive";
        const string casual = "_casual";
        if (name.EndsWith(comp, StringComparison.OrdinalIgnoreCase))
            return (name[..^comp.Length], "competitive");
        if (name.EndsWith(casual, StringComparison.OrdinalIgnoreCase))
            return (name[..^casual.Length], "casual");
        return (name, null);
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
                $"GameType {entry.GameType}: mu={entry.Value.Mu:F2} sigma={entry.Value.Sigma:F2} " +
                $"(rating={(int)Math.Round(entry.Value.Ordinal * 10.0, MidpointRounding.AwayFromZero)}, {entry.Value.GamesPlayed} games)");
        }
        if (!any) _chat.SendMessage(player, "You haven't completed any rated matches yet.");
    }

    // ---- ?cancel

    private void Cancel(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("cancel", player, parameters);
        if (_resolver.KeyOf(player) is not PlayerKey k) return;
        var removed = _engine.DequeueEverywhere(k, _clock.UtcNow);
        if (_log.IsDebug)
            _log.Debug(LogCategory, $"?cancel {k.Name} removed from {removed.Count} queue(s): [{string.Join(",", removed)}]");
        if (removed.Count == 0) _chat.SendMessage(player, "You weren't in any queue.");
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
            var inviteeKey = new PlayerKey(raw);
            var result = _engine.InviteToGroup(k, inviteeKey, now);
            if (_log.IsDebug)
                _log.Debug(LogCategory, $"?party {k.Name} -> {raw} result={result}");
            var msg = result switch
            {
                InviteResult.Sent => $"Invitation sent to {raw}.",
                InviteResult.SelfInvite => "You can't invite yourself.",
                InviteResult.AlreadyInGroup => $"{raw} is already in your group.",
                InviteResult.InviteeBusy => $"{raw} is already in another group.",
                InviteResult.AlreadyInvited => $"{raw} already has a pending invitation from you.",
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

    // ---- ?veto <player>

    private void Veto(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        LogCommand("veto", player, parameters);
        if (_resolver.KeyOf(player) is not PlayerKey voter) return;

        var arg = parameters.Trim().ToString();
        if (arg.Length == 0)
        {
            _chat.SendMessage(player, "Usage: ?veto <player>");
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
            _chat.SendMessage(player, $"No pending griefing penalty for {arg} that you can veto.");
            return;
        }

        var result = _engine.Veto(matchId, targetKey, voter, _clock.UtcNow);
        if (_log.IsDebug)
            _log.Debug(LogCategory, $"?veto {voter.Name} -> {targetKey.Name} match={matchId:N} result={result}");
        var msg = result switch
        {
            VetoResult.RecordedNeedMore => null,   // listener already messaged
            VetoResult.PenaltyRescinded => null,   // listener already messaged
            VetoResult.AlreadyVoted => "You already voted.",
            VetoResult.NotEligible => "You're not eligible to veto that penalty.",
            VetoResult.NoPendingPenalty => "No pending penalty.",
            VetoResult.WindowExpired => "The veto window has closed.",
            _ => null,
        };
        if (msg is not null) _chat.SendMessage(player, msg);
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
            ("?play [comp|casual] <queue>",  "Queue for the next match. \"comp\" is the default tier."),
            ("?cancel",                      "Leave every ClashEngine queue."),
            ("?queue [name]",                "List all queues (no arg) or show who is waiting in <name>."),
            ("?rating",                      "Show your skill rating per game type."),
            ("?party",                       "List your current party's members (leader marked if closed)."),
            ("?party <p1>[,<p2>,...]",       "Invite one or more players to your party."),
            ("?accept [inviter]",            "Accept a pending party invitation."),
            ("?decline [inviter]",           "Decline a pending party invitation."),
            ("?leaveparty",                  "Leave your party (leader of a closed party = disband)."),
            ("?partymode [open|closed]",     "View or change your party's invite-control mode."),
            ("?veto <player>",               "Vote to overturn a pending griefing penalty."),
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
}
