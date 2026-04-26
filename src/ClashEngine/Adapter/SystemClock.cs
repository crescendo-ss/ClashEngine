using System;
using ClashEngine.Core.Adapter;

namespace ClashEngine.Adapter;

/// <summary>Wall-clock time source. Used in production; tests use <c>FakeClock</c>.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
