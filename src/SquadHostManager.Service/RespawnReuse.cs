namespace SquadHostManager.Service;
public sealed partial class RespawnHost
{
    public async Task<RespawnManagedServer?> Reusable(string owner,RespawnAllocation plan,CancellationToken ct)
    {
        var referenced=new[]{store.Load<PoolState>("pool"),store.Load<PoolState>("pool-respawn"),store.Load<PoolState>("pool-cherry")}
            .Where(p=>p!=null).SelectMany(p=>p!.Instances).Select(i=>i.Id).ToHashSet();
        var maps=store.LoadPrefix<RespawnManagedServer>("respawn-instance-");
        var occupied=maps.Where(m=>referenced.Contains(m.Id["respawn-instance-".Length..])).Select(m=>m.Value.Uuid).ToHashSet();
        var candidates=maps.Where(m=>!referenced.Contains(m.Id["respawn-instance-".Length..]) && !occupied.Contains(m.Value.Uuid) && m.Value.OwnerId==owner && m.Value.Plan==plan).Select(m=>m.Value).ToList();
        if(candidates.Count==0)return null;
        var servers=await Servers(ct);
        var pending=store.Load<Dictionary<string,string>>("respawn-power-pending")??[];
        return candidates.FirstOrDefault(m=>!pending.ContainsKey(m.Uuid) && servers.Any(s=>s.Id==m.Uuid && s.State is "offline" or "stopped"));
    }
}
