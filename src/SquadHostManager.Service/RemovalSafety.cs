namespace SquadHostManager.Service;

public static class RemovalSafety
{
    public static bool CanRemove(PoolState state) => state.HostId==null && state.HostPhase=="empty"
        && state.Instances.All(i=>i.Phase=="stopped")
        && state.Operations.All(o=>o.Phase is not ("queued" or "running"));
}
