namespace ClashEngine.Core.Stats;

/// <summary>
/// Sink for completed-match envelopes. Implementations may POST to a backend, write to a file
/// for batch upload, or no-op (testing). Called by
/// <see cref="ClashEngine.Stats.ClashStatsTelemetry"/> once per finalized match.
/// Implementations must not throw -- failures should be logged.
/// </summary>
public interface IMatchUploader
{
    void Upload(MatchPayload payload);
}
