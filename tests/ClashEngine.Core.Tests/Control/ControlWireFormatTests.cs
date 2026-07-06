using System.Text.Json;
using ClashEngine.Core.Control;

namespace ClashEngine.Core.Tests.Control;

/// <summary>
/// Wire-shape guards for the control surface: a camelCase JSON body (what an external service
/// sends per <c>schema/command.schema.json</c>) must deserialize into <see cref="ControlCommand"/>,
/// and <see cref="ControlResponse"/> / <see cref="StateSnapshot"/> must serialize camelCase with
/// nulls dropped. Mirrors the options the plug-in's listener uses.
/// </summary>
public class ControlWireFormatTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Command_json_deserializes_with_camel_case_fields()
    {
        const string body = """
            {
              "schemaVersion": 1,
              "type": "form_match",
              "commandId": "req-7",
              "queue": "lobby/casual_4v4",
              "teams": [["A", "B"], ["C", "D"]]
            }
            """;

        var cmd = JsonSerializer.Deserialize<ControlCommand>(body, JsonOptions);

        Assert.NotNull(cmd);
        Assert.Equal(ControlSchema.Version, cmd!.SchemaVersion);
        Assert.Equal(ControlCommandTypes.FormMatch, cmd.Type);
        Assert.Equal("req-7", cmd.CommandId);
        Assert.Equal("lobby/casual_4v4", cmd.Queue);
        Assert.Equal(2, cmd.Teams!.Count);
        Assert.Equal(new[] { "A", "B" }, cmd.Teams[0]);
        Assert.Null(cmd.Player);
        Assert.Null(cmd.MatchId);
    }

    [Fact]
    public void Response_serializes_camel_case_and_drops_nulls()
    {
        var resp = new ControlResponse(
            ControlSchema.Version, "req-7", ControlStatus.Rejected,
            ControlResults.InTimeout, "serving a timeout", Player: "D");

        string json = JsonSerializer.Serialize(resp, JsonOptions);

        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"commandId\":\"req-7\"", json);
        Assert.Contains("\"status\":\"rejected\"", json);
        Assert.Contains("\"result\":\"in_timeout\"", json);
        Assert.Contains("\"player\":\"D\"", json);
        // Unset optionals stay off the wire entirely.
        Assert.DoesNotContain("matchId", json);
        Assert.DoesNotContain("removedQueues", json);
    }

    [Fact]
    public void Snapshot_serializes_camel_case_and_drops_nulls()
    {
        var snap = new StateSnapshot(
            ControlSchema.Version,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new[]
            {
                new QueueStateSnapshot("lobby/2v2", "2v2", null, 4, 2, 2,
                    new[] { new QueueMemberSnapshot("A", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) }),
            },
            Array.Empty<MatchStateSnapshot>(),
            Array.Empty<PenaltyStateSnapshot>());

        string json = JsonSerializer.Serialize(snap, JsonOptions);

        Assert.Contains("\"capturedAt\":", json);
        Assert.Contains("\"queueName\":\"lobby/2v2\"", json);
        Assert.Contains("\"capacity\":4", json);
        Assert.Contains("\"teamCount\":2", json);
        Assert.Contains("\"playersPerTeam\":2", json);
        Assert.Contains("\"player\":\"A\"", json);
        Assert.DoesNotContain("gameType", json);   // null -> dropped
        Assert.DoesNotContain("\"group\"", json);  // null -> dropped
    }
}
