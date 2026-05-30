namespace ClashEngine.Core.Events;

/// <summary>
/// Outbound event-stream edge: a fire-and-forget push of normalized queue- and match-lifecycle
/// events to some external consumer (e.g. a Discord bot that advertises queue state and DMs
/// players). One of the engine's pluggable I/O edges, alongside
/// <see cref="ClashEngine.Core.Stats.IMatchUploader"/>,
/// <see cref="ClashEngine.Core.GameType.IGameTypeRegistrar"/>, and
/// <see cref="ClashEngine.Core.Ratings.IRatingsProvider"/>.
/// </summary>
/// <remarks>
/// <para>Same contract as <see cref="ClashEngine.Core.Stats.IMatchUploader"/>: <see cref="Emit"/>
/// is called on the engine's mainloop thread and MUST return promptly -- the mainloop is waiting.
/// It MUST NOT throw and MUST NOT block on I/O; the implementation absorbs and logs all transport
/// failures (typically by handing the envelope to a background worker that drops on overflow).</para>
///
/// <para>The engine is deliberately identity-agnostic: events are keyed by in-game player
/// <see cref="ClashEngine.Core.Identity.PlayerKey.Name"/>. Any mapping to a Discord account, and
/// any per-player opt-in, lives entirely in the external consumer, not here.</para>
/// </remarks>
public interface IEventSink
{
    /// <summary>Publishes one event. Must return promptly, never throw, never block on I/O.</summary>
    void Emit(EventEnvelope envelope);
}

/// <summary>
/// No-op <see cref="IEventSink"/>. Used as the engine's default when no event-stream endpoint is
/// configured (mirrors <see cref="ClashEngine.Core.Adapter.NoOpTelemetry"/>).
/// </summary>
public sealed class NoOpEventSink : IEventSink
{
    public static NoOpEventSink Instance { get; } = new();
    public void Emit(EventEnvelope envelope) { }
}
