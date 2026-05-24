namespace ClashEngine.Core.Stats;

/// <summary>
/// Integration surface for outgoing match envelopes. Implement this interface to plug a
/// custom backend (visualizer, dashboard, alternate stats server, local file archive) into
/// ClashEngine. The Core DTOs (<see cref="MatchPayload"/> and its nested types) match
/// <c>schema/match.schema.json</c> 1:1 -- a consumer who references <c>ClashEngine.Core</c>
/// and reads that schema has everything needed to write an alternate implementation without
/// forking the host project.
/// </summary>
/// <remarks>
/// <para>See also <see cref="ClashEngine.Core.Ratings.IRatingsProvider"/> (incoming
/// rating pulls, <c>schema/rating.schema.json</c>) and
/// <see cref="ClashEngine.Core.GameType.IGameTypeRegistrar"/> (outgoing gametype
/// registrations, <c>schema/gametype.schema.json</c>) for the other halves of the
/// integration surface.</para>
///
/// <para><see cref="Upload"/> is called once per finalized match by
/// <see cref="ClashEngine.Stats.ClashStatsTelemetry"/>. It is fire-and-forget from the
/// engine's point of view: the call must return promptly (the SubspaceServer mainloop is
/// waiting) and must not throw -- transport failures, queue overflow, serializer errors,
/// and any other I/O misery are the implementation's responsibility to absorb and log.</para>
///
/// <para>The bundled implementations (<see cref="ClashEngine.Stats.HttpMatchUploader"/>
/// and <see cref="ClashEngine.Stats.JsonFileMatchUploader"/>) both hand the actual work to
/// a background worker so the <see cref="Upload"/> call returns immediately; a custom
/// implementation is free to choose any strategy as long as it honors the
/// "return-fast, never-throw" contract.</para>
/// </remarks>
public interface IMatchUploader
{
    void Upload(MatchPayload payload);
}
