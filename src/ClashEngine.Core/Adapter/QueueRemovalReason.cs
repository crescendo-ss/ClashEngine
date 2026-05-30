namespace ClashEngine.Core.Adapter;

/// <summary>
/// Why a player left a queue, carried on <see cref="IMatchmakingTelemetry.OnQueueRemoved"/> so
/// observers (the event stream, chat) can distinguish a user-initiated cancel from a disconnect,
/// a match formation, an AFK cull, a party change, or an operator reset.
/// </summary>
public enum QueueRemovalReason
{
    /// <summary>The player explicitly left (e.g. <c>?cancel</c>), or a queue was reconfigured out
    /// from under them. The default.</summary>
    Cancel,

    /// <summary>The player disconnected from the zone while queued.</summary>
    Disconnect,

    /// <summary>The player was dequeued because a match they were proposed into formed.</summary>
    Matched,

    /// <summary>The player was auto-removed after idling in the queue past the dwell-cull threshold.</summary>
    AfkCull,

    /// <summary>The player was swept from queues by a party membership change (join/leave/disband).</summary>
    GroupChange,

    /// <summary>The player's matchmaking state was wiped by an operator <c>?clashreset</c>.</summary>
    Reset,
}
