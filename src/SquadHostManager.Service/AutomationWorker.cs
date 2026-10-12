namespace SquadHostManager.Service;

public sealed class AutomationWorker(PoolCoordinator pool,ILogger<AutomationWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try{await pool.TickFeatures(ct);await pool.TickServerTools(ct);await pool.DeliverDiscord(ct);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception error){logger.LogWarning("Automation tick failed ({Type})",error.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(5),ct);
        }
    }
}
