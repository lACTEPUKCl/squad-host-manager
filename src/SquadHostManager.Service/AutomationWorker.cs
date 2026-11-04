namespace SquadHostManager.Service;

public sealed class AutomationWorker(HostingPools pools,ILogger<AutomationWorker> logger):BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct)=>Task.WhenAll(pools.All.Select(pool=>Run(pool,ct)));
    private async Task Run(PoolCoordinator pool,CancellationToken ct)
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
