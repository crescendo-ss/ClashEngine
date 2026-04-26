namespace ClashEngine.Core.Stats;

/// <summary>
/// Items the player can deploy. Use counts feed into <see cref="PlayerStats"/>.
/// Repels additionally trigger forced-repel-damage attribution against the user's recent attackers.
/// </summary>
public enum ItemKind
{
    Repel,
    Burst,
    Decoy,
    Thor,
    Brick,
    Portal,
    Rocket,
}
