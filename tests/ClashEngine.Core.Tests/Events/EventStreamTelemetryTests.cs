using System.Linq;
using System.Text.Json;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Events;
using ClashEngine.Core.GameType;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests.Events;

public class EventStreamTelemetryTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Minimal game-type def whose Label feeds match.gameLabel; only Name/Label/shape matter here.
    private static GameTypeDef GtDef(string name, string label) =>
        new(
            Name: name,
            Label: label,
            Description: null,
            Metadata: GameTypeMetadata.Uniform(2, 2, lives: 0),
            TeamCount: 2,
            PlayersPerTeam: 2,
            KillTarget: 30,
            Lives: 0,
            TimeLimit: null,
            StartSetByTeam: null,
            MaxStartDriftTiles: null,
            UseStartLocation: false,
            SpawnByTeam: null,
            StagingDuration: null,
            CountdownDuration: null,
            KnockoutSpecDelay: null,
            TeamCollapseGrace: null,
            ShipChangeGracePeriod: null,
            ReturnItemsAction: ClashEngine.Core.Queue.ItemsAction.Full);

    // Drives a real engine whose telemetry IS the mapper, so the event stream is exercised through
    // the genuine queue/match pipeline (counts, reasons, matched dequeues, lifecycle).
    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public InMemoryRatingStore Ratings { get; } = new();
        public RecordingEventSink Sink { get; } = new();
        public MatchmakingEngine Engine { get; }
        public EventStreamTelemetry Mapper { get; }

        public Harness(int killTarget = 3, bool registerGameType = true)
        {
            Engine = new MatchmakingEngine(
                Ratings, Clock,
                new[] { PenaltyPolicy.DefaultAbandonment, PenaltyPolicy.DefaultGriefing });
            Engine.Queues.Register(
                "2v2",
                new MatchShape(2, 2),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt1",
                () => new KillCountEndPolicy(killTarget),
                label: "2v2 Casual");
            // The queue references game type "gt1"; registering its def (with a human Label) lets
            // the mapper resolve match.gameLabel. Skipped when registerGameType is false to exercise
            // the unregistered-fallback path.
            if (registerGameType)
                Engine.GameTypes.ReplaceArenaContribution("lobby", new[] { GtDef("gt1", "2v2 Showdown") }, out _);
            Mapper = new EventStreamTelemetry(Sink, Engine.Queues, Engine.GameTypes, Clock);
            Engine.SetTelemetry(Mapper);
        }

        public void Connect(params string[] names)
        {
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
        }

        public void Enqueue(params string[] names)
        {
            foreach (var n in names) Engine.TryEnqueue(K(n), "2v2", Clock.UtcNow);
        }

        public ActiveMatch StartProposedMatch()
        {
            var match = Engine.ActiveMatches.Values.Single();
            foreach (var team in match.Teams)
                foreach (var p in team)
                    Engine.OnPlayerJoinedArena(p, Clock.UtcNow);
            Engine.MarkMatchLive(match.MatchId, Clock.UtcNow);
            return match;
        }

        public EventEnvelope Single(string type) => Sink.Events.Single(e => e.Type == type);
        public int Count(string type) => Sink.Events.Count(e => e.Type == type);
    }

    [Fact]
    public void Enqueue_emits_queue_joined_with_count_and_shape()
    {
        var h = new Harness();
        h.Connect("A");
        h.Enqueue("A");

        var e = h.Single(ClashEventTypes.QueueJoined);
        Assert.Equal(EventSchema.Version, e.SchemaVersion);
        Assert.NotNull(e.Queue);
        Assert.Equal("2v2", e.Queue!.QueueName);
        Assert.Equal("2v2 Casual", e.Queue.QueueLabel);
        Assert.Equal("gt1", e.Queue.GameType);
        Assert.Equal("A", e.Queue.Player);
        Assert.Equal(1, e.Queue.Count);
        Assert.Equal(4, e.Queue.Capacity);
        Assert.Null(e.Queue.Reason);
        Assert.Null(e.Match);
        Assert.Null(e.Player);
    }

    [Fact]
    public void Cancel_emits_queue_left_reason_cancel_with_post_removal_count()
    {
        var h = new Harness();
        h.Connect("A");
        h.Enqueue("A");
        h.Engine.Dequeue(K("A"), "2v2", h.Clock.UtcNow);

        var e = h.Single(ClashEventTypes.QueueLeft);
        Assert.Equal("cancel", e.Queue!.Reason);
        Assert.Equal("A", e.Queue.Player);
        Assert.Equal(0, e.Queue.Count);
    }

    [Fact]
    public void Disconnect_while_queued_emits_queue_left_reason_disconnect()
    {
        var h = new Harness();
        h.Connect("A");
        h.Enqueue("A");
        h.Engine.OnPlayerDisconnected(K("A"), h.Clock.UtcNow);

        var e = h.Single(ClashEventTypes.QueueLeft);
        Assert.Equal("disconnect", e.Queue!.Reason);
    }

    [Fact]
    public void Match_formation_emits_matched_lefts_and_teams_locked()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        h.Enqueue("A", "B", "C", "D");
        h.Engine.Tick(T0);

        // One queue.left (reason=matched) per player on the forming queue, plus the lock.
        Assert.Equal(4, h.Sink.Events.Count(e =>
            e.Type == ClashEventTypes.QueueLeft && e.Queue!.Reason == "matched"));

        var locked = h.Single(ClashEventTypes.MatchTeamsLocked);
        Assert.Null(locked.Match!.MatchId);            // no id at proposal time
        Assert.Equal("gt1", locked.Match.GameType);
        Assert.Equal("2v2 Showdown", locked.Match.GameLabel);   // resolved from the game-type registry
        Assert.Equal("2v2", locked.Match.QueueName);
        Assert.Equal(2, locked.Match.Teams!.Count);
        Assert.All(locked.Match.Teams, t => Assert.Equal(2, t.Players.Count));
    }

    [Fact]
    public void Near_full_emits_waiting_roster()
    {
        var h = new Harness();
        h.Connect("A", "B", "C");
        h.Enqueue("A", "B", "C");
        h.Engine.Tick(T0);   // 3/4 -- near full, no match

        var e = h.Single(ClashEventTypes.QueueNearFull);
        Assert.Equal(3, e.Queue!.Count);
        Assert.Equal(4, e.Queue.Capacity);
        Assert.Equal(new[] { "A", "B", "C" }, e.Queue.Waiting);
        Assert.Null(e.Queue.Player);
    }

    [Fact]
    public void Match_started_and_ended_carry_id_rosters_and_outcome()
    {
        var h = new Harness(killTarget: 3);
        h.Connect("A", "B", "C", "D");
        h.Enqueue("A", "B", "C", "D");
        h.Engine.Tick(T0);

        h.Clock.Advance(TimeSpan.FromSeconds(2));
        var match = h.StartProposedMatch();

        var started = h.Single(ClashEventTypes.MatchStarted);
        Assert.Equal(match.MatchId, started.Match!.MatchId);
        Assert.Equal("2v2 Showdown", started.Match.GameLabel);
        Assert.Equal(h.Clock.UtcNow, started.Match.StartedAt);
        Assert.Equal(2, started.Match.Teams!.Count);

        var winners = match.Teams[0];
        var losers = match.Teams[1];
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        h.Engine.OnKill(winners[0], losers[0], h.Clock.UtcNow);
        h.Engine.OnKill(winners[0], losers[1], h.Clock.UtcNow);
        h.Engine.OnKill(winners[1], losers[0], h.Clock.UtcNow);

        var ended = h.Single(ClashEventTypes.MatchEnded);
        Assert.Equal(match.MatchId, ended.Match!.MatchId);
        Assert.Equal("2v2 Showdown", ended.Match.GameLabel);
        Assert.Equal("Completed", ended.Match.FinalState);
        Assert.Equal(2, ended.Match.Teams!.Count);
        Assert.Equal(1, ended.Match.Teams[0].Rank);
        Assert.NotNull(ended.Match.DurationSeconds);
    }

    [Fact]
    public void Match_game_label_is_null_when_gametype_unregistered()
    {
        // Queue still references "gt1", but no def is registered, so the label can't be resolved
        // and the consumer falls back to gameType.
        var h = new Harness(registerGameType: false);
        h.Connect("A", "B", "C", "D");
        h.Enqueue("A", "B", "C", "D");
        h.Engine.Tick(T0);

        var locked = h.Single(ClashEventTypes.MatchTeamsLocked);
        Assert.Equal("gt1", locked.Match!.GameType);
        Assert.Null(locked.Match.GameLabel);
    }

    [Fact]
    public void Discord_link_request_emits_player_event()
    {
        var h = new Harness();
        h.Connect("A");
        Assert.True(h.Engine.RequestDiscordLink(K("A"), "  Cool#1234  ", h.Clock.UtcNow));

        var e = h.Single(ClashEventTypes.PlayerDiscordLinkRequested);
        Assert.NotNull(e.Player);
        Assert.Equal("A", e.Player!.Player);
        Assert.Equal("Cool#1234", e.Player.DiscordAlias);   // trimmed by the engine
        Assert.Null(e.Queue);
        Assert.Null(e.Match);
    }

    [Fact]
    public void Blank_discord_alias_emits_nothing()
    {
        var h = new Harness();
        h.Connect("A");
        Assert.False(h.Engine.RequestDiscordLink(K("A"), "   ", h.Clock.UtcNow));
        Assert.Empty(h.Sink.Events);
    }

    [Theory]
    [InlineData(QueueRemovalReason.Cancel, "cancel")]
    [InlineData(QueueRemovalReason.Disconnect, "disconnect")]
    [InlineData(QueueRemovalReason.Matched, "matched")]
    [InlineData(QueueRemovalReason.AfkCull, "afk_cull")]
    [InlineData(QueueRemovalReason.GroupChange, "group_change")]
    [InlineData(QueueRemovalReason.Reset, "reset")]
    public void Removal_reason_maps_to_wire_string(QueueRemovalReason reason, string wire)
    {
        var (mapper, sink, clock) = DirectMapper();
        mapper.OnQueueRemoved(K("A"), "2v2", clock.UtcNow, reason);
        Assert.Equal(wire, sink.Events.Single().Queue!.Reason);
    }

    [Fact]
    public void Dwell_warning_maps_to_dwell_seconds()
    {
        var (mapper, sink, clock) = DirectMapper();
        mapper.OnQueueDwellWarning(K("A"), "2v2", clock.UtcNow, TimeSpan.FromMinutes(15));

        var e = sink.Events.Single();
        Assert.Equal(ClashEventTypes.QueueDwellWarning, e.Type);
        Assert.Equal("A", e.Queue!.Player);
        Assert.Equal(900d, e.Queue.DwellSeconds);
    }

    [Fact]
    public void Ignored_events_emit_nothing()
    {
        var (mapper, sink, clock) = DirectMapper();
        // These are default-interface no-ops the mapper deliberately does not override; call them
        // through the interface to confirm none produce an envelope.
        IMatchmakingTelemetry t = mapper;
        t.OnInviteSent(K("A"), K("B"), clock.UtcNow, TimeSpan.FromSeconds(15));
        t.OnQueueHoldStarted("2v2", new[] { K("A") }, 0.5, TimeSpan.FromSeconds(10));
        t.OnAbandonment(K("A"), 1, clock.UtcNow);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void Winner_promotion_maps_to_queue_joined()
    {
        // The engine fires OnWinnerPromoted (not OnQueueAdded) for a KOTH re-enqueue so the chat
        // adapter can send a promotion-specific DM. For the stream the reason is irrelevant -- the
        // winner is a queue member again -- so it surfaces as a plain queue.joined, no reason.
        var (mapper, sink, clock) = DirectMapper();
        mapper.OnWinnerPromoted(K("A"), "2v2", clock.UtcNow, defensesUsed: 2, maxDefenses: 3, sentToBack: false);

        var e = sink.Events.Single();
        Assert.Equal(ClashEventTypes.QueueJoined, e.Type);
        Assert.Equal("2v2", e.Queue!.QueueName);
        Assert.Equal("2v2 Casual", e.Queue.QueueLabel);
        Assert.Equal("gt1", e.Queue.GameType);
        Assert.Equal("A", e.Queue.Player);
        Assert.Null(e.Queue.Reason);
        Assert.Null(e.Match);
        Assert.Null(e.Player);
    }

    [Fact]
    public void Auto_queue_maps_to_queue_joined()
    {
        var (mapper, sink, clock) = DirectMapper();
        mapper.OnAutoQueued(K("B"), "2v2", clock.UtcNow);

        var e = sink.Events.Single();
        Assert.Equal(ClashEventTypes.QueueJoined, e.Type);
        Assert.Equal("2v2", e.Queue!.QueueName);
        Assert.Equal("B", e.Queue.Player);
        Assert.Null(e.Queue.Reason);
        Assert.Null(e.Match);
    }

    [Fact]
    public void Koth_promotion_emits_queue_joined_with_post_add_count()
    {
        // End-to-end: a real KOTH match completes and re-enqueues both winners at the head. Each
        // re-enqueue must surface on the stream as queue.joined carrying the queue's post-add
        // occupancy (1, then 2 as the winners are re-enqueued in turn), so the board stays accurate.
        var clock = new FakeClock(T0);
        var sink = new RecordingEventSink();
        var engine = new MatchmakingEngine(
            new InMemoryRatingStore(), clock,
            new[] { PenaltyPolicy.DefaultAbandonment, PenaltyPolicy.DefaultGriefing });
        engine.Queues.Register(
            "koth2v2",
            new MatchShape(2, 2),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
            "gt1",
            () => new KillCountEndPolicy(1),
            promoteWinnersToFront: true,
            maxConsecutiveDefenses: 3);
        engine.GameTypes.ReplaceArenaContribution("lobby", new[] { GtDef("gt1", "2v2 Showdown") }, out _);
        engine.SetTelemetry(new EventStreamTelemetry(sink, engine.Queues, engine.GameTypes, clock));

        foreach (var n in new[] { "A", "B", "C", "D" }) engine.OnPlayerConnected(K(n), clock.UtcNow);
        foreach (var n in new[] { "A", "B", "C", "D" }) engine.TryEnqueue(K(n), "koth2v2", clock.UtcNow);
        engine.Tick(clock.UtcNow);

        var match = engine.ActiveMatches.Values.Single();
        foreach (var team in match.Teams)
            foreach (var p in team)
                engine.OnPlayerJoinedArena(p, clock.UtcNow);
        engine.MarkMatchLive(match.MatchId, clock.UtcNow);
        var winners = match.Teams[0];

        int before = sink.Events.Count;
        clock.Advance(TimeSpan.FromSeconds(5));
        engine.OnKill(winners[0], match.Teams[1][0], clock.UtcNow);

        var promotions = sink.Events.Skip(before)
            .Where(e => e.Type == ClashEventTypes.QueueJoined && e.Queue!.QueueName == "koth2v2")
            .ToList();
        Assert.Equal(2, promotions.Count);
        Assert.Equal(
            winners.Select(w => w.Name).OrderBy(x => x),
            promotions.Select(p => p.Queue!.Player!).OrderBy(x => x));
        Assert.Equal(new[] { 1, 2 }, promotions.Select(p => p.Queue!.Count).OrderBy(c => c));
    }

    [Fact]
    public void OccurredAt_is_clock_now()
    {
        var (mapper, sink, clock) = DirectMapper();
        clock.Advance(TimeSpan.FromMinutes(42));
        mapper.OnQueueRemoved(K("A"), "2v2", T0, QueueRemovalReason.Cancel);
        Assert.Equal(clock.UtcNow, sink.Events.Single().OccurredAt);
    }

    [Fact]
    public void Envelope_serializes_camelCase_and_drops_null_siblings()
    {
        var env = new EventEnvelope(EventSchema.Version, ClashEventTypes.QueueJoined, T0,
            Queue: new QueueEventPayload("lobby/2v2", "2v2 Casual", "gt1", 1, 4, Player: "A"));

        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        var json = JsonSerializer.Serialize(env, options);

        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"type\":\"queue.joined\"", json);
        Assert.Contains("\"queueName\":\"lobby/2v2\"", json);
        Assert.Contains("\"player\":\"A\"", json);
        // Null siblings and unset optionals are dropped.
        Assert.DoesNotContain("\"match\"", json);
        Assert.DoesNotContain("\"reason\"", json);
        Assert.DoesNotContain("\"waiting\"", json);
    }

    [Fact]
    public void Match_payload_serializes_game_label_camelCase_and_drops_it_when_null()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        var withLabel = JsonSerializer.Serialize(
            new EventEnvelope(EventSchema.Version, ClashEventTypes.MatchEnded, T0,
                Match: new MatchEventPayload(MatchId: null, GameType: "elim", GameLabel: "Elimination")),
            options);
        Assert.Contains("\"gameType\":\"elim\"", withLabel);
        Assert.Contains("\"gameLabel\":\"Elimination\"", withLabel);

        var withoutLabel = JsonSerializer.Serialize(
            new EventEnvelope(EventSchema.Version, ClashEventTypes.MatchEnded, T0,
                Match: new MatchEventPayload(MatchId: null, GameType: "elim")),
            options);
        Assert.DoesNotContain("\"gameLabel\"", withoutLabel);
    }

    private static (EventStreamTelemetry Mapper, RecordingEventSink Sink, FakeClock Clock) DirectMapper()
    {
        var clock = new FakeClock(T0);
        var registry = new ClashEngine.Core.Queue.QueueRegistry();
        registry.Register("2v2", new MatchShape(2, 2),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)), "gt1", label: "2v2 Casual");
        var sink = new RecordingEventSink();
        return (new EventStreamTelemetry(sink, registry, new GameTypeRegistry(), clock), sink, clock);
    }
}
