using System.Linq;
using ClashEngine.Core;
using ClashEngine.Core.Control;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests.Control;

public class ControlCommandDispatcherTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public RecordingTelemetry Telemetry { get; } = new();
        public MatchmakingEngine Engine { get; }
        public ControlCommandDispatcher Dispatcher { get; }

        public Harness()
        {
            Engine = new MatchmakingEngine(
                new InMemoryRatingStore(), Clock,
                new[] { PenaltyPolicy.DefaultAbandonment, PenaltyPolicy.DefaultGriefing },
                quality: new OrdinalSpreadQuality(),
                telemetry: Telemetry);

            Engine.Queues.Register(
                "2v2",
                new MatchShape(2, 2),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt1");

            Dispatcher = new ControlCommandDispatcher(Engine);
        }

        public void Connect(params string[] names)
        {
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
        }

        public ControlResponse Execute(ControlCommand cmd) => Dispatcher.Execute(cmd, Clock.UtcNow);
    }

    private static ControlCommand Cmd(
        string? type,
        string? player = null,
        string? queue = null,
        IReadOnlyList<string>? players = null,
        IReadOnlyList<IReadOnlyList<string>>? teams = null,
        Guid? matchId = null,
        bool? enabled = null,
        string? commandId = null,
        int version = ControlSchema.Version) =>
        new(version, type, commandId, player, players, queue, teams, matchId, enabled);

    private static IReadOnlyList<IReadOnlyList<string>> Teams2v2(string a, string b, string c, string d) =>
        new[] { new[] { a, b }, new[] { c, d } };

    // ---- envelope-level validation

    [Fact]
    public void Unsupported_schema_version_is_an_error()
    {
        var h = new Harness();
        var resp = h.Execute(Cmd(ControlCommandTypes.Enqueue, player: "A", queue: "2v2", version: 99));
        Assert.Equal(ControlStatus.Error, resp.Status);
        Assert.Equal(ControlResults.UnsupportedSchemaVersion, resp.Result);
    }

    [Fact]
    public void Unknown_type_is_an_error()
    {
        var h = new Harness();
        var resp = h.Execute(Cmd("frobnicate"));
        Assert.Equal(ControlStatus.Error, resp.Status);
        Assert.Equal(ControlResults.UnknownType, resp.Result);
    }

    [Fact]
    public void Command_id_is_echoed_verbatim()
    {
        var h = new Harness();
        h.Connect("A");
        var resp = h.Execute(Cmd(ControlCommandTypes.Enqueue, player: "A", queue: "2v2", commandId: "req-42"));
        Assert.Equal("req-42", resp.CommandId);
    }

    // ---- enqueue

    [Fact]
    public void Enqueue_ok_queues_the_player()
    {
        var h = new Harness();
        h.Connect("A");
        var resp = h.Execute(Cmd(ControlCommandTypes.Enqueue, player: "A", queue: "2v2"));
        Assert.Equal(ControlStatus.Ok, resp.Status);
        Assert.Equal(ControlResults.Queued, resp.Result);
        Assert.Equal(new[] { "2v2" }, h.Engine.QueuesFor(K("A")));
    }

    [Fact]
    public void Enqueue_expands_to_the_players_party_like_play_does()
    {
        var h = new Harness();
        h.Connect("A", "B");
        h.Engine.InviteToGroup(K("A"), K("B"), T0);
        h.Engine.AcceptInvite(K("B"), K("A"), T0, out _);

        var resp = h.Execute(Cmd(ControlCommandTypes.Enqueue, player: "A", queue: "2v2"));

        Assert.Equal(ControlStatus.Ok, resp.Status);
        Assert.Equal(new[] { "2v2" }, h.Engine.QueuesFor(K("A")));
        Assert.Equal(new[] { "2v2" }, h.Engine.QueuesFor(K("B")));
    }

    [Fact]
    public void Enqueue_repeat_reports_refresh_as_ok()
    {
        var h = new Harness();
        h.Connect("A");
        h.Execute(Cmd(ControlCommandTypes.Enqueue, player: "A", queue: "2v2"));
        var resp = h.Execute(Cmd(ControlCommandTypes.Enqueue, player: "A", queue: "2v2"));
        Assert.Equal(ControlStatus.Ok, resp.Status);
        Assert.Equal(ControlResults.AlreadyQueuedRefreshed, resp.Result);
    }

    [Fact]
    public void Enqueue_unknown_queue_is_rejected()
    {
        var h = new Harness();
        h.Connect("A");
        var resp = h.Execute(Cmd(ControlCommandTypes.Enqueue, player: "A", queue: "nope"));
        Assert.Equal(ControlStatus.Rejected, resp.Status);
        Assert.Equal(ControlResults.UnknownQueue, resp.Result);
    }

    [Fact]
    public void Enqueue_disconnected_player_is_rejected_with_the_player_named()
    {
        var h = new Harness();
        var resp = h.Execute(Cmd(ControlCommandTypes.Enqueue, player: "Ghost", queue: "2v2"));
        Assert.Equal(ControlStatus.Rejected, resp.Status);
        Assert.Equal(ControlResults.NotConnected, resp.Result);
        Assert.Equal("Ghost", resp.Player);
    }

    [Fact]
    public void Enqueue_missing_fields_are_errors()
    {
        var h = new Harness();
        Assert.Equal(ControlResults.MissingField,
            h.Execute(Cmd(ControlCommandTypes.Enqueue, queue: "2v2")).Result);
        Assert.Equal(ControlResults.MissingField,
            h.Execute(Cmd(ControlCommandTypes.Enqueue, player: "A")).Result);
    }

    // ---- enqueue_group

    [Fact]
    public void Enqueue_group_queues_all_members_atomically_with_a_shared_group()
    {
        var h = new Harness();
        h.Connect("A", "B");
        var resp = h.Execute(Cmd(ControlCommandTypes.EnqueueGroup,
            players: new[] { "A", "B" }, queue: "2v2"));

        Assert.Equal(ControlStatus.Ok, resp.Status);
        Assert.Equal(ControlResults.Queued, resp.Result);

        Assert.True(h.Engine.Queues.TryGet("2v2", out var def));
        var entries = def.Queue.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.NotNull(entries[0].Group);
        Assert.Equal(entries[0].Group, entries[1].Group);
    }

    [Fact]
    public void Enqueue_group_with_a_disconnected_member_queues_nobody()
    {
        var h = new Harness();
        h.Connect("A");   // B not connected
        var resp = h.Execute(Cmd(ControlCommandTypes.EnqueueGroup,
            players: new[] { "A", "B" }, queue: "2v2"));

        Assert.Equal(ControlStatus.Rejected, resp.Status);
        Assert.Equal(ControlResults.NotConnected, resp.Result);
        Assert.Empty(h.Engine.QueuesFor(K("A")));
    }

    [Fact]
    public void Enqueue_group_with_blank_name_is_an_error()
    {
        var h = new Harness();
        var resp = h.Execute(Cmd(ControlCommandTypes.EnqueueGroup,
            players: new[] { "A", " " }, queue: "2v2"));
        Assert.Equal(ControlStatus.Error, resp.Status);
        Assert.Equal(ControlResults.InvalidField, resp.Result);
    }

    // ---- dequeue

    [Fact]
    public void Dequeue_specific_queue_reports_what_was_removed()
    {
        var h = new Harness();
        h.Connect("A");
        h.Execute(Cmd(ControlCommandTypes.Enqueue, player: "A", queue: "2v2"));

        var resp = h.Execute(Cmd(ControlCommandTypes.Dequeue, player: "A", queue: "2v2"));

        Assert.Equal(ControlStatus.Ok, resp.Status);
        Assert.Equal(ControlResults.Dequeued, resp.Result);
        Assert.Equal(new[] { "2v2" }, resp.RemovedQueues);
        Assert.Empty(h.Engine.QueuesFor(K("A")));
    }

    [Fact]
    public void Dequeue_when_not_queued_is_an_idempotent_ok()
    {
        var h = new Harness();
        h.Connect("A");
        var resp = h.Execute(Cmd(ControlCommandTypes.Dequeue, player: "A", queue: "2v2"));
        Assert.Equal(ControlStatus.Ok, resp.Status);
        Assert.Empty(resp.RemovedQueues!);
    }

    [Fact]
    public void Dequeue_unknown_queue_is_rejected()
    {
        var h = new Harness();
        var resp = h.Execute(Cmd(ControlCommandTypes.Dequeue, player: "A", queue: "nope"));
        Assert.Equal(ControlStatus.Rejected, resp.Status);
        Assert.Equal(ControlResults.UnknownQueue, resp.Result);
    }

    [Fact]
    public void Dequeue_without_queue_leaves_every_queue()
    {
        var h = new Harness();
        h.Connect("A");
        h.Execute(Cmd(ControlCommandTypes.Enqueue, player: "A", queue: "2v2"));

        var resp = h.Execute(Cmd(ControlCommandTypes.Dequeue, player: "A"));

        Assert.Equal(ControlStatus.Ok, resp.Status);
        Assert.Equal(new[] { "2v2" }, resp.RemovedQueues);
        Assert.Empty(h.Engine.QueuesFor(K("A")));
    }

    // ---- form_match

    [Fact]
    public void Form_match_ok_returns_the_match_id()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        var resp = h.Execute(Cmd(ControlCommandTypes.FormMatch,
            queue: "2v2", teams: Teams2v2("A", "B", "C", "D")));

        Assert.Equal(ControlStatus.Ok, resp.Status);
        Assert.Equal(ControlResults.MatchFormed, resp.Result);
        Assert.NotNull(resp.MatchId);
        Assert.True(h.Engine.ActiveMatches.ContainsKey(resp.MatchId!.Value));
    }

    [Fact]
    public void Form_match_shape_mismatch_is_rejected()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D", "E", "F");
        var resp = h.Execute(Cmd(ControlCommandTypes.FormMatch,
            queue: "2v2", teams: new[] { new[] { "A", "B", "E" }, new[] { "C", "D" } }));
        Assert.Equal(ControlStatus.Rejected, resp.Status);
        Assert.Equal(ControlResults.ShapeMismatch, resp.Result);
    }

    [Fact]
    public void Form_match_names_the_blocking_player()
    {
        var h = new Harness();
        h.Connect("A", "B", "C");   // D missing
        var resp = h.Execute(Cmd(ControlCommandTypes.FormMatch,
            queue: "2v2", teams: Teams2v2("A", "B", "C", "D")));
        Assert.Equal(ControlStatus.Rejected, resp.Status);
        Assert.Equal(ControlResults.NotConnected, resp.Result);
        Assert.Equal("D", resp.Player);
    }

    // ---- cancel_match

    [Fact]
    public void Cancel_match_cancels_a_forming_match()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        var formed = h.Execute(Cmd(ControlCommandTypes.FormMatch,
            queue: "2v2", teams: Teams2v2("A", "B", "C", "D")));

        var resp = h.Execute(Cmd(ControlCommandTypes.CancelMatch, matchId: formed.MatchId));

        Assert.Equal(ControlStatus.Ok, resp.Status);
        Assert.Equal(ControlResults.MatchCancelled, resp.Result);
        Assert.Empty(h.Engine.ActiveMatches);
        // A service cancel is blameless: nobody lands in timeout, so the same four can re-form.
        var again = h.Execute(Cmd(ControlCommandTypes.FormMatch,
            queue: "2v2", teams: Teams2v2("A", "B", "C", "D")));
        Assert.Equal(ControlStatus.Ok, again.Status);
    }

    [Fact]
    public void Cancel_match_unknown_id_is_rejected()
    {
        var h = new Harness();
        var resp = h.Execute(Cmd(ControlCommandTypes.CancelMatch, matchId: Guid.NewGuid()));
        Assert.Equal(ControlStatus.Rejected, resp.Status);
        Assert.Equal(ControlResults.UnknownMatch, resp.Result);
    }

    [Fact]
    public void Cancel_match_on_a_live_match_is_rejected()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        var formed = h.Execute(Cmd(ControlCommandTypes.FormMatch,
            queue: "2v2", teams: Teams2v2("A", "B", "C", "D")));
        var matchId = formed.MatchId!.Value;
        foreach (var n in new[] { "A", "B", "C", "D" })
            h.Engine.OnPlayerJoinedArena(K(n), h.Clock.UtcNow);
        Assert.True(h.Engine.MarkMatchLive(matchId, h.Clock.UtcNow));

        var resp = h.Execute(Cmd(ControlCommandTypes.CancelMatch, matchId: matchId));

        Assert.Equal(ControlStatus.Rejected, resp.Status);
        Assert.Equal(ControlResults.NotForming, resp.Result);
        Assert.True(h.Engine.ActiveMatches.ContainsKey(matchId));
    }

    // ---- set_auto

    [Fact]
    public void Set_auto_updates_the_preference()
    {
        var h = new Harness();
        var resp = h.Execute(Cmd(ControlCommandTypes.SetAuto, player: "A", enabled: true));
        Assert.Equal(ControlStatus.Ok, resp.Status);
        Assert.Equal(ControlResults.AutoSet, resp.Result);
        Assert.True(h.Engine.AutoQueue.IsEnabled(K("A")));

        h.Execute(Cmd(ControlCommandTypes.SetAuto, player: "A", enabled: false));
        Assert.False(h.Engine.AutoQueue.IsEnabled(K("A")));
    }

    [Fact]
    public void Set_auto_without_enabled_is_an_error()
    {
        var h = new Harness();
        var resp = h.Execute(Cmd(ControlCommandTypes.SetAuto, player: "A"));
        Assert.Equal(ControlStatus.Error, resp.Status);
        Assert.Equal(ControlResults.MissingField, resp.Result);
    }

    // ---- PlanRatingSeed

    [Fact]
    public void Plan_rating_seed_covers_solo_party_group_and_roster_commands()
    {
        var h = new Harness();
        h.Connect("A", "B");
        h.Engine.InviteToGroup(K("A"), K("B"), T0);
        h.Engine.AcceptInvite(K("B"), K("A"), T0, out _);

        var solo = h.Dispatcher.PlanRatingSeed(Cmd(ControlCommandTypes.Enqueue, player: "C", queue: "2v2"));
        Assert.NotNull(solo);
        Assert.Equal("gt1", solo!.GameType);
        Assert.Equal(new[] { K("C") }, solo.Players);

        var party = h.Dispatcher.PlanRatingSeed(Cmd(ControlCommandTypes.Enqueue, player: "A", queue: "2v2"));
        Assert.NotNull(party);
        Assert.Equal(2, party!.Players.Count);
        Assert.Contains(K("A"), party.Players);
        Assert.Contains(K("B"), party.Players);

        var group = h.Dispatcher.PlanRatingSeed(Cmd(ControlCommandTypes.EnqueueGroup,
            players: new[] { "C", "D" }, queue: "2v2"));
        Assert.Equal(new[] { K("C"), K("D") }, group!.Players);

        var roster = h.Dispatcher.PlanRatingSeed(Cmd(ControlCommandTypes.FormMatch,
            queue: "2v2", teams: Teams2v2("A", "B", "C", "D")));
        Assert.Equal(4, roster!.Players.Count);
    }

    [Fact]
    public void Plan_rating_seed_is_null_when_nothing_needs_seeding()
    {
        var h = new Harness();
        Assert.Null(h.Dispatcher.PlanRatingSeed(Cmd(ControlCommandTypes.Dequeue, player: "A", queue: "2v2")));
        Assert.Null(h.Dispatcher.PlanRatingSeed(Cmd(ControlCommandTypes.Enqueue, player: "A", queue: "nope")));
        Assert.Null(h.Dispatcher.PlanRatingSeed(Cmd(ControlCommandTypes.Enqueue, player: "A")));
        Assert.Null(h.Dispatcher.PlanRatingSeed(Cmd(ControlCommandTypes.SetAuto, player: "A", enabled: true)));
    }
}
