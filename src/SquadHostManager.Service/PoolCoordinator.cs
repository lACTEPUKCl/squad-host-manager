using System.Text.RegularExpressions;
using SquadHostManager.Core;

namespace SquadHostManager.Service;

public sealed partial class PoolCoordinator(Store store, IInfrastructure infra, IConfiguration? configuration = null)
{
    private readonly object gate = new();
    private readonly SemaphoreSlim worker = new(1, 1);
    private readonly PoolState state = LoadState(store);
    private static PoolState LoadState(Store store)
    {
        var state=store.Load<PoolState>("pool")??new();
        foreach(var record in store.Load<List<RentalRecord>>("rental-history-import")??[])
            if(!state.Rentals.Any(r=>r.Id==record.Id))state.Rentals.Add(record);
        store.Save("pool",state);
        return state;
    }
    private readonly bool rentalDisabled = configuration?.GetValue<bool>("Cherry:DisableRent") ?? false;
    private readonly bool releaseDisabled = configuration?.GetValue<bool>("Cherry:DisableRelease") ?? false;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> rconLocks = new();
    public event Action? Changed;
    public ServerDefaults Defaults(User user){lock(gate)return store.Load<ServerDefaults>("server-defaults-"+user.Id)??new(state.Instances.Where(i=>i.OwnerId==user.Id).OrderByDescending(i=>i.StartedAt).FirstOrDefault()?.Mods??"");}
    private Dictionary<string,string> CommonConfigs(User user)
    {
        var saved=Defaults(user);var configs=StandardConfigs.Copy();
        foreach(var pair in store.Load<Dictionary<string,string>>("game-standard-configs")??new())if(RemoteDocker.ConfigFiles.Contains(pair.Key))configs[pair.Key]=pair.Value;
        foreach(var pair in saved.Configs??new())configs[pair.Key]=pair.Value;
        if(saved.Admins!=null)configs["Admins.cfg"]=saved.Admins;
        configs["Server.cfg"]=StandardConfigs.SharedServer(configs["Server.cfg"]);
        return configs;
    }
    public string DefaultConfig(User user,ConfigRequest request)
    {
        if(!RemoteDocker.ConfigFiles.Contains(request.File) || request.Content?.Length>20000 || request.Content?.Contains('\0')==true)throw new ArgumentException("Недопустимый конфиг.");
        lock(gate)
        {
            if(request.Content==null)return CommonConfigs(user).GetValueOrDefault(request.File,"");
            var content=request.File=="Server.cfg"?StandardConfigs.SharedServer(request.Content):request.Content;
            if(request.File=="Server.cfg")SquadConfig.Validate(content);
            var before=CommonConfigs(user).GetValueOrDefault(request.File,"");if(before!=content)RememberCommonConfig(user,request.File,before);
            var prior=Defaults(user);var configs=new Dictionary<string,string>(prior.Configs??new());configs[request.File]=content;
            store.Save("server-defaults-"+user.Id,prior with{Configs=configs,Admins=request.File=="Admins.cfg"?content:prior.Admins});
            foreach(var instance in state.Instances.Where(i=>i.OwnerId==user.Id))
            {
                if(request.File=="Server.cfg"){instance.ServerConfig=content;if(int.TryParse(SquadConfig.Get(content,"MaxPlayers"),out var players))instance.MaxPlayers=players;}
            }
            Save();return content;
        }
    }
    private void RememberMods(User user,string mods){var prior=Defaults(user);store.Save("server-defaults-"+user.Id,prior with{Mods=mods});}

