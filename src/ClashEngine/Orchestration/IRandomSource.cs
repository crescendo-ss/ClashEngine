namespace ClashEngine.Orchestration;

/// <summary>Seam over <see cref="System.Random"/>'s <c>Next(int)</c> so orchestrator
/// spawn-pick logic can be exercised deterministically from a test. The default production
/// implementation is <see cref="DefaultRandomSource"/>, which delegates to
/// <see cref="System.Random.Shared"/>.</summary>
public interface IRandomSource
{
    /// <summary>Returns a non-negative int strictly less than <paramref name="exclusiveMax"/>.</summary>
    int Next(int exclusiveMax);
}

/// <summary>Production <see cref="IRandomSource"/> that delegates to
/// <see cref="System.Random.Shared"/>. Match orchestration runs on the SS mainloop thread,
/// so the thread-safety guarantees of <c>Random.Shared</c> are sufficient.</summary>
public sealed class DefaultRandomSource : IRandomSource
{
    public static DefaultRandomSource Instance { get; } = new();
    public int Next(int exclusiveMax) => System.Random.Shared.Next(exclusiveMax);
}
