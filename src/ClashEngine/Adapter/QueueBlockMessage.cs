using System;
using ClashEngine.Core.Matching;

namespace ClashEngine.Adapter;

/// <summary>
/// Renders a <see cref="QueueBlockStatus"/> (why a full-enough queue hasn't started a match) into
/// the single operator-facing line shared by two surfaces: the Verbose "why" log
/// (<see cref="EngineEventListener.OnQueueMatchmakingBlocked"/>) and the <c>?queue</c> reply
/// (<c>MatchmakingCommands.ShowQueueDetail</c>). Keeping it here means both read identically.
/// </summary>
internal static class QueueBlockMessage
{
    public static string Format(QueueBlockStatus status, DateTimeOffset now) => status.Reason switch
    {
        QueueBlockReason.BelowQualityThreshold =>
            "Sufficient players, matchmaking failed to find a game with balanced teams. " +
            $"Best match quality {status.BestQuality:0.0#} does not meet threshold of {status.Threshold:0.0#}.",

        QueueBlockReason.NoViableTeams =>
            "Sufficient players, but no balanced team assignment is possible right now " +
            "(party grouping or rating-spread limits). Waiting for the queue to change.",

        QueueBlockReason.SkillSpreadTooWide =>
            "Sufficient players, but the skill gap between them is too wide (waiting for a " +
            $"closer-rated player). This limit relaxes in {RemainingSeconds(status.HoldUntil, now)} s.",

        QueueBlockReason.HoldingForArrivals =>
            "Sufficient players, matchmaking succeeded. Waiting " +
            $"{RemainingSeconds(status.HoldUntil, now)} more seconds for potential additions to " +
            "queue to find best teams.",

        _ => "",
    };

    private static int RemainingSeconds(DateTimeOffset? holdUntil, DateTimeOffset now)
    {
        if (holdUntil is not { } until) return 0;
        var remaining = until - now;
        return remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalSeconds);
    }
}
