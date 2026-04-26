namespace ClashEngine.Orchestration;

/// <summary>Where a <c>MatchOrchestrator</c> is in its life cycle.</summary>
public enum MatchPhase
{
    /// <summary>Players being placed onto ships and freqs.</summary>
    Setup,

    /// <summary>
    /// Idle detection window: players have been warped and ship-locked. We wait for each player
    /// to demonstrate non-idleness (rotation change, movement, or weapon fire). AFK players at
    /// end of staging are flagged as no-shows and the match is cancelled.
    /// </summary>
    Staging,

    /// <summary>Pre-match countdown is running.</summary>
    Countdown,

    /// <summary>Match is being played.</summary>
    Live,

    /// <summary>Match ended; cleaning up.</summary>
    Cleanup,
}
