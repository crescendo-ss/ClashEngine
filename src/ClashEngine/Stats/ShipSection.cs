using SS.Core;

namespace ClashEngine.Stats;

/// <summary>
/// Maps a <see cref="ShipType"/> to its arena-conf section name (the same eight names the
/// SubspaceServer stock conf uses: <c>Warbird</c>, <c>Javelin</c>, ...). Centralizes the switch
/// so the energy/inventory builders and the EMP lookup all read the same source of truth and
/// stay in sync if the canonical naming ever changes.
/// </summary>
internal static class ShipSection
{
    /// <summary>
    /// Returns the conf-section name for <paramref name="ship"/>. Unknown / spec ship values
    /// fall back to <c>Warbird</c> rather than throwing, matching the long-standing behavior of
    /// the per-ship config readers.
    /// </summary>
    public static string Of(ShipType ship) => ship switch
    {
        ShipType.Warbird => "Warbird",
        ShipType.Javelin => "Javelin",
        ShipType.Spider => "Spider",
        ShipType.Leviathan => "Leviathan",
        ShipType.Terrier => "Terrier",
        ShipType.Weasel => "Weasel",
        ShipType.Lancaster => "Lancaster",
        ShipType.Shark => "Shark",
        _ => "Warbird",
    };
}
