using SquadHostManager.Core;
namespace SquadHostManager.Service;

// A provider selection is a user's view, never a migration of a running pool.
public sealed class HostingPools
{
    private readonly Store store;
    private readonly string initialProvider;
    private readonly Dictionary<string,PoolCoordinator> pools=[];
    private readonly object creationGate=new();
    public IReadOnlyCollection<PoolCoordinator> All=>pools.Values;
    public HostingPools(Store store,Func<string,IInfrastructure> infrastructure,IConfiguration? config=null)
    {
        this.store=store;
        initialProvider=store.Load<PoolState>("pool")?.Provider??"cherry";
        foreach(var provider in new[]{"cherry","respawn"})
        {
            var key=provider==initialProvider?"pool":"pool-"+provider;
            if(store.Load<PoolState>(key)==null && store.Load<PoolState>("provider-state-"+provider) is {} previous)store.Save(key,previous);
            var otherKey=provider==initialProvider?"pool-"+(provider=="cherry"?"respawn":"cherry"):"pool";
            pools.Add(provider,new(store,infrastructure(provider),config,key,provider,creationGate,
                userId=>store.Load<PoolState>(otherKey)?.Instances.Count(i=>i.OwnerId==userId && i.Phase!="stopped")??0,
                requestId=>store.Load<PoolState>(otherKey)?.Operations.Find(o=>o.Id==requestId.ToString("N"))));
        }
    }
    public PoolCoordinator Get(string provider)=>pools.TryGetValue(provider,out var pool)?pool:throw new ArgumentException("Неизвестный хостинг.");
    public string Selected(User user)=>store.Load<string>("hosting-provider-user-"+user.Id)??initialProvider;
    public PoolCoordinator For(User user)=>Get(Selected(user));
    public HostingProviderView View(User user)=>new(Selected(user),true,Get("cherry").HasRentedHost);
    public HostingProviderView Select(User user,string provider)
    {
        _=Get(provider);new Accounts(store).EnsureAccess(user.Id);
        store.Save("hosting-provider-user-"+user.Id,provider);return View(user);
    }
    public PoolCoordinator Resource(User user,string? id)=>id==null?For(user):pools.Values.FirstOrDefault(p=>p.ContainsResource(id))??For(user);
    public PoolCoordinator Request(User user,Guid requestId,string? instanceId=null)=>pools.Values.FirstOrDefault(p=>p.ContainsOperation(requestId))??Resource(user,instanceId);
    public async Task<ConfigApplyResult> SaveCommonConfig(User user,ConfigRequest request,CancellationToken ct)
    {
        var selected=For(user);var first=await selected.SaveCommonConfig(user,request,ct);
        if(request.Content==null)return first;
        var other=await pools.Values.Single(p=>p!=selected).SaveCommonConfig(user,request,ct);
        return first with{Applied=first.Applied+other.Applied,Failed=first.Failed.Concat(other.Failed).ToList()};
    }
}
public sealed partial class PoolCoordinator
{
    public bool HasRentedHost{get{lock(gate)return state.HostId!=null;}}
    public bool ContainsOperation(Guid requestId){lock(gate)return state.Operations.Any(o=>o.Id==requestId.ToString("N"));}
    public bool ContainsResource(string id)
    {
        lock(gate)return state.Instances.Any(i=>i.Id==id) || state.Matches.Any(m=>m.Value.Id==id) || state.Backups.Any(b=>b.Value.Id==id) || state.Templates.Any(t=>t.Value.Id==id);
    }
}
