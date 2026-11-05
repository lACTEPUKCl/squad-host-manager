using SquadHostManager.Core;
namespace SquadHostManager.Service;

public sealed partial class PoolCoordinator
{
    private static bool RejectedWithoutAllocation(Store store,Instance instance)=>
        store.Load<RespawnManagedServer>("respawn-instance-"+instance.Id)==null &&
        store.Load<System.Text.Json.JsonElement?>("respawn-order-result-"+Guid.Parse(instance.Id))==null &&
        store.Load<string>("respawn-order-"+Guid.Parse(instance.Id))=="rejected";
    private static void ArchiveRejectedRental(Store store,Instance instance)
    {
        store.Save("rejected-rental-record-"+instance.Id,instance);
        store.Save("last-rental-error-respawn-"+instance.OwnerId,instance.Error??"RespawnHost: аренда отклонена.");
    }
    private static void RestoreRejectedRentals(Store store,PoolState state)
    {
        if(state.Provider!="respawn")return;
        foreach(var instance in state.Instances.Where(i=>i.Phase=="error" && Guid.TryParse(i.Id,out _) && RejectedWithoutAllocation(store,i)).ToList())
        {
            if(state.Operations.Any(o=>o.InstanceId==instance.Id && o.Phase is "queued" or "running"))continue;
            ArchiveRejectedRental(store,instance);state.Instances.Remove(instance);
        }
    }
}
