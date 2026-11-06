namespace UniMap.ServicePoints.API.Here;

/// <summary>Backoff wait between HERE retries. A seam so tests do not wait.</summary>
public interface IHereDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class TaskHereDelay : IHereDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}
