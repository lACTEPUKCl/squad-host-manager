using SquadHostManager.Core;
namespace SquadHostManager.Service;
public sealed partial class PoolCoordinator
{
    private bool providerChanging;
    private bool ProviderBlocked()=>providerChanging || state.Instances.Any(i=>i.Phase!="stopped") || state.Operations.Any(o=>o.Phase is "queued" or "running") || state.HostPhase is not ("empty" or "ready");
    public HostingProviderView Provider(){lock(gate)return new(state.Provider,!ProviderBlocked(),(state.Provider=="cherry"?state.HostId:store.Load<PoolState>("provider-state-cherry")?.HostId)!=null);}
    private void RequireCherry()
    {
        if(providerChanging || state.Provider!="cherry")throw new InvalidOperationException("Выбран другой хостинг или выполняется переключение.");
    }
    public async Task<HostingProviderView> SwitchProvider(User user,string provider,Func<Task<bool>> respawnHasServers,CancellationToken ct)
    {
        if(!user.IsAdmin)throw new UnauthorizedAccessException("Хостинг переключает владелец.");
        if(provider is not ("cherry" or "respawn"))throw new ArgumentException("Неизвестный хостинг.");
        lock(gate){if(state.Provider!=provider && ProviderBlocked())throw new InvalidOperationException("Сначала остановите все серверы и дождитесь завершения операций.");}
        await worker.WaitAsync(ct);
        try
        {
            string current;string? host,ip;
            lock(gate)
            {
                if(state.Provider==provider)return Provider();
                if(ProviderBlocked())throw new InvalidOperationException("Сначала остановите все серверы и дождитесь завершения операций.");
                providerChanging=true;current=state.Provider;host=state.HostId;ip=state.Ip;
            }
            try
            {
                if(current=="cherry" && host!=null)
                {
                    if(ip==null || state.HostPhase!="ready")throw new InvalidOperationException("Не удалось проверить текущий хост.");
                    if((await infra.Inventory(host,ip,ct)).Any())throw new InvalidOperationException("На текущем хосте ещё есть серверные контейнеры.");
                }
                if(current=="respawn" && await respawnHasServers())throw new InvalidOperationException("На RespawnHost ещё работают серверы или не завершена аренда.");
                ct.ThrowIfCancellationRequested();
                lock(gate)
                {
                    store.Save("provider-state-"+current,state);
                    var previous=state;var next=store.Load<PoolState>("provider-state-"+provider)??new();
                    next.Provider=provider;next.Backups=previous.Backups;next.Matches=previous.Matches;next.DeletedMatches=previous.DeletedMatches;next.Notices=previous.Notices;next.Templates=previous.Templates;next.Versions=previous.Versions;next.Budgets=previous.Budgets;next.Discord=previous.Discord;next.Rentals=previous.Rentals;next.CostEvents=previous.CostEvents;next.Metrics=previous.Metrics;next.Rounds=previous.Rounds;
                    state=next;Save();
                }
            }
            finally{lock(gate)providerChanging=false;}
            return Provider();
        }
        finally{worker.Release();}
    }
    private void RequireStartProvider(User user)
    {
        if(providerChanging)throw new InvalidOperationException("Выполняется переключение хостинга.");
        if(state.Provider=="respawn" && store.Load<RespawnAllocation>("respawn-plan-"+user.Id)==null)throw new InvalidOperationException("Сначала выберите тариф RespawnHost на странице хостинга.");
    }
    public async Task<OperationView> StartRespawn(User user,RespawnRentRequest request,RespawnHost api,CancellationToken ct)
    {
        new Accounts(store).EnsureRentalAccess(user.Id);
        if(!api.Configured)throw new InvalidOperationException("Владелец должен подключить API-ключ RespawnHost.");
        var plan=(await api.Plans(ct)).SingleOrDefault(p=>p.Id==request.PlanId);
        if(plan==null || plan.HourlyPrice!=request.HourlyPrice)throw new InvalidOperationException("Тариф изменился. Обновите список.");
        if(request.Region != "eu")throw new ArgumentException("Неверный регион.");
        lock(gate)
        {
            if(providerChanging || state.Provider!="respawn")throw new InvalidOperationException("Сначала выберите RespawnHost.");
            if(Existing(user,request.RequestId) is {} old)return View(old);
            store.Save("respawn-plan-"+user.Id,new RespawnAllocation(plan.Id,plan.MemoryMb,plan.HourlyPrice,request.Region));
            var settings=request.Settings??new ServerSettingsRequest("Squad · "+user.Name,"",Defaults(user).Mods,100);
            return Start(user,new(request.RequestId,settings.Name,settings.Password,settings.Mods,settings.MaxPlayers,StartLayer:settings.StartLayer));
        }
    }
    public async Task<T> OnRespawn<T>(User user,Func<Task<T>> action,CancellationToken ct)
    {
        if(!user.IsAdmin)throw new UnauthorizedAccessException("Управление RespawnHost доступно владельцу.");
        lock(gate){if(providerChanging || state.Provider!="respawn")throw new InvalidOperationException("Сначала выберите RespawnHost.");}
        await worker.WaitAsync(ct);
        try{lock(gate){if(providerChanging || state.Provider!="respawn")throw new InvalidOperationException("Сначала выберите RespawnHost.");providerChanging=true;}try{return await action();}finally{lock(gate)providerChanging=false;}}
        finally{worker.Release();}
    }
}
