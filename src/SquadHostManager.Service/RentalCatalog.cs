using SquadHostManager.Core;

namespace SquadHostManager.Service;

public sealed class RentalCatalog(IInfrastructure infrastructure)
{
    private readonly SemaphoreSlim gate=new(1,1);
    private List<RentalLocation>? cached;
    private DateTimeOffset checkedAt;
    public async Task<List<RentalLocation>> Read(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if(cached==null || DateTimeOffset.UtcNow-checkedAt>=TimeSpan.FromSeconds(30))
            {cached=await infrastructure.Locations(ct);checkedAt=DateTimeOffset.UtcNow;}
            return cached.ToList();
        }
        finally{gate.Release();}
    }
}
