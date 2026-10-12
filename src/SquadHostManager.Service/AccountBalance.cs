using SquadHostManager.Core;

namespace SquadHostManager.Service;

public sealed class AccountBalance(IInfrastructure infrastructure)
{
    private readonly SemaphoreSlim gate=new(1,1);
    private BalanceView cached=new(null,"EUR",null,null,true);
    private DateTimeOffset attempted;
    public async Task<BalanceView> Read(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if(DateTimeOffset.UtcNow-attempted>TimeSpan.FromSeconds(30))
            {
                attempted=DateTimeOffset.UtcNow;
                try{cached=await infrastructure.Balance(ct);}
                catch(Exception error)when(error is not OperationCanceledException){cached=cached with{Stale=true};}
            }
            return cached;
        }
        finally{gate.Release();}
    }
}
