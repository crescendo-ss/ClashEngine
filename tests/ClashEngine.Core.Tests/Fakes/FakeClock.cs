using ClashEngine.Core.Adapter;

namespace ClashEngine.Core.Tests.Fakes;

/// <summary>Test clock with explicit time advancement. Not thread-safe; tests are single-threaded.</summary>
public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset start) { UtcNow = start; }
    public FakeClock() : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) { }

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan delta) => UtcNow += delta;
    public void Set(DateTimeOffset to) => UtcNow = to;
}
