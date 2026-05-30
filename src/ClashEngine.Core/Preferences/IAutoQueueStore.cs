using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Preferences;

/// <summary>
/// Per-player "auto-queue" preference. When enabled, the engine re-enqueues the player into the
/// queue their just-finished match was formed from (see <c>MatchmakingEngine</c> finalization), so
/// a player who opts in keeps cycling through matches without re-issuing <c>?play</c>. It is a
/// persistent switch the player toggles with <c>?autoqueue [on|off]</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is independent of KOTH winner-promotion (<c>QueueDefinition.PromoteWinnersToFront</c>):
/// auto-queue is an opt-in per-player setting that applies to winners and losers alike and adds
/// players to the back of the queue, whereas KOTH is a per-queue rule that pushes only the winning
/// team to the head.
/// </para>
/// <para>
/// Thread-safety is implementation-defined; the in-memory implementation is safe for concurrent
/// reads and writes. The engine reads/writes on the mainloop thread, and a persistence wrapper may
/// also touch it from a worker thread.
/// </para>
/// </remarks>
public interface IAutoQueueStore
{
    /// <summary>Returns <see langword="true"/> if <paramref name="player"/> has auto-queue enabled.</summary>
    bool IsEnabled(PlayerKey player);

    /// <summary>Turns auto-queue on or off for <paramref name="player"/>.</summary>
    void Set(PlayerKey player, bool enabled);
}
