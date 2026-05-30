using System.Collections.Generic;
using ClashEngine.Core.Events;

namespace ClashEngine.Core.Tests.Fakes;

/// <summary>Records every <see cref="EventEnvelope"/> emitted, for assertions.</summary>
public sealed class RecordingEventSink : IEventSink
{
    public List<EventEnvelope> Events { get; } = new();
    public void Emit(EventEnvelope envelope) => Events.Add(envelope);
}
