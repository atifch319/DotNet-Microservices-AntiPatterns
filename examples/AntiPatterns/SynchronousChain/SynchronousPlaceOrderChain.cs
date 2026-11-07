namespace AntiPatterns.SynchronousChain;

public interface IHopClient
{
    Task CallAsync(CancellationToken cancellationToken);
}

public sealed class DelayingHopClient(TimeSpan delay) : IHopClient
{
    public Task CallAsync(CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed record ChainTrace(IReadOnlyList<string> CompletedHops, TimeSpan Elapsed, bool TimedOut);

/// <summary>
/// Problematic implementation of a place-order call chain. Delays exist only in this example.
/// The runnable services do not add artificial latency.
/// </summary>
public sealed class SynchronousPlaceOrderChain(IHopClient inventory, IHopClient payment, IHopClient notification)
{
    public async Task<ChainTrace> ExecuteAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var completed = new List<string>();
        var started = DateTimeOffset.UtcNow;
        try
        {
            await inventory.CallAsync(timeoutSource.Token);
            completed.Add("inventory");
            await payment.CallAsync(timeoutSource.Token);
            completed.Add("payment");
            await notification.CallAsync(timeoutSource.Token);
            completed.Add("notification");
            return new ChainTrace(completed, DateTimeOffset.UtcNow - started, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ChainTrace(completed, DateTimeOffset.UtcNow - started, true);
        }
    }
}
