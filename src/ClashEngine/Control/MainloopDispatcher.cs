using System;
using System.Threading.Tasks;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Control;

/// <summary>
/// Bounces work from a foreign thread onto the SS mainloop thread and hands the result back as
/// an awaitable <see cref="Task{T}"/> -- the request/response counterpart of the fire-and-forget
/// <see cref="IMainloop.QueueMainWorkItem{TState}"/> pattern <c>?play</c> uses. Everything
/// engine-facing must run on the mainloop; the control listener's HTTP worker threads route every
/// engine touch through this.
/// </summary>
public sealed class MainloopDispatcher
{
    private readonly IMainloop _mainloop;

    public MainloopDispatcher(IMainloop mainloop)
    {
        _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
    }

    /// <summary>
    /// Runs <paramref name="func"/> on the mainloop thread and returns its result. Continuations
    /// resume off the mainloop (<see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>),
    /// so awaiting callers can never block the loop they were serviced by. Throws
    /// <see cref="TimeoutException"/> when the mainloop hasn't run the item within
    /// <paramref name="timeout"/> -- the work item may still execute later, so callers must treat
    /// that as "engine busy, result unknown" rather than "not applied".
    /// </summary>
    public async Task<T> RunAsync<T>(Func<T> func, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(func);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mainloop.QueueMainWorkItem(static state =>
        {
            try { state.Tcs.TrySetResult(state.Func()); }
            catch (Exception ex) { state.Tcs.TrySetException(ex); }
        }, (Func: func, Tcs: tcs));
        return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
    }
}
