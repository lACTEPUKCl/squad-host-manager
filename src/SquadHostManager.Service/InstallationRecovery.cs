using SquadHostManager.Core;

namespace SquadHostManager.Service;

public sealed partial class PoolCoordinator
{
    public List<OperationView> RetryFailedInstallations(User user)
    {
        if(!user.IsAdmin)throw new UnauthorizedAccessException("Только владелец хостинга.");
        lock(gate)
        {
            if(state.HostId==null || state.HostPhase!="ready")throw new InvalidOperationException("Хост ещё не готов.");
            var result=new List<OperationView>();
            foreach(var instance in state.Instances.Where(i=>i.Phase=="error" && i.GameState==null && (i.Error?.Contains("Steam download failed",StringComparison.Ordinal)==true || i.Error=="Packet is too big. Maximum packet size is 68536 bytes." || i.Error=="Ошибка операции на игровом хосте (код 1): /bin/bash: /shm-cache-download.sh: Permission denied")))
            {
                if(state.Operations.Any(o=>o.InstanceId==instance.Id && o.Phase is "queued" or "running"))continue;
                var op=new Operation{Id=Guid.NewGuid().ToString("N"),UserId=instance.OwnerId,InstanceId=instance.Id,Kind="restart",ExpectedHostId=state.HostId};
                state.Operations.Add(op);instance.Phase="restarting";instance.Error=null;instance.ReadyNotified=false;instance.DownloadPercent=null;
                result.Add(View(op));
            }
            Save();return result;
        }
    }
}
