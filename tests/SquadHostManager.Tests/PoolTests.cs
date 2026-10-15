using SquadHostManager.Core;
using SquadHostManager.Service;
using Microsoft.Extensions.Configuration;

namespace SquadHostManager.Tests;

public sealed class Fixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "shm-tests-" + Guid.NewGuid().ToString("N"));
    public Store Store { get; }
    public FakeInfrastructure Infra { get; } = new();
    public PoolCoordinator Pool { get; }
    public Fixture() { Store = new(Directory);for(var i=0;i<33;i++)Store.Save("access-policy-user"+i,new AccessPolicy(null,16,true)); Pool = new(Store, Infra); }
    public void Dispose() { Store.Dispose(); System.IO.Directory.Delete(Directory, true); }
    public static User User(int id) => new("user" + id, "User " + id, false);
    public OperationView Start(int id, Guid? request = null) => Pool.Start(User(id), new(request ?? Guid.NewGuid(), "Server " + id, "", "", 100));
    public async Task Drain(int n) { for (var i = 0; i < n; i++) await Pool.ProcessNext(default); }
}
public sealed class FakeInfrastructure : IInfrastructure
{
    public Dictionary<string,string> StockConfigs=[];
    public Task<Dictionary<string,string>> DownloadedConfigs(string hostId,string ip,CancellationToken ct)=>Task.FromResult(new Dictionary<string,string>(StockConfigs));
    public BalanceView BalanceValue=new(28.31m,"EUR",0,DateTimeOffset.UtcNow);
    public bool BalanceFails;
    public int BalanceReads;
    public Task<BalanceView> Balance(CancellationToken ct){BalanceReads++;if(BalanceFails)throw new IOException();return Task.FromResult(BalanceValue);}
    public TaskCompletionSource? PrepareEntered, PrepareBlock;
    public bool FailPrepare;
    public List<RentalLocation> RentalLocations { get; } = [];
    public Task<List<RentalLocation>> Locations(CancellationToken ct) => Task.FromResult(RentalLocations.ToList());
    public int Rents, Deletes, Starts, Stops;
    public bool FailRentAfterCreating, FailStartAfterCreating, FailInventory;
    public bool RejectRent;
    public List<string>? CpuLayout;
    public Task<List<string>> CpuSets(string id,string ip,CancellationToken ct)=>Task.FromResult(CpuLayout??Enumerable.Range(0,16).Select(i=>$"{i},{i+16}").ToList());
    public List<string> Commands=[];
    public List<(string Instance,string Command)> InstanceCommands=[];
    public HashSet<string> FailCommandsFor=[];
    public GameStatus? GameStatus;
    public MetricSample Metric=new(DateTimeOffset.UtcNow,12,1048576,12884901888,1024,1000000,2,"AlBasrah_AAS_v1");
    public Task<MetricSample> Metrics(string id,string ip,Instance instance,CancellationToken ct)=>Task.FromResult(Metric);
    public List<ServerFile> ServerFiles=[];
    public Task<List<ServerFile>> Files(string id,string ip,string instance,CancellationToken ct)=>Task.FromResult(ServerFiles);
    public byte[] ArchiveData=[];
    public bool FailArchive;
    public List<string> ArchiveOrder=[];
    public int RestoredFiles;
    public Task RestoreFiles(string id,string ip,string instance,string path,CancellationToken ct){RestoredFiles++;return Task.CompletedTask;}
    public string LogText="logs";
    public bool FailProbe;
    public RemoteHost? Host;
    public List<RemoteInstance> Containers = [];
    public HashSet<int> ExtraPorts = [];
    public Task<RemoteHost?> FindHost(string marker, CancellationToken ct) => Task.FromResult(Host);
    public Task<RemoteHost> Rent(string marker, CancellationToken ct)
    {
        if (RejectRent) throw new RentalRejectedException("Out of stock");
        Rents++; Host = new("123", "192.0.2.1", true);
        if (FailRentAfterCreating) throw new IOException("Lost response"); return Task.FromResult(Host);
    }
    public Task<RemoteHost?> GetHost(string id, CancellationToken ct) => Task.FromResult(Host);
    public Task Prepare(string id, string ip, CancellationToken ct) => Task.CompletedTask;
    public Task Prepare(string id, string ip, Action<string> progress, CancellationToken ct)
    {
        progress("sshConnecting");progress("sshConnected");progress("dockerPreparing");progress("imageDownloading");PrepareEntered?.TrySetResult();
        if(FailPrepare)throw new IOException("Preparation failed");return PrepareBlock?.Task??Task.CompletedTask;
    }
    public Task<List<RemoteInstance>> Inventory(string id, string ip, CancellationToken ct)
    { if (FailInventory) throw new IOException("SSH unavailable"); return Task.FromResult(Containers.ToList()); }
    public Task<HashSet<int>> BoundPorts(string id, string ip, CancellationToken ct) => Task.FromResult(ExtraPorts.ToHashSet());
    public Task Start(string id, string ip, Instance inst, CancellationToken ct)
    {
        foreach(var pair in inst.CommonConfigs??new())Configurations[(inst.Id,pair.Key)]=pair.Key=="Server.cfg"?SquadConfig.Identity(pair.Value,inst.Name,inst.Password,inst.MaxPlayers):pair.Value;
        Starts++; Containers.Add(new(inst.Id, true, inst.Ports.All));
        if (FailStartAfterCreating) throw new IOException("Lost SSH response"); return Task.CompletedTask;
    }
    public Task Stop(string id, string ip, string instance, CancellationToken ct) { ArchiveOrder.Add("stop");Stops++; Containers.RemoveAll(c => c.Id == instance); return Task.CompletedTask; }
    public Task Delete(string id, CancellationToken ct) { Deletes++; Host = null; return Task.CompletedTask; }
    public Task<string> Logs(string id, string ip, string instance, CancellationToken ct) => Task.FromResult(LogText);
    public Task<string> Command(string id, string ip, Instance inst, string command, CancellationToken ct) {Commands.Add(command);InstanceCommands.Add((inst.Id,command));if(FailCommandsFor.Contains(inst.Id))throw new IOException("RCON unavailable");return Task.FromResult("response");}
    public Dictionary<(string,string),string> Configurations=[];
    public Task<string> Config(string id,string ip,string inst,ConfigRequest r,CancellationToken ct)
    {if(r.Content==null)return Task.FromResult(Configurations.GetValueOrDefault((inst,r.File),"config"));Configurations[(inst,r.File)]=r.Content;return Task.FromResult("saved");}
    public Task<byte[]> Archive(string id, string ip, string inst, CancellationToken ct) {ArchiveOrder.Add("archive");if(FailArchive)throw new IOException("Archive transfer failed");return Task.FromResult(ArchiveData);}
    public Task<GameStatus?> Probe(string id, string ip, Instance inst, CancellationToken ct)
    { if (FailProbe) throw new IOException("Game probe failed"); return Task.FromResult(GameStatus); }
}
public sealed class PoolTests
{
    [Fact] public async Task SavingConfigReloadsRunningServerButReadingDoesNot()
    {
        using var f=new Fixture();f.Start(0);await f.Drain(1);
        var id=f.Pool.Snapshot(Fixture.User(0)).Instances.Single().Id;
        await f.Pool.Config(Fixture.User(0),id,new("Admins.cfg"),default);
        Assert.Empty(f.Infra.Commands);
        await f.Pool.Config(Fixture.User(0),id,new("Admins.cfg","Group=Test:kick"),default);
        Assert.Equal(new[]{"AdminReloadServerConfig"},f.Infra.Commands);
        f.Pool.Stop(Fixture.User(0),new(Guid.NewGuid(),false,id));await f.Drain(1);
        await f.Pool.Config(Fixture.User(0),id,new("Admins.cfg","Group=Test:ban"),default);
        Assert.Single(f.Infra.Commands);
    }
    [Fact]
    public void AllSixteenSlotsHaveUniquePortsAcrossProtocols()
    { var all = Enumerable.Range(0, 16).SelectMany(i => PortSet.ForSlot(i).All).ToList(); Assert.Equal(80, all.Distinct().Count()); }
    [Fact]
    public async Task SimultaneousUsersRentOneHostAndReserveDifferentPorts()
    {
        using var f = new Fixture(); await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => f.Start(i))));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => f.Pool.ProcessNext(default)));
        Assert.Equal(1, f.Infra.Rents); Assert.Equal(16, f.Infra.Starts);
        var all = f.Pool.Snapshot(new("observer","observer",true)).Instances.SelectMany(i => i.Ports.All).ToList(); Assert.Equal(80, all.Distinct().Count());
        Assert.Throws<InvalidOperationException>(() => f.Start(17));
    }
    [Fact]
    public async Task DuplicateRequestIsIdempotent()
    { using var f = new Fixture(); var id = Guid.NewGuid(); Assert.Equal(f.Start(0, id), f.Start(0, id)); await f.Drain(2); Assert.Equal(1, f.Infra.Starts); }
    [Fact]
    public void RequestCannotBeReplayedByAnotherUser()
    { using var f = new Fixture(); var id = Guid.NewGuid(); f.Start(0, id); Assert.Throws<UnauthorizedAccessException>(() => f.Start(1, id)); }
    [Fact]
    public async Task StopOneOwnerKeepsOtherServerAndHost()
    {
        using var f = new Fixture(); f.Start(0); f.Start(1); await f.Drain(2);
        f.Pool.Stop(Fixture.User(0), new(Guid.NewGuid(), true)); await f.Drain(1);
        Assert.Equal(0, f.Infra.Deletes); Assert.Single(f.Infra.Containers); Assert.Equal("User 1", f.Pool.Snapshot(new("observer","observer",true)).Instances.Single(i => i.Phase == "running").OwnerName);
    }
    [Fact]
    public async Task LastOwnerCanReleaseAndNextSessionGetsNewHost()
    {
        using var f = new Fixture(); f.Start(0); await f.Drain(1); f.Pool.Stop(Fixture.User(0), new(Guid.NewGuid(), true)); await f.Drain(1);
        Assert.Equal(1, f.Infra.Deletes); Assert.Equal("empty", f.Pool.Snapshot(Fixture.User(0)).Host.Phase);
        f.Start(1); await f.Drain(1); Assert.Equal(2, f.Infra.Rents);
    }
    [Fact]
    public async Task BothOwnersStopBeforeEitherCompletesReleasesOnlyAfterLastStop()
    {
        using var f=new Fixture();f.Start(0);f.Start(1);await f.Drain(2);
        f.Pool.Stop(Fixture.User(0),new(Guid.NewGuid(),true));
        f.Pool.Stop(Fixture.User(1),new(Guid.NewGuid(),true));
        Assert.All(f.Pool.Snapshot(Fixture.User(0)).Instances,i=>Assert.Equal("stopping",i.Phase));
        await f.Drain(1);Assert.Equal(0,f.Infra.Deletes);Assert.Single(f.Infra.Containers);
        await f.Drain(1);Assert.Equal(1,f.Infra.Deletes);Assert.Empty(f.Infra.Containers);
        Assert.Equal("empty",f.Pool.Snapshot(Fixture.User(0)).Host.Phase);
        await f.Drain(1);Assert.Equal(1,f.Infra.Deletes);
    }
    [Fact]
    public async Task NewQueuedParticipantPreventsLastOwnerDeletion()
    {
        using var f = new Fixture(); f.Start(0); await f.Drain(1); f.Pool.Stop(Fixture.User(0), new(Guid.NewGuid(), true)); f.Start(1); await f.Drain(2);
        Assert.Equal(0, f.Infra.Deletes); Assert.Equal(1, f.Infra.Rents); Assert.Single(f.Infra.Containers);
    }
    [Fact]
    public async Task ExternalContainerBlocksDeletion()
    {
        using var f = new Fixture(); f.Start(0); await f.Drain(1); f.Infra.Containers.Add(new("external:squad", true, [7780]));
        f.Pool.Stop(Fixture.User(0), new(Guid.NewGuid(), true)); await f.Drain(1);
        Assert.Equal(0, f.Infra.Deletes); Assert.Equal("ready", f.Pool.Snapshot(Fixture.User(0)).Host.Phase);
    }
    [Fact]
    public async Task CannotDeleteWhenHostInspectionFails()
    {
        using var f = new Fixture(); f.Start(0); await f.Drain(1); f.Infra.FailInventory = true;
        f.Pool.Stop(Fixture.User(0), new(Guid.NewGuid(), true)); await f.Drain(1); Assert.Equal(0, f.Infra.Deletes);
    }
    [Fact]
    public async Task UnknownRentResponseNeverOrdersAnotherHost()
    {
        using var f = new Fixture(); f.Infra.FailRentAfterCreating = true; f.Start(0); f.Start(1); await f.Drain(2);
        Assert.Equal(1, f.Infra.Rents); Assert.Equal("ready", f.Pool.Snapshot(Fixture.User(0)).Host.Phase);
    }
    [Fact]
    public async Task UnknownStartResponseKeepsPortsAndOtherOwnerCannotDelete()
    {
        using var f = new Fixture(); f.Infra.FailStartAfterCreating = true; f.Start(0); await f.Drain(1); f.Infra.FailStartAfterCreating = false;
        f.Start(1); await f.Drain(1); var pool = f.Pool.Snapshot(new("observer","observer",true));
        Assert.Equal(10, pool.Instances.SelectMany(i => i.Ports.All).Distinct().Count());
        f.Pool.Stop(Fixture.User(1), new(Guid.NewGuid(), true)); await f.Drain(1); Assert.Equal(0, f.Infra.Deletes);
    }
    [Fact]
    public async Task ExistingBoundSteamSecondaryPortIsNeverAllocated()
    {
        using var f = new Fixture(); f.Infra.ExtraPorts.Add(7788); f.Start(0); await f.Drain(1);
        Assert.Equal(1, f.Pool.Snapshot(Fixture.User(0)).Instances.Single().Slot);
    }
    [Fact]
    public async Task OwnerCannotReadOtherOwnersRconOrLogs()
    {
        using var f = new Fixture(); f.Start(0); await f.Drain(1); var id = f.Pool.Snapshot(Fixture.User(0)).Instances.Single().Id;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Pool.Access(Fixture.User(1), id, "ListPlayers", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Pool.Access(Fixture.User(1), id, null, default));
    }
    [Fact]
    public async Task StateSurvivesCoordinatorRestart()
    {
        using var f = new Fixture(); f.Start(0); var restored = new PoolCoordinator(f.Store, f.Infra); await restored.ProcessNext(default);
        Assert.Equal("running", restored.Snapshot(Fixture.User(0)).Instances.Single().Phase); Assert.Equal(1, f.Infra.Rents);
    }
    [Fact]
    public async Task OnlyLastStoppedOwnerGetsReleaseButton()
    {
        using var f = new Fixture(); f.Start(0); await f.Drain(1); f.Pool.Stop(Fixture.User(0), new(Guid.NewGuid())); await f.Drain(1);
        Assert.True(f.Pool.Snapshot(Fixture.User(0)).CanRelease); Assert.False(f.Pool.Snapshot(Fixture.User(1)).CanRelease);
        Assert.Throws<InvalidOperationException>(() => f.Pool.Stop(Fixture.User(1), new(Guid.NewGuid(), true)));
    }
    [Fact]
    public void InvitationsAreSingleUseAndSessionsCanBeRevoked()
    {
        using var f = new Fixture(); var accounts = new Accounts(f.Store); var invite = accounts.Invite(1);
        var session = accounts.Register(new("player", "long-password-123", invite)); Assert.NotNull(accounts.Authenticate(session.Token));
        Assert.Throws<ArgumentException>(() => accounts.Register(new("player2", "long-password-123", invite)));
        accounts.Logout(session.Token); Assert.Null(accounts.Authenticate(session.Token));
    }
    [Fact]
    public void SameStoreCannotHaveTwoProcesses()
    { using var f = new Fixture(); Assert.Throws<IOException>(() => new Store(f.Directory)); }
    [Fact]
    public void DisabledRentCannotQueueOrReserveAnything()
    {
        using var f = new Fixture(); var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Cherry:DisableRent"] = "true" }).Build();
        var pool = new PoolCoordinator(f.Store, f.Infra, config); Assert.False(pool.Snapshot(Fixture.User(0)).CanStart);
        Assert.Throws<InvalidOperationException>(() => pool.Start(Fixture.User(0), new(Guid.NewGuid(), "Server", "", ""))); Assert.Empty(pool.Snapshot(Fixture.User(0)).Instances);
    }
    [Fact]
    public async Task AdoptUsesExistingHostWithoutRenting()
    {
        using var f = new Fixture(); f.Infra.Host = new("123", "192.0.2.1", true);
        await f.Pool.Adopt(new("admin", "admin", true), "123", default); f.Start(0); await f.Drain(1); Assert.Equal(0, f.Infra.Rents);
    }
    [Fact]
    public async Task AdoptionFailureDoesNotChangePool()
    {
        using var f = new Fixture(); f.Infra.Host = new("123", "192.0.2.1", true); f.Infra.FailInventory = true;
        await Assert.ThrowsAsync<IOException>(() => f.Pool.Adopt(new("admin", "admin", true), "123", default)); Assert.Equal("empty", f.Pool.Snapshot(Fixture.User(0)).Host.Phase);
    }
    [Fact]
    public async Task ConfigAndArchiveAreOwnerScoped()
    {
        using var f = new Fixture(); f.Start(0); await f.Drain(1); var id = f.Pool.Snapshot(Fixture.User(0)).Instances.Single().Id;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Pool.Config(Fixture.User(1), id, new("Admins.cfg"), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Pool.Archive(Fixture.User(1), id, default));
    }
    [Fact]
    public async Task RestartReusesOwnDataAndReservesNewFreeSlot()
    {
        using var f = new Fixture(); f.Start(0); await f.Drain(1); var id = f.Pool.Snapshot(Fixture.User(0)).Instances.Single().Id;
        f.Pool.Stop(Fixture.User(0), new(Guid.NewGuid())); await f.Drain(1); f.Start(1); f.Pool.Start(Fixture.User(0), new(Guid.NewGuid(), "Server 0", "", "", 100, id)); await f.Drain(2);
        var view = f.Pool.Snapshot(Fixture.User(0)); Assert.Equal(id, view.Instances.Single(i => i.IsMine).Id); Assert.Equal(2, f.Pool.Snapshot(new("observer","observer",true)).Instances.Count);
        Assert.Equal(10, f.Pool.Snapshot(new("observer","observer",true)).Instances.SelectMany(i => i.Ports.All).Distinct().Count());
    }
    [Fact]
    public async Task RejectedOrderDoesNotRequireUnknownOrderReset()
    {
        using var f = new Fixture(); f.Infra.RejectRent = true; f.Start(0); await f.Drain(1); Assert.Equal("empty", f.Pool.Snapshot(Fixture.User(0)).Host.Phase);
        f.Pool.Stop(Fixture.User(0), new(Guid.NewGuid())); await f.Drain(1); f.Infra.RejectRent = false; f.Start(0); await f.Drain(1); Assert.Equal(1, f.Infra.Rents);
    }
    [Fact]
    public async Task LostHostInspectionClearsStaleReadinessButKeepsPortsAndRental()
    {
        using var f = new Fixture(); f.Start(0); await f.Drain(1); f.Infra.GameStatus = new("gameReady"); await f.Pool.Reconcile(default);
        var before = f.Pool.Snapshot(Fixture.User(0)).Instances.Single(); Assert.Equal("gameReady",before.GameState);
        f.Infra.FailInventory = true; await Assert.ThrowsAsync<IOException>(() => f.Pool.Reconcile(default));
        var after = f.Pool.Snapshot(Fixture.User(0)).Instances.Single(); Assert.Equal("gameUnavailable",after.GameState); Assert.Equal(before.Ports,after.Ports);Assert.Equal("running",after.Phase); Assert.Equal(0,f.Infra.Deletes);
        f.Infra.FailInventory = false; await f.Pool.Reconcile(default);Assert.Equal("gameReady",f.Pool.Snapshot(Fixture.User(0)).Instances.Single().GameState);
    }
    [Fact]
    public async Task PasswordIsEditableOnlyByOwnerAndEmptyRemovesIt()
    {
        using var f = new Fixture();f.Pool.Start(Fixture.User(0),new(Guid.NewGuid(),"Private server","game-password",""));await f.Drain(1);
        var own=f.Pool.Snapshot(Fixture.User(0)).Instances.Single();Assert.Equal(100,own.MaxPlayers);Assert.Equal("game-password",own.Password);Assert.Empty(f.Pool.Snapshot(Fixture.User(1)).Instances);Assert.Null(f.Pool.Snapshot(new("observer","observer",true)).Instances.Single().Password);
        f.Pool.Settings(Fixture.User(0),own.Id,new("Private server","","",100));Assert.Equal("",f.Pool.Snapshot(Fixture.User(0)).Instances.Single().Password);
    }

    [Fact]
    public async Task ProviderConfirmedExternalCancellationClearsStaleServerWithoutRenting()
    {
        using var f = new Fixture();f.Start(0);await f.Drain(1);f.Infra.Host=null;
        await f.Pool.Reconcile(default);var view=f.Pool.Snapshot(Fixture.User(0));Assert.Equal("empty",view.Host.Phase);Assert.Null(view.Host.Ip);Assert.Empty(view.Instances);Assert.False(view.CanRelease);Assert.Equal(1,f.Infra.Rents);Assert.Equal(0,f.Infra.Deletes);
    }

}
