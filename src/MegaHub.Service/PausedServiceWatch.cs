using MegaHub.Privileged;

namespace MegaHub.Service;

/// <summary>
/// The loop behind the service's look at paused services: one tick per interval until the service stops. A tick that throws is logged (the same
/// line only once in a row) and the loop goes on; only the service's own stop ends it.
/// </summary>
public static class PausedServiceWatch
{
    public static async Task RunAsync(Func<CancellationToken, int> tick, TimeSpan interval, IEventSink log, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(interval);
        string? lastError = null;
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    tick(stop);
                    lastError = null;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    var line = $"[services] watch: {ex.GetType().Name}";
                    if (line != lastError) log.Error(line);
                    lastError = line;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }
}
