using Microsoft.Extensions.Configuration;
using SquadHostManager.Core;
using SquadHostManager.Service;
namespace SquadHostManager.Tests;
public partial class RespawnPoolTests
{
    [Fact] public async Task NewGameCannotReceiveAStartCommandWithoutPreparedRcon()
    {
        using var f=new Fixture();using var backend=new Backend{MissingRconAllocations=true};using var http=new HttpClient(backend);
        var config=new ConfigurationBuilder().AddConfiguration(RentalConfig()).AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:RconSetupTimeoutSeconds","0"}}).Build();
        using var api=new RespawnHost(f.Store,config,http,backend.Watch);using var infra=new RespawnInfrastructure(api,f.Store);
        f.Store.Save("pool",new PoolState{Provider="respawn"});f.Store.Save("respawn-plan-user1",new RespawnAllocation(1070,8192,.13324999m,"eu"));var pool=new PoolCoordinator(f.Store,infra);
        pool.Start(Fixture.User(1),new(Guid.NewGuid(),"Rcon guarded server","","",20));await pool.ProcessNext(default);
        var instance=Assert.Single(pool.Snapshot(Fixture.User(1)).Instances);Assert.Equal("error",instance.Phase);Assert.Contains("RCON",instance.Error);
        Assert.Equal(1,backend.Rentals);Assert.DoesNotContain(backend.Calls,c=>c.StartsWith("POST ") && c.EndsWith("/powerstate"));
    }
    private static IConfiguration RentalConfig()=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Respawn:Token","rsp_"+new string('a',64)}}).Build();
    [Fact] public async Task RejectedRentalsLeaveAnErrorButNoPhantomServersOrReservedSlots()
    {
        using var f=new Fixture();using var backend=new Backend{RejectRental=true};using var http=new HttpClient(backend);using var api=new RespawnHost(f.Store,RentalConfig(),http,backend.Watch);using var infra=new RespawnInfrastructure(api,f.Store);
        f.Store.Save("pool",new PoolState{Provider="respawn"});f.Store.Save("respawn-plan-user1",new RespawnAllocation(1070,8192,.13324999m,"eu"));var pool=new PoolCoordinator(f.Store,infra);
        for(var i=0;i<2;i++){pool.Start(Fixture.User(1),new(Guid.NewGuid(),"Failed server","","",20));await pool.ProcessNext(default);}
        var snapshot=pool.Snapshot(Fixture.User(1));Assert.Empty(snapshot.Instances);Assert.Equal(0,snapshot.OccupiedServers);Assert.Contains("Server limit reached",snapshot.Host.Error);
        Assert.Equal(2,f.Store.LoadPrefix<Instance>("rejected-rental-record-").Count);Assert.Equal(0,backend.Rentals);Assert.False(api.Pending);
        var restored=new PoolCoordinator(f.Store,infra);Assert.Empty(restored.Snapshot(Fixture.User(1)).Instances);
    }
    [Fact] public async Task LostRentReplyRetainsTheReservationAndCannotBeCleanedAsRejected()
    {
        using var f=new Fixture();using var backend=new Backend{LoseRentalResponse=true};using var http=new HttpClient(backend);using var api=new RespawnHost(f.Store,RentalConfig(),http,backend.Watch);using var infra=new RespawnInfrastructure(api,f.Store);
        f.Store.Save("pool",new PoolState{Provider="respawn"});f.Store.Save("respawn-plan-user1",new RespawnAllocation(1070,8192,.13324999m,"eu"));var pool=new PoolCoordinator(f.Store,infra);
        pool.Start(Fixture.User(1),new(Guid.NewGuid(),"Uncertain server","","",20));await pool.ProcessNext(default);
        Assert.Equal("error",Assert.Single(pool.Snapshot(Fixture.User(1)).Instances).Phase);Assert.True(api.Pending);Assert.Equal(1,backend.Rentals);
        Assert.Single(new PoolCoordinator(f.Store,infra).Snapshot(Fixture.User(1)).Instances);Assert.Empty(f.Store.LoadPrefix<Instance>("rejected-rental-record-"));
    }
    [Theory][InlineData(1,1)][InlineData(2,2)]
    public async Task ReleasedOfflineAllocationCanOnlyBeReusedByItsOwner(int nextUser,int rentals)
    {
        using var f=new Fixture();using var backend=new Backend();using var http=new HttpClient(backend);using var api=new RespawnHost(f.Store,RentalConfig(),http,backend.Watch);using var infra=new RespawnInfrastructure(api,f.Store);
        f.Store.Save("pool",new PoolState{Provider="respawn"});foreach(var id in new[]{1,2})f.Store.Save("respawn-plan-user"+id,new RespawnAllocation(1070,8192,.13324999m,"eu"));var pool=new PoolCoordinator(f.Store,infra);
        pool.Start(Fixture.User(1),new(Guid.NewGuid(),"Original server","","",20));await pool.ProcessNext(default);var originalUuid=backend.Uuid;
        var original=Assert.Single(pool.Snapshot(Fixture.User(1)).Instances);pool.Stop(Fixture.User(1),new(Guid.NewGuid(),true,original.Id));await pool.ProcessNext(default);Assert.Empty(pool.Snapshot(Fixture.User(1)).Instances);
        pool.Start(Fixture.User(nextUser),new(Guid.NewGuid(),"Next server","","",20));await pool.ProcessNext(default);
        var next=Assert.Single(pool.Snapshot(Fixture.User(nextUser)).Instances);Assert.Equal("running",next.Phase);Assert.Equal(rentals,backend.Rentals);
        var mapped=f.Store.Load<RespawnManagedServer>("respawn-instance-"+next.Id)!;Assert.Equal(nextUser==1,originalUuid==mapped.Uuid);
    }
    [Fact] public void StartupCleansOnlyProvenRejectedUnmappedOrdersAndKeepsHistory()
    {
        using var f=new Fixture();var state=new PoolState{Provider="respawn"};
        foreach(var status in new[]{"rejected","sent"})
        {
            var i=new Instance{OwnerId="user1",Phase="error",Name=status,Error="Rejected"};state.Instances.Add(i);f.Store.Save("respawn-order-"+Guid.Parse(i.Id),status);
        }
        f.Store.Save("pool",state);var pool=new PoolCoordinator(f.Store,f.Infra);
        Assert.Equal("sent",Assert.Single(pool.Snapshot(Fixture.User(1)).Instances).Name);Assert.Single(f.Store.LoadPrefix<Instance>("rejected-rental-record-"));
    }
}
