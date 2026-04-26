using System;

namespace ClashEngine.Core.Adapter;

/// <summary>
/// Time source. The engine takes one of these so tests can advance time deterministically
/// without sleeping.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
