namespace Boxy.Web.Services;

/// <summary>
/// Periodically discards abandoned chunked-upload parts (a dropped multi-GB upload would otherwise
/// keep its parts on disk until the next restart). Runs once on start, then every few hours.
/// </summary>
public class TempCleanupService(IBlobStore storage, ILogger<TempCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                storage.CleanupStaleScratch(TimeSpan.FromHours(24));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Periodic temp cleanup failed");
            }
        } while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