    public OperationView RetryHostPreparation(User user,RestartRequest request)
    {
        if(!user.IsAdmin)throw new UnauthorizedAccessException("Только владелец хостинга.");
        lock(gate)
        {
            if(Existing(user,request.RequestId) is {} old)return View(old);
            if(state.HostId==null || state.HostPhase=="deleting" || state.Operations.Any(o=>o.Phase is "queued" or "running"))throw new InvalidOperationException("Хост недоступен или операция ещё выполняется.");
            var op=new Operation{Id=request.RequestId.ToString("N"),UserId=user.Id,Kind="prepare",ExpectedHostId=state.HostId};
            state.HostError=null;state.Operations.Add(op);Save();return View(op);
        }
    }
    public async Task<OperationView> RentHost(User user, RentHostRequest request, CancellationToken ct)
    {
        new Accounts(store).EnsureRentalAccess(user.Id);
        lock(gate) { if(Existing(user,request.RequestId) is {} old)return View(old); if(rentalDisabled)throw new InvalidOperationException("Новая аренда отключена владельцем."); }
        var offer=(await infra.Locations(ct)).Find(r=>r.Slug==request.Region);
        if(offer==null || offer.Available<1 || offer.HourlyPrice!=request.HourlyPrice || offer.Currency!=request.Currency || offer.PrebuiltId!=request.PrebuiltId)throw new InvalidOperationException("Цена или наличие изменились. Обновите список городов.");
        lock(gate)
        {
            if(Existing(user,request.RequestId) is {} old)return View(old);
            if(state.HostId!=null || state.HostPhase!="empty" || state.Operations.Any(o=>o.Phase is "queued" or "running"))throw new InvalidOperationException("Хост уже существует или аренда выполняется.");
            new Accounts(store).EnsureRentalAccess(user.Id);
            store.Save("rental-region",request.Region);store.Save("rental-quote-"+state.Marker,request);
            var op=new Operation{Id=request.RequestId.ToString("N"),UserId=user.Id,RenterName=user.Name,Kind="rent"};
            state.Startup=null; HostProgress("hostQueued");
            state.Operations.Add(op);state.RentedByName=user.Name;state.LastStoppedBy=user.Id;state.LastEvent="Аренда хоста поставлена в очередь";Save();return View(op);
        }
    }
    public async Task SetRegion(User user,string region,CancellationToken ct)
    {
        new Accounts(store).EnsureRentalAccess(user.Id);
        var locations=await infra.Locations(ct);
        if(!locations.Any(r=>r.Slug==region))throw new ArgumentException("Выберите европейский город из списка Cherry Servers.");
        lock(gate)
        {
            if(state.HostId!=null || state.Operations.Any(o=>o.Phase is "queued" or "running"))throw new InvalidOperationException("Город нельзя менять при существующей аренде или заказе.");
            store.Save("rental-region",region);
        }
    }
    public async Task Adopt(User user, string hostId, CancellationToken ct)
    {
        if (!user.IsAdmin) throw new UnauthorizedAccessException("Только владелец пула подключает существующий хост.");
        if (!Regex.IsMatch(hostId, "^[0-9]+$")) throw new ArgumentException("Укажите Cherry server ID.");
        await worker.WaitAsync(ct);
        try
        {
            lock (gate)
            {
                if (state.HostId != null || state.Instances.Any(Occupied) || state.Operations.Any(o => o.Phase is "queued" or "running")) throw new InvalidOperationException("Подключение доступно только для пустого пула без операций.");
            }
            var host = await infra.GetHost(hostId, ct) ?? throw new InvalidOperationException("Cherry не видит этот хост.");
            if (host.Ip == null || !host.Ready) throw new InvalidOperationException("Хост ещё не готов.");
            // Do not modify the adopted host while verifying access.
            _ = await infra.Inventory(hostId, host.Ip, ct);
            lock (gate) { ObserveHost(host); state.HostId = hostId; state.Ip = host.Ip; state.HostPhase = "ready"; state.HostError = null; state.ConnectedByName = user.Name; state.LastEvent = "Существующий хост подключён к пулу"; Save(); }
        }
        finally { worker.Release(); }
    }
    public async Task Recover(User user, bool confirmedNoOrder, CancellationToken ct)
    {
        if (!user.IsAdmin) throw new UnauthorizedAccessException("Только владелец пула выполняет сверку.");
        await worker.WaitAsync(ct);
        try
        {
            if (state.HostPhase is "ordering" or "order-uncertain")
            {
                var found = await infra.FindHost(state.Marker, ct);
                lock (gate)
                {
                    if (found != null) { state.HostId = found.Id; state.Ip = found.Ip; state.HostPhase = "provisioning"; state.HostError = null; }
                    else if (confirmedNoOrder) { state.HostPhase = "empty"; state.HostError = null; }
                    else throw new InvalidOperationException("Хост ещё не найден. Проверьте в Cherry отсутствие выполняющегося заказа перед сбросом.");
                    Save();
                }
            }
            else if (state.HostPhase == "deleting" && state.HostId != null)
            {
                var found = await infra.GetHost(state.HostId, ct);
                lock (gate)
                {
                    if (found == null) { EndRental(false); state.HostId = null; state.Ip = null; state.HostPhase = "empty"; state.Instances.Clear(); state.Marker = "shm-" + Guid.NewGuid().ToString("N")[..16]; }
                    else throw new InvalidOperationException("Cherry ещё показывает хост. Необходимо дождаться завершения удаления.");
                    state.HostError = null; Save();
                }
            }
            else throw new InvalidOperationException("Пул не требует сверки заказа или удаления.");
        }
        finally { worker.Release(); }
    }
    private void HostProgress(string stage)
    {
        lock(gate)
        {
            var now=DateTimeOffset.UtcNow;var old=state.Startup;
            var events=old?.Events.ToList()??[];
            if(old?.Stage!=stage)
            {
                events.Add(new(now,stage));
                var operation=state.Operations.FirstOrDefault(o=>o.Phase=="running");
                var code=stage switch{"providerPreparing"=>"noticeHostPreparing","gameCacheDownloading"=>"noticeSquadDownloading","hostReady"=>"noticeHostReady",_=>null};
                if(operation?.Kind is "rent" or "prepare" && code!=null)Notify(operation.UserId,code,null,"Cherry "+state.HostId);
            }
            state.Startup=new(stage,old?.StartedAt??now,now,state.Rentals.Find(r=>r.Id==state.HostId) is {} rental ? rental.State+" / "+rental.ProviderStatus : null,events.TakeLast(100).ToList());Save();
        }
    }
    private void ObserveHost(RemoteHost host)
    {
        state.HostCheckedAt = DateTimeOffset.UtcNow; state.HostReachable = host.Ready;
        if(state.Startup!=null)state.Startup=state.Startup with {UpdatedAt=state.HostCheckedAt.Value,ProviderState=host.Rental==null?null:host.Rental.State+" / "+host.Rental.ProviderStatus};
        if (host.Rental is not {} record) return;
        var previous = state.Rentals.Find(r => r.Id == host.Id);
        if (previous != null)
        {
            record.Rates = previous.Rates.ToList(); record.EstimateIncomplete=previous.EstimateIncomplete;
            if (previous.Currency != record.Currency) { record.Rates = []; record.EstimateIncomplete=true; }
        }
        if(record.HourlyRate is {} rate && (record.Rates.Count==0 || record.Rates[^1].Rate!=rate))
            record.Rates.Add(new(record.Rates.Count==0 ? record.StartedAt ?? record.CheckedAt : record.CheckedAt, rate));
        state.Rentals.RemoveAll(r => r.Id == host.Id);state.Rentals.Add(record);
    }
    private void EndRental(bool estimated)
    {
        if(state.Rentals.Find(r=>r.Id==state.HostId) is {} record) { record.EndedAt=DateTimeOffset.UtcNow;record.EndTimeEstimated=estimated;record.State="cancelled"; }
        state.HostReachable=false;state.HostCheckedAt=DateTimeOffset.UtcNow;
    }
    private void Save() { if (state.HostId == null && state.HostPhase == "empty") { state.RentedByName = null; state.ConnectedByName = null; } RecordParticipants(); store.Save("pool", state); Changed?.Invoke(); }
    private void RecordParticipants()
    {
        if(state.HostId==null)return;
        var users=state.Instances.Where(Occupied).Select(i=>new CostParticipant(i.OwnerId,i.OwnerName)).DistinctBy(u=>u.Id).OrderBy(u=>u.Id).ToList();
        var previous=state.CostEvents.LastOrDefault(e=>e.HostId==state.HostId);
        if(users.Count==0)
        {
            if(previous!=null)users=previous.Users.ToList();
            else if(state.Operations.LastOrDefault(o=>o.Kind=="rent") is {} renter)users=[new(renter.UserId,renter.RenterName??state.RentedByName??renter.UserId)];
        }
        if(users.Count>0 && (previous==null || !previous.Users.SequenceEqual(users)))state.CostEvents.Add(new(DateTimeOffset.UtcNow,state.HostId,users));
    }
    public CostStatistics Statistics(User user,DateTimeOffset? from=null)
    {
        if(!user.IsAdmin)throw new UnauthorizedAccessException("Статистика доступна только администратору.");
        lock(gate)return CostAccounting.Calculate(state.Rentals,state.CostEvents,DateTimeOffset.UtcNow,from);
    }
    private static bool Occupied(Instance i) => i.Phase != "stopped";
    public PoolView Snapshot(User user)
    {
        lock(gate)
        {
            var rental=state.Rentals.LastOrDefault(r=>r.Id==state.HostId)?.Copy() ?? (user.IsAdmin && state.HostId==null ? state.Rentals.LastOrDefault()?.Copy():null);
            if(!user.IsAdmin && rental!=null)rental=rental with{ProviderAmount=null,Rates=[]};
            var error=user.IsAdmin?state.HostError:(state.HostPhase=="unavailable"?"Хост недоступен":null);
            var startup=state.Startup is {} current?current with{Events=current.Events.ToList(),Error=user.IsAdmin?current.Error:null}:null;
            var host=new HostView(state.HostId,state.Ip,state.HostPhase,error,state.RentedByName,state.ConnectedByName,rental,state.HostCheckedAt is {} checkedAt && DateTimeOffset.UtcNow-checkedAt<TimeSpan.FromSeconds(60) && state.HostReachable,startup);
            var instances=state.Instances.Where(i=>user.IsAdmin || i.OwnerId==user.Id).Select(i=>new InstanceView(i.Id,i.OwnerId,i.OwnerName,i.Name,i.Slot,i.Ports,i.Phase,i.OwnerId==user.Id,i.Error,i.GameState,i.DownloadPercent,i.MaxPlayers,i.Mods,i.StartedAt,QueuePosition(i.Id),i.OwnerId==user.Id?i.Password:null,i.StartLayer,i.ProgressKey)).ToList();
            var pending=state.Operations.Any(o=>o.Phase is "queued" or "running");
            var canRent=!rentalDisabled && new Accounts(store).CanRent(user.Id);
            return new(host,instances,!releaseDisabled && state.HostPhase=="ready" && !state.Instances.Any(Occupied) && state.LastStoppedBy==user.Id && !pending,pending,user.IsAdmin?state.LastEvent:null,!(rentalDisabled && state.HostId==null) && state.HostPhase is not ("deleting" or "order-uncertain" or "unavailable"),!releaseDisabled,user.IsAdmin?state.Rentals.Select(r=>r.Copy()).ToList():[],state.CpuCapacity,state.Instances.Count(Occupied),state.Instances.Count(i=>i.OwnerId!=user.Id && i.Phase is not ("stopped" or "stopping")),state.Instances.Count(i=>i.OwnerId!=user.Id && i.Phase=="stopping"),canRent);
        }
    }
    private int? QueuePosition(string instanceId)
    {
        var pending=state.Operations.Where(o=>o.Phase is "queued" or "running").ToList();
        var position=pending.FindIndex(o=>o.InstanceId==instanceId);
        return position<0?null:position+1;
    }
    private Operation? Existing(User user, Guid requestId)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("Нужен уникальный RequestId.");
        var op = state.Operations.Find(o => o.Id == requestId.ToString("N"));
        if (op != null && op.UserId != user.Id) throw new UnauthorizedAccessException("Запрос принадлежит другому пользователю.");
        return op;
    }
    private static OperationView View(Operation o) => new(o.Id, o.Kind, o.Phase, o.Error);
    public OperationView Start(User user, StartRequest request)
    {
        lock (gate)
        {
            var existing = Existing(user, request.RequestId); if (existing != null) return View(existing);
            if(state.HostId==null)new Accounts(store).EnsureRentalAccess(user.Id);
            if (rentalDisabled && state.HostId == null) throw new InvalidOperationException("Владелец запретил новую аренду. Сначала подключите существующий хост.");
            if (state.HostPhase is "deleting" or "order-uncertain" or "unavailable") throw new InvalidOperationException("Хост закрывается или результат аренды требует проверки.");
            ValidateSettings(request.Name, request.Password, request.Mods, request.MaxPlayers);
            if(request.StartLayer!=null && !ValidStartLayer(request.StartLayer))throw new ArgumentException("Неверное название стартового слоя.");
            Instance? inst = null;
            if (request.InstanceId != null)
            {
                inst = state.Instances.Find(i => i.Id == request.InstanceId && i.OwnerId == user.Id) ?? throw new UnauthorizedAccessException("Можно запустить только свой сервер.");
                if (inst.Phase != "stopped" || state.Operations.Any(o => o.InstanceId == inst.Id && o.Phase is "queued" or "running")) throw new InvalidOperationException("Сервер уже запущен или занят операцией.");
            }
            EnforceQuota(user,inst);
            var slot = PortSet.Allocate(state.Instances.Where(Occupied).Select(i => i.Slot), [],state.CpuCapacity);
            if (inst == null) { inst = new Instance { OwnerId = user.Id, OwnerName = user.Name }; state.Instances.Add(inst); }
            inst.Name = request.Name.Trim(); inst.Password = request.Password ?? inst.Password;
            if(request.StartLayer!=null)inst.StartLayer=request.StartLayer;
            inst.Mods = string.Join(' ', Regex.Matches(request.Mods, @"\d+").Select(m => m.Value).Distinct());
            RememberMods(user,request.Mods);
            inst.ReadyNotified=false; inst.StartedAt = DateTimeOffset.UtcNow; inst.MaxPlayers = Defaults(user).Configs?.TryGetValue("Server.cfg",out var commonMain)==true && int.TryParse(SquadConfig.Get(commonMain,"MaxPlayers"),out var commonPlayers)?commonPlayers:request.MaxPlayers; inst.Slot = slot; inst.Ports = PortSet.ForSlot(slot); inst.Phase = "queued"; inst.Error = null; inst.GameState = null; inst.DownloadPercent = null;
            var op = new Operation { Id = request.RequestId.ToString("N"), UserId = user.Id, InstanceId = inst.Id, Kind = "start", ExpectedHostId=state.HostId };
            state.Operations.Add(op); state.LastEvent = $"{user.Name}: запуск {inst.Name} поставлен в очередь"; Save(); return View(op);
        }
    }
    public OperationView Stop(User user, StopRequest request)
    {
        lock (gate)
        {
            if (request.ReleaseIfLast && releaseDisabled) throw new InvalidOperationException("Прекращение аренды отключено владельцем на время проверки.");
            var existing = Existing(user, request.RequestId); if (existing != null) return View(existing);
            var owned = state.Instances.Where(i => i.OwnerId == user.Id && Occupied(i)).ToList();
            if (request.InstanceId == null && owned.Count > 1) throw new ArgumentException("Выберите сервер для отключения.");
            var inst = request.InstanceId == null ? owned.SingleOrDefault() : state.Instances.Find(i => i.Id == request.InstanceId && i.OwnerId == user.Id) ?? throw new UnauthorizedAccessException("Можно отключить только свой сервер.");
            if (inst?.Phase == "stopped") throw new InvalidOperationException("Сервер уже отключён.");
            if (inst == null && !(request.ReleaseIfLast && Snapshot(user).CanRelease)) throw new InvalidOperationException("У вас нет активного сервера или права завершить аренду.");
            if (state.Operations.Any(o => o.InstanceId == (inst == null ? "" : inst.Id) && o.Kind == "stop" && o.Phase is "queued" or "running")) throw new InvalidOperationException("Отключение уже выполняется.");
            var op = new Operation { Id = request.RequestId.ToString("N"), UserId = user.Id, InstanceId = inst?.Id ?? "", Kind = "stop", Release = request.ReleaseIfLast };
            if (inst != null) { inst.RecoveryNeeded=false; inst.Phase = "stopping"; inst.GameState = null; inst.DownloadPercent = null; }
            state.Operations.Add(op); Save(); return View(op);
        }
    }
    private static bool ValidStartLayer(string value)=>value=="" || value.Length<=240 && Regex.IsMatch(value,@"^(?:[A-Za-z0-9_]{1,120}|/(?:[A-Za-z0-9_]+/)+[A-Za-z0-9_]+)$");
    private static void ValidateSettings(string name, string? password, string mods, int maxPlayers)
    {
        if (name.Trim().Length is < 3 or > 100 || name.Any(char.IsControl) || password?.Length > 100 || password?.Any(char.IsControl) == true) throw new ArgumentException("Проверьте название и пароль.");
        if (mods.Length > 2048 || mods.Any(c => !char.IsAsciiDigit(c) && !char.IsWhiteSpace(c) && c != ',')) throw new ArgumentException("Моды: только числовые Workshop ID через пробел или запятую.");
        if (maxPlayers is < 2 or > 100) throw new ArgumentException("Игроки: 2–100.");
    }
    public void Settings(User user, string id, ServerSettingsRequest request)
    {
        lock (gate)
        {
            var inst = state.Instances.Find(i => i.Id == id && i.OwnerId == user.Id) ?? throw new UnauthorizedAccessException("Можно настраивать только свой сервер.");
            if (inst.Phase is not ("running" or "stopped" or "error") || state.Operations.Any(o => o.InstanceId == id && o.Phase is "queued" or "running")) throw new InvalidOperationException("Дождитесь завершения операции.");
            ValidateSettings(request.Name, request.Password, request.Mods, request.MaxPlayers);
            if(request.StartLayer!=null && !ValidStartLayer(request.StartLayer))throw new ArgumentException("Неверное название стартового слоя.");
            if(request.StartLayer!=null)inst.StartLayer=request.StartLayer;
            RememberMods(user,request.Mods);
            inst.Name = request.Name.Trim(); inst.Mods = string.Join(' ', Regex.Matches(request.Mods, @"\d+").Select(m => m.Value).Distinct()); inst.MaxPlayers = request.MaxPlayers;
            if (request.Password != null) inst.Password = request.Password ?? inst.Password;
            Save();
        }
    }
    public async Task ProcessNext(CancellationToken ct)
    {
        await worker.WaitAsync(ct);
        try
        {
            Operation? op;
            lock (gate) { op = state.Operations.FirstOrDefault(o => o.Phase is "queued" or "running"); if (op == null) return; op.Phase = "running"; Save(); }
            try
            {
                if (op.Kind is "rent" or "prepare") await EnsureHost(op, ct);
                else if (op.Kind == "start") await StartRemote(op, ct);
                else if (op.Kind == "restart") await RestartRemote(op, ct);
                else await StopRemote(op, ct);
                lock (gate) { op.Phase = "done"; Save(); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                lock (gate)
                {
                    op.Phase = "failed"; op.Error = e.Message;if(op.Kind is "rent" or "prepare")Notify(op.UserId,"noticeHostFailed",null,"Cherry "+state.HostId);
                    if(state.Startup?.Stage is not (null or "hostReady")) { HostProgress("hostFailed"); state.Startup=state.Startup! with {Error=e.Message}; state.HostError=e.Message; }
                    var inst = state.Instances.Find(i => i.Id == op.InstanceId);
                    // Failed starts retain reservations: Docker may already have started.
                    if (inst != null && inst.Phase != "stopped") { inst.Phase = "error"; inst.Error = e.Message; }
                    state.LastEvent = "Ошибка: " + e.Message; Save();
                }
            }
        }
        finally { worker.Release(); }
    }
    private async Task EnsureHost(Operation operation, CancellationToken ct)
    {
        if(operation.ExpectedHostId!=null && state.HostId!=operation.ExpectedHostId)throw new InvalidOperationException("Хост изменился или аренда закончилась. Запуск на другом хосте не выполнялся.");
        if (state.HostPhase == "deleting") throw new InvalidOperationException("Аренда прекращается.");
        lock(gate){state.HostError=null;Save();}
        var track=state.HostPhase!="ready" || operation.Kind is "rent" or "prepare" || state.Startup?.Stage=="hostFailed";
        if(track && state.Startup?.Stage is null or "hostReady" or "hostFailed") { lock(gate) state.Startup=null; HostProgress("hostQueued"); }
        RemoteHost? host;
        if (state.HostId == null)
        {
            host = await infra.FindHost(state.Marker, ct);
            if (host == null)
            {
                if (state.HostPhase is "ordering" or "order-uncertain") throw new InvalidOperationException("Результат предыдущего заказа неизвестен. Проверьте Cherry; повторный заказ заблокирован.");
                new Accounts(store).EnsureRentalAccess(operation.UserId);
                lock (gate) { state.HostPhase = "ordering"; state.HostError = null; state.RentedByName = operation.RenterName ?? state.Instances.FirstOrDefault(i => i.Id == operation.InstanceId)?.OwnerName; state.ConnectedByName = null; Save(); }
                if(track)HostProgress("hostOrdering");
                try { host = await infra.Rent(state.Marker, ct); }
                catch (RentalRejectedException e) { lock (gate) { state.HostPhase = "empty"; state.HostError = e.Message; Save(); } throw; }
                catch { lock (gate) { state.HostPhase = "order-uncertain"; state.HostError = "Заказ мог быть принят Cherry. Повторная аренда заблокирована до сверки."; Save(); } throw; }
            }
            lock (gate) { state.HostId = host.Id; ObserveHost(host); state.Ip=host.Ip; state.HostPhase = "provisioning"; Save(); }
            if(track)HostProgress("providerPreparing");
        }
        var deadline = DateTimeOffset.UtcNow.AddMinutes(40);
        while (true)
        {
            host = await infra.GetHost(state.HostId!, ct);
            if (host == null) throw new InvalidOperationException("Арендованный хост исчез из Cherry. Автоматически заменять его запрещено.");
            lock(gate) { ObserveHost(host); state.Ip=host.Ip; Save(); }
            if(track)HostProgress("providerPreparing");
            if (host.Ready && host.Ip != null) break;
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("Cherry ещё не подготовил хост. Повторите запуск позже.");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        lock (gate) { ObserveHost(host); state.Ip = host.Ip; if (state.HostPhase != "ready") state.HostPhase = "preparing"; Save(); }
        if(track)
        {
            var preparation=infra.Prepare(host.Id,host.Ip!,HostProgress,ct);
            while(!preparation.IsCompleted)
            {
                if(await Task.WhenAny(preparation,Task.Delay(TimeSpan.FromSeconds(5),ct))==preparation)break;
                ct.ThrowIfCancellationRequested();
                try
                {
                    if(state.Startup?.Stage is "gameCacheDownloading" or "gameCacheVerifying" && await infra.PreparationStatus(host.Id,host.Ip!,ct) is {} download)
                    {
                        HostProgress(download.State);
                        lock(gate){state.Startup=state.Startup! with {DownloadPercent=download.DownloadPercent};Save();}
                    }
                    var current=await infra.GetHost(host.Id,ct);
                    if(current!=null)lock(gate){ObserveHost(current);state.Ip=current.Ip??state.Ip;if(state.Startup!=null)state.Startup=state.Startup with {UpdatedAt=DateTimeOffset.UtcNow,ProviderState=current.Rental==null?null:current.Rental.State+" / "+current.Rental.ProviderStatus};Save();}
                }
                catch(Exception) when(!ct.IsCancellationRequested) { }
            }
            await preparation;
        }
        else await infra.Prepare(host.Id, host.Ip!, ct);
        var capacity=(await infra.CpuSets(host.Id,host.Ip!,ct)).Count;
        lock (gate) { state.CpuCapacity=capacity; state.HostPhase = "ready"; state.HostError = null; Save(); }
        if(track)HostProgress("hostReady");
    }
    private async Task StartRemote(Operation op, CancellationToken ct)
    {
        await EnsureHost(op, ct);
        if(store.Load<string>("game-standard-config-host")!=state.HostId)
        {
            var downloaded=await infra.DownloadedConfigs(state.HostId!,state.Ip!,ct);
            if(downloaded.Count>0){store.Save("game-standard-configs",downloaded);store.Save("game-standard-config-host",state.HostId!);}
        }
        Instance inst; lock (gate)
        {
            inst = state.Instances.Single(i => i.Id == op.InstanceId);
            if(inst.TemplateConfigs is {} imported)foreach(var pair in imported)DefaultConfig(new(inst.OwnerId,inst.OwnerName,false),new(pair.Key,pair.Value));
            inst.CommonConfigs=CommonConfigs(new(inst.OwnerId,inst.OwnerName,false));
            inst.ServerConfig=inst.CommonConfigs["Server.cfg"];
            if(inst.TemplateConfigs?.Remove("Server.cfg",out var configured)==true){SquadConfig.Validate(configured);inst.ServerConfig=configured;Save();}
        }
        var cpuSets = await infra.CpuSets(state.HostId!,state.Ip!,ct);
        lock(gate)state.CpuCapacity=cpuSets.Count;
        var inventory = await infra.Inventory(state.HostId!, state.Ip!, ct);
        var own = inventory.Find(i => i.Id == inst.Id);
        if (own?.Running != true)
        {
            var bound = await infra.BoundPorts(state.HostId!, state.Ip!, ct);
            foreach (var item in inventory.Where(i => i.Id != inst.Id)) bound.UnionWith(item.Ports);
            // Include every local reservation, across all protocols, even stopped external containers.
            lock (gate)
            {
                var other = state.Instances.Where(i => Occupied(i) && i.Id != inst.Id).ToList();
                bound.UnionWith(other.SelectMany(i => i.Ports.All));
                if (inst.Slot >= cpuSets.Count || inst.Ports.All.Any(bound.Contains))
                {
                    inst.Slot = PortSet.Allocate(other.Select(i => i.Slot), bound,cpuSets.Count); inst.Ports = PortSet.ForSlot(inst.Slot);
                }
                inst.CpuSet=cpuSets[inst.Slot];
                inst.Phase = "starting"; Save();
            }
            await infra.Start(state.HostId!, state.Ip!, inst, ct);
        }
        if(inst.TemplateConfigs is {} configs)
        {
            await infra.Stop(state.HostId!,state.Ip!,inst.Id,ct);
            foreach(var entry in configs)await infra.Config(state.HostId!,state.Ip!,inst.Id,new(entry.Key,entry.Value),ct);
            await RestoreSavedFiles(inst,ct);
            await infra.Start(state.HostId!,state.Ip!,inst,ct);
            lock(gate)inst.TemplateConfigs=null;
        }
        lock (gate) { inst.Phase = "running"; inst.Error = null; state.LastEvent = $"{inst.Name}: контейнер запущен, игра загружается"; Save(); }
    }
    public OperationView Restart(User user, string id, RestartRequest request)
    {
        lock (gate)
        {
            var existing = Existing(user, request.RequestId); if (existing != null) return View(existing);
            var inst = state.Instances.Find(i => i.Id == id && i.OwnerId == user.Id) ?? throw new UnauthorizedAccessException("Можно перезапустить только свой сервер.");
            if (inst.Phase is not ("running" or "error") || state.HostPhase != "ready" || state.Operations.Any(o => o.InstanceId == id && o.Phase is "queued" or "running")) throw new InvalidOperationException("Дождитесь завершения текущей операции.");
            var op = new Operation { Id = request.RequestId.ToString("N"), UserId = user.Id, InstanceId = id, Kind = "restart", ExpectedHostId=state.HostId };
            inst.Error=null;inst.ReadyNotified=false;inst.StartedAt = DateTimeOffset.UtcNow; inst.Phase = "restarting"; inst.GameState = null; inst.DownloadPercent = null; state.Operations.Add(op); Save(); return View(op);
        }
    }
    private async Task RestartRemote(Operation op, CancellationToken ct)
    {
        Instance inst; lock (gate) inst = state.Instances.Single(i => i.Id == op.InstanceId);
        await infra.Stop(state.HostId!, state.Ip!, inst.Id, ct);
        await StartRemote(op, ct);
    }
    private async Task StopRemote(Operation op, CancellationToken ct)
    {
        Instance? inst; lock (gate) inst = state.Instances.Find(i => i.Id == op.InstanceId);
        if (state.HostId == null)
        {
            if (state.HostPhase != "empty") throw new InvalidOperationException("Нельзя подтвердить отсутствие арендованного хоста.");
        }
        else
        {
            var remote = await infra.GetHost(state.HostId, ct);
            if (remote == null)
            {
                lock (gate)
                {
                    if (state.HostPhase != "deleting") throw new InvalidOperationException("Хост недоступен в Cherry: необходима сверка администратором.");
                    state.HostId = null; state.Ip = null; state.HostPhase = "empty"; state.HostError = null; state.Instances.Clear();
                    state.Marker = "shm-" + Guid.NewGuid().ToString("N")[..16]; state.LastEvent = "Cherry подтвердил прекращение аренды после восстановления"; Save(); return;
                }
            }
            if (state.Ip == null) throw new InvalidOperationException("Хост пока не готов; повторите отключение после подготовки.");
            if(inst!=null)await PreserveMatchArchive(inst,op.Id,ct);
            if (inst != null && inst.Phase != "stopped") await infra.Stop(state.HostId, state.Ip, inst.Id, ct);
        }
        lock (gate)
        {
            if (inst != null) { inst.RecoveryNeeded=false; inst.Phase = "stopped"; inst.Error = null; inst.GameState = null; inst.DownloadPercent = null; Notify(inst.OwnerId,"noticeServerStopped",inst.Id,inst.Name); }
            state.LastStoppedBy = op.UserId; state.LastEvent = "Ваш сервер отключён"; Save();
            if (!op.Release || state.Instances.Any(Occupied) || state.Operations.Any(o => o.Kind == "start" && o.Phase == "queued")) return;
            state.HostPhase = "deleting"; Save(); // rejects all new starts before remote inspection
        }
        if (state.HostId == null) return;
        var deletionRequested = false;
        try
        {
            var inventory = await infra.Inventory(state.HostId, state.Ip!, ct);
            if (inventory.Any(i => i.Running)) throw new InvalidOperationException("На хосте есть работающие контейнеры. Аренда сохранена.");
            // A retry after a crash can observe an already deleted host.
            if (await infra.GetHost(state.HostId, ct) != null) { deletionRequested = true; await infra.Delete(state.HostId, ct); }
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (await infra.GetHost(state.HostId, ct) == null)
                {
                    lock (gate) { EndRental(false); state.HostId = null; state.Ip = null; state.HostPhase = "empty"; state.HostError = null; state.Instances.Clear(); state.Marker = "shm-" + Guid.NewGuid().ToString("N")[..16]; state.LastEvent = "Аренда хоста прекращена"; Save(); }
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            throw new InvalidOperationException("Cherry ещё не подтвердил удаление. Новая аренда заблокирована.");
        }
        catch { lock (gate) { if (!deletionRequested) state.HostPhase = "ready"; state.HostError = deletionRequested ? "Удаление не подтверждено. Запуск заблокирован; требуется сверка." : "Аренда сохранена: нельзя подтвердить, что хост пуст."; Save(); } throw; }
    }
    public async Task Reconcile(CancellationToken ct)
    {
        if (!await worker.WaitAsync(0, ct)) return;
        try
        {
            if (state.HostId == null || state.Ip == null || state.HostPhase is not ("ready" or "unavailable")) return;
            List<RemoteInstance> inventory;
            try
            {
                var host = await infra.GetHost(state.HostId, ct);
                if (host == null)
                {
                    lock (gate)
                    {
                        EndRental(true); state.HostId = null; state.Ip = null; state.HostPhase = "empty"; state.HostError = null;
                        state.Instances.Clear(); state.LastStoppedBy = null; state.Marker = "shm-" + Guid.NewGuid().ToString("N")[..16]; state.LastEvent = "Аренда завершена в Cherry Servers"; Save();
                    }
                    return;
                }
                lock (gate) ObserveHost(host);
                if (!host.Ready || host.Ip == null) throw new IOException("Игровой хост ещё не готов или отключён у провайдера.");
                lock (gate) { state.Ip = host.Ip; state.HostPhase="ready"; }
                inventory = await infra.Inventory(state.HostId, host.Ip, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                lock (gate)
                {
                    state.HostReachable=false; state.HostPhase="unavailable";
                    foreach (var inst in state.Instances.Where(i => i.Phase == "running")) { inst.GameState = "gameUnavailable"; inst.DownloadPercent = null; }
                    Save();
                }
                throw;
            }
            List<Instance> running;
            lock (gate) running = state.Instances.Where(i => i.Phase is "running" or "error" && inventory.Any(r => r.Id == i.Id && r.Running)).ToList();
            foreach (var inst in running)
            {
                GameStatus? status;
                try { status = await infra.Probe(state.HostId, state.Ip, inst, ct); }
                catch (Exception) when (!ct.IsCancellationRequested) { status = new("gameUnavailable"); }
                if (status != null) lock (gate) { inst.GameState = status.State; inst.DownloadPercent = status.DownloadPercent;inst.ProgressKey=status.ProgressKey;if(status.State=="gameFailed"){if(!inst.RecoveryNeeded)Notify(inst.OwnerId,"noticeCrashed",inst.Id,inst.Name);inst.RecoveryNeeded=true;}if(status.State=="gameReady"){inst.RecoveryNeeded=false;inst.Phase="running";inst.Error=null;if(!inst.ReadyNotified){Notify(inst.OwnerId,"noticeReady",inst.Id,inst.Name);inst.ReadyNotified=true;}} }
            }
            lock (gate)
            {
                foreach (var inst in state.Instances.Where(i => i.Phase == "running"))
                    if (!inventory.Any(i => i.Id == inst.Id && i.Running)) { inst.Phase = "error"; inst.RecoveryNeeded=true; inst.Error = "Контейнер остановился. Порты сохранены до явного отключения."; Notify(inst.OwnerId,"noticeCrashed",inst.Id,inst.Name); }
                Save();
            }
        }
        finally { worker.Release(); }
    }
    public async Task<string> Access(User user, string id, string? command, CancellationToken ct)
    {
        Instance inst; string hostId, ip;
        lock (gate)
        {
            inst = state.Instances.Find(i => i.Id == id && i.OwnerId == user.Id) ?? throw new UnauthorizedAccessException("Можно управлять только своим сервером.");
            if (state.HostPhase != "ready" || (command != null ? inst.Phase != "running" : inst.Phase is not ("running" or "starting" or "restarting" or "error"))) throw new InvalidOperationException("Сервер ещё не запущен.");
            hostId = state.HostId!; ip = state.Ip!;
        }
        if (command != null && (command.Length is < 1 or > 1024 || command.Any(char.IsControl))) throw new ArgumentException("Недопустимая RCON-команда.");
        if (command == null) return await infra.Logs(hostId, ip, id, ct);
        var commandLock = rconLocks.GetOrAdd(id, _ => new(1, 1)); await commandLock.WaitAsync(ct);
        try { return await infra.Command(hostId, ip, inst, command, ct); } finally { commandLock.Release(); }
    }
    public async Task<string> Config(User user, string id, ConfigRequest request, CancellationToken ct)
    {
        if(!RemoteDocker.ConfigFiles.Contains(request.File) || request.Content?.Length>20000 || request.Content?.Contains('\0')==true)throw new ArgumentException("Недопустимый конфиг.");
        if(request.File=="Server.cfg" && request.Content!=null)SquadConfig.Validate(request.Content);
        await worker.WaitAsync(ct);
        try
        {
            Instance inst; lock (gate) inst = state.Instances.Find(i => i.Id == id && i.OwnerId == user.Id && i.Phase is "running" or "stopped" or "error") ?? throw new UnauthorizedAccessException("Настройки доступны только для своего запущенного сервера.");
            if(request.File=="Server.cfg" && request.Content==null)
            {
                var content=inst.ServerConfig??await infra.Config(state.HostId!,state.Ip!,inst.Id,request,ct);
                lock(gate){inst.ServerConfig=content;Save();}
                return SquadConfig.Identity(content,inst.Name,inst.Password,inst.MaxPlayers);
            }
            if(request.Content!=null)
            {
                var before=await infra.Config(state.HostId!,state.Ip!,inst.Id,new(request.File),ct);
                lock(gate){var version=Guid.NewGuid().ToString("N");store.Save("config-version-"+version,before);state.Versions.Add(new(user.Id,new(version,id,request.File,DateTimeOffset.UtcNow,"")));TrimVersions(id,request.File);Save();}
            }
            var result=await infra.Config(state.HostId!, state.Ip!, inst.Id, request, ct);
            if(request.File=="Server.cfg" && request.Content!=null)
            {
                DefaultConfig(user,new("Server.cfg",request.Content));
                lock(gate){inst.Name=SquadConfig.Get(request.Content,"ServerName")??inst.Name;inst.Password=SquadConfig.Get(request.Content,"ServerPassword")??inst.Password;if(int.TryParse(SquadConfig.Get(request.Content,"MaxPlayers"),out var players))inst.MaxPlayers=players;Save();}
                return result;
            }
            if(request.Content!=null)DefaultConfig(user,request);
            else if(request.File=="Admins.cfg" && Defaults(user).Admins==null)DefaultConfig(user,new("Admins.cfg",result));
            if(request.Content==null || inst.Phase!="running")return result;
            var commandLock=rconLocks.GetOrAdd(inst.Id,_=>new(1,1));
            try
            {
                await commandLock.WaitAsync(ct);
                try
                {
                    var reply=await infra.Command(state.HostId!,state.Ip!,inst,"AdminReloadServerConfig",ct);
                    if(reply.TrimStart().StartsWith("ERROR",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException(reply);
                    return "Конфиг сохранён. AdminReloadServerConfig выполнен.\n"+reply;
                }
                finally{commandLock.Release();}
            }
            catch(Exception e) when(e is not OperationCanceledException)
            {throw new InvalidOperationException("Конфиг сохранён, но применить его через AdminReloadServerConfig не удалось: "+e.Message,e);}
        }
        finally { worker.Release(); }
    }
    private Instance FileAccess(User user, string id)
    {
        lock (gate)
        {
            var inst = state.Instances.Find(i => i.Id == id && i.OwnerId == user.Id) ?? throw new UnauthorizedAccessException("Можно скачать только свои файлы.");
            if (state.HostPhase != "ready" || inst.Phase is not ("running" or "stopped" or "error")) throw new InvalidOperationException("Дождитесь готовности хоста и завершения операции.");
            return inst;
        }
    }
    public async Task<List<ServerFile>> Files(User user, string id, CancellationToken ct)
    {
        await worker.WaitAsync(ct);
        try { var inst = FileAccess(user, id); return await infra.Files(state.HostId!, state.Ip!, inst.Id, ct); }
        finally { worker.Release(); }
    }
    public async Task<byte[]> Download(User user, string id, string path, CancellationToken ct)
    {
        await worker.WaitAsync(ct);
        try { var inst = FileAccess(user, id); return await infra.Download(state.HostId!, state.Ip!, inst.Id, path, ct); }
        finally { worker.Release(); }
    }
    public async Task<Stream> ArchiveStream(User user,string id,CancellationToken ct)
    {
        await worker.WaitAsync(ct);
        try
        {
            FileAccess(user,id);
            var stream=await infra.ArchiveStream(state.HostId!,state.Ip!,id,ct);lock(gate){Notify(user.Id,"noticeArchive",id,Owned(user,id).Name);Save();}return stream;
        }
        finally{worker.Release();}
    }
    public async Task<byte[]> Archive(User user, string id, CancellationToken ct)
    {
        await worker.WaitAsync(ct);
        try
        {
            lock (gate) if (!state.Instances.Any(i => i.Id == id && i.OwnerId == user.Id && i.Phase is "running" or "stopped" or "error")) throw new UnauthorizedAccessException("Архив доступен только для своего сервера.");
            return await infra.Archive(state.HostId!, state.Ip!, id, ct);
        }
        finally { worker.Release(); }
    }
}
