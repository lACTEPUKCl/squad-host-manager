using SquadHostManager.Core;

namespace SquadHostManager.Service;

public sealed partial class PoolCoordinator
{
    public async Task<ConfigApplyResult> SaveCommonConfig(User user,ConfigRequest request,CancellationToken ct)
    {
        if(request.Content==null)return new(DefaultConfig(user,request),0,[]);
        await worker.WaitAsync(ct);
        try
        {
            var output=DefaultConfig(user,request);var applied=0;var failed=new List<string>();
            if(request.File!="Admins.cfg")return new(output,applied,failed);
            List<Instance> targets;lock(gate)targets=state.Instances.Where(i=>i.OwnerId==user.Id && i.Phase=="running").ToList();
            foreach(var instance in targets)
            {
                if(await ApplyLiveAdmins(instance,ct))applied++;else failed.Add(instance.Name);
            }
            return new(output,applied,failed);
        }
        finally{worker.Release();}
    }
    private async Task<bool> ApplyLiveAdmins(Instance instance,CancellationToken ct)
    {
        string? host,ip;lock(gate){host=state.HostId;ip=state.Ip;}
        if(host==null || ip==null)return false;
        try
        {
            var content=CommonConfigs(new(instance.OwnerId,instance.OwnerName,false))["Admins.cfg"];
            await infra.Config(host,ip,instance.Id,new("Admins.cfg",content),ct);
            var commandLock=rconLocks.GetOrAdd(instance.Id,_=>new(1,1));await commandLock.WaitAsync(ct);
            try
            {
                var reply=await infra.Command(host,ip,instance,"AdminReloadServerConfig",ct);
                if(reply.TrimStart().StartsWith("ERROR",StringComparison.OrdinalIgnoreCase) || reply.Contains("Unknown command",StringComparison.OrdinalIgnoreCase) || reply.Contains("not recognized",StringComparison.OrdinalIgnoreCase))throw new IOException("RCON rejected AdminReloadServerConfig.");
            }
            finally{commandLock.Release();}
            lock(gate){instance.PendingAdminsReload=false;Save();}return true;
        }
        catch(OperationCanceledException){throw;}
        catch(Exception){lock(gate){instance.PendingAdminsReload=true;Save();}return false;}
    }
}
