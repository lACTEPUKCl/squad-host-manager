using SquadHostManager.Core;
namespace SquadHostManager.Service;

public sealed partial class PoolCoordinator
{
    private Instance Owned(User user,string id)=>state.Instances.Find(i=>i.Id==id && i.OwnerId==user.Id)??throw new UnauthorizedAccessException("Выберите свой сервер.");
    private void EnforceQuota(User user,Instance? restarting)
    {
        var accounts=new Accounts(store);accounts.EnsureAccess(user.Id);
        if(!user.IsAdmin && state.Instances.Count(i=>i.OwnerId==user.Id && Occupied(i) && i!=restarting)>=accounts.Policy(user.Id).MaxServers)throw new InvalidOperationException("Достигнут лимит серверов пользователя.");
    }
    private void Notify(string owner,string code,string? instance,string detail)
    {
        state.Notices.Add(new(owner,new(Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,code,instance,detail)));
        foreach(var old in state.Notices.Where(n=>n.OwnerId==owner).Reverse().Skip(200).ToList())state.Notices.Remove(old);
    }
    private void TrimVersions(string id,string file)
    {foreach(var old in state.Versions.Where(v=>v.Value.InstanceId==id && v.Value.File==file).Reverse().Skip(20).ToList()){state.Versions.Remove(old);store.Delete("config-version-"+old.Value.Id);}}
    public AutomationView Features(User user,string? id)
    {
        lock(gate)
        {
            if(id!=null)Owned(user,id);
            var report=CostAccounting.Calculate(state.Rentals,state.CostEvents,DateTimeOffset.UtcNow);
            var costs=report.Users.Where(r=>r.UserId==user.Id).ToList();
            var active=state.CostEvents.LastOrDefault(e=>e.HostId==state.HostId)?.Users;
            var rate=state.Rentals.LastOrDefault(r=>r.Id==state.HostId)?.HourlyRate;
            var hostCosts=CostAccounting.Calculate(state.Rentals.Where(r=>r.Id==state.HostId).ToList(),state.CostEvents.Where(e=>e.HostId==state.HostId).ToList(),DateTimeOffset.UtcNow);
            return new(state.Templates.Where(t=>t.OwnerId==user.Id).Select(t=>t.Value).ToList(),state.Notices.Where(n=>n.OwnerId==user.Id).Select(n=>n.Value).Reverse().ToList(),state.Budgets.GetValueOrDefault(user.Id)??new(),costs,active?.Any(u=>u.Id==user.Id)==true && active.Count>0 && rate!=null?rate/active.Count:null,state.Rentals.LastOrDefault(r=>r.Id==state.HostId)?.Currency,report.HistoryIncomplete,state.HostId==null?null:hostCosts.Users.Where(r=>r.UserId==user.Id).Sum(r=>r.Spent));
        }
    }
    public async Task<TemplateView> SaveTemplate(User user,TemplateRequest request,CancellationToken ct)
    {
        if(request.Name.Trim().Length is <3 or >80)throw new ArgumentException("Название шаблона: 3–80 символов.");
        await worker.WaitAsync(ct);
        try
        {
            Instance inst;lock(gate){inst=Owned(user,request.InstanceId);if(inst.Phase is not ("running" or "stopped" or "error") || state.HostPhase!="ready")throw new InvalidOperationException("Дождитесь готовности сервера.");if(state.Templates.Count(t=>t.OwnerId==user.Id)>=30)throw new InvalidOperationException("Можно сохранить до 30 шаблонов.");}
            var configs=new Dictionary<string,string>();
            foreach(var file in RemoteDocker.ConfigFiles)configs[file]=await infra.Config(state.HostId!,state.Ip!,inst.Id,new(file),ct);
            var template=new TemplateView(Guid.NewGuid().ToString("N"),request.Name.Trim(),new(inst.Name,inst.Password,inst.Mods,inst.MaxPlayers,inst.StartLayer),new());
            store.Save("template-data-"+template.Id,configs);
            lock(gate){state.Templates.Add(new(user.Id,template));Save();}return template;
        }
        finally{worker.Release();}
    }
    public OperationView StartTemplate(User user,string id,TemplateStart request)
    {
        lock(gate)
        {
            if(state.HostId==null || state.HostPhase!="ready")throw new InvalidOperationException("Сначала выберите и арендуйте хост во вкладке Хостинг.");
            var t=state.Templates.Find(t=>t.OwnerId==user.Id && t.Value.Id==id)?.Value??throw new UnauthorizedAccessException("Шаблон недоступен.");
            if(Existing(user,request.RequestId) is {} existing)return View(existing);
            var op=Start(user,new(request.RequestId,t.Settings.Name,t.Settings.Password,t.Settings.Mods,t.Settings.MaxPlayers,StartLayer:t.Settings.StartLayer));
            var instance=state.Instances.Single(i=>i.Id==state.Operations.Single(o=>o.Id==op.Id).InstanceId);instance.TemplateConfigs=store.Load<Dictionary<string,string>>("template-data-"+t.Id)??new(t.Configs);Save();return op;
        }
    }
    public void DeleteTemplate(User user,string id){lock(gate){if(state.Templates.RemoveAll(t=>t.OwnerId==user.Id && t.Value.Id==id)>0)store.Delete("template-data-"+id);Save();}}
    public void SetBudget(User user,BudgetPolicy policy)
    {
        if(policy.Amount<0 || policy.Amount>1000000 || !System.Text.RegularExpressions.Regex.IsMatch(policy.Currency,"^[A-Z]{3}$"))throw new ArgumentException("Проверьте сумму и валюту.");
        lock(gate){state.Budgets[user.Id]=policy;state.Notices.RemoveAll(n=>n.OwnerId==user.Id && n.Value.Code=="noticeBudget");Save();}
    }
    public void ReadNotices(User user){lock(gate){for(var i=0;i<state.Notices.Count;i++)if(state.Notices[i].OwnerId==user.Id)state.Notices[i]=state.Notices[i] with{Value=state.Notices[i].Value with{Read=true}};Save();}}
    public List<ConfigVersion> Versions(User user,string id,string file){lock(gate){Owned(user,id);return state.Versions.Where(v=>v.OwnerId==user.Id && v.Value.InstanceId==id && v.Value.File==file).Select(v=>v.Value with{Content=store.Load<string>("config-version-"+v.Value.Id)??v.Value.Content}).Reverse().ToList();}}
    public Task<string> RestoreVersion(User user,string id,string version,CancellationToken ct)
    {
        ConfigVersion value;lock(gate){Owned(user,id);value=state.Versions.Find(v=>v.OwnerId==user.Id && v.Value.InstanceId==id && v.Value.Id==version)?.Value??throw new UnauthorizedAccessException("Версия недоступна.");}
        return Config(user,id,new(value.File,store.Load<string>("config-version-"+value.Id)??value.Content),ct);
    }
    public async Task<Diagnostic> Diagnose(User user,string id,CancellationToken ct)
    {
        string phase;string? game,error;lock(gate){var inst=Owned(user,id);phase=inst.Phase;game=inst.GameState;error=inst.Error;}
        string logs="";try{logs=await Access(user,id,null,ct);}catch(Exception e) when(e is not OperationCanceledException){error??=e.Message;}
        return StartupDiagnosis.Evaluate(phase,game,error,logs.Length>32000?logs[^32000..]:logs);
    }
    public async Task TickFeatures(CancellationToken ct,DateTimeOffset? clock=null)
    {
        var now=clock??DateTimeOffset.UtcNow;
        lock(gate)
        {
            foreach(var inst in state.Instances.Where(i=>i.RecoveryNeeded && i.Phase is "running" or "error").ToList())
            {
                if(state.HostPhase!="ready" || state.HostId==null || state.Operations.Any(o=>o.InstanceId==inst.Id && o.Phase is "queued" or "running"))continue;
                if(inst.RecoveryAttempts.LastOrDefault() is {} last && last!=default && now<last.AddMinutes(Math.Min(5,Math.Pow(2,Math.Min(3,inst.RecoveryAttempts.Count-1)))))continue;
                if(inst.RecoveryAttempts.Count>=6)inst.RecoveryAttempts.RemoveAt(0);
                try{new Accounts(store).EnsureAccess(inst.OwnerId);inst.RecoveryAttempts.Add(now);Restart(new(inst.OwnerId,inst.OwnerName,false),inst.Id,new(Guid.NewGuid()));Notify(inst.OwnerId,"noticeRecovering",inst.Id,inst.Name);Save();}catch(InvalidOperationException){}
                catch(UnauthorizedAccessException){}
            }
            var report=CostAccounting.Calculate(state.Rentals,state.CostEvents,now);
            foreach(var entry in state.Budgets.Where(b=>b.Value.Amount>0))
            {
                var spent=report.Users.Where(r=>r.UserId==entry.Key && r.Currency==entry.Value.Currency).Sum(r=>r.Spent);
                if(spent>=entry.Value.Amount && !state.Notices.Any(n=>n.OwnerId==entry.Key && n.Value.Code=="noticeBudget")){Notify(entry.Key,"noticeBudget",null,spent.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+" "+entry.Value.Currency);Save();}
            }
        }
    }
}
