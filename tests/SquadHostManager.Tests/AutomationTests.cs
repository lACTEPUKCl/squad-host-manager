using SquadHostManager.Core;
using SquadHostManager.Service;
namespace SquadHostManager.Tests;

public class AutomationTests
{
    private static string Id(Fixture f)=>f.Pool.Snapshot(Fixture.User(1)).Instances.First(i=>i.IsMine).Id;
    [Fact] public async Task TemplateCopiesSettingsAndAdminsButNeverOtherOwners()
    {
        using var f=new Fixture();f.Pool.Start(Fixture.User(1),new(Guid.NewGuid(),"Test mods","pw","3816653940",80,StartLayer:"AlBasrah_AAS_v1"));await f.Drain(1);var id=Id(f);
        f.Infra.Configurations[(id,"Admins.cfg")]="Group=Admin:chat,kick";
        var t=await f.Pool.SaveTemplate(Fixture.User(1),new("My template",id),default);
        Assert.Empty(f.Pool.Features(Fixture.User(2),null).Templates);
        Assert.Throws<UnauthorizedAccessException>(()=>f.Pool.StartTemplate(Fixture.User(2),t.Id,new(Guid.NewGuid())));
        var request=new TemplateStart(Guid.NewGuid());var op=f.Pool.StartTemplate(Fixture.User(1),t.Id,request);Assert.Equal(op.Id,f.Pool.StartTemplate(Fixture.User(1),t.Id,request).Id);await f.Drain(1);
        var created=f.Pool.Snapshot(Fixture.User(1)).Instances.Last();Assert.Equal("3816653940",created.Mods);Assert.Equal(80,created.MaxPlayers);Assert.Equal("pw",created.Password);Assert.Equal("Group=Admin:chat,kick",f.Infra.Configurations[(created.Id,"Admins.cfg")]);Assert.Equal(1,f.Infra.Rents);
    }
    [Fact] public async Task ConfigRollbackCreatesNewVersionAndReloads()
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);var id=Id(f);f.Infra.Configurations[(id,"Admins.cfg")]="original";
        await f.Pool.Config(Fixture.User(1),id,new("Admins.cfg","changed"),default);var version=f.Pool.Versions(Fixture.User(1),id,"Admins.cfg").Single();Assert.Equal("original",version.Content);
        Assert.Throws<UnauthorizedAccessException>(()=>f.Pool.Versions(Fixture.User(2),id,"Admins.cfg"));
        await f.Pool.RestoreVersion(Fixture.User(1),id,version.Id,default);Assert.Equal("original",f.Infra.Configurations[(id,"Admins.cfg")]);Assert.Equal("changed",f.Pool.Versions(Fixture.User(1),id,"Admins.cfg")[0].Content);Assert.Equal(2,f.Infra.Commands.Count(c=>c=="AdminReloadServerConfig"));
    }
    [Fact] public async Task HistoryRetainsTwentyVersionsPerFile()
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);var id=Id(f);for(var i=0;i<22;i++)await f.Pool.Config(Fixture.User(1),id,new("MOTD.cfg",i.ToString()),default);Assert.Equal(20,f.Pool.Versions(Fixture.User(1),id,"MOTD.cfg").Count);
    }
    [Fact] public async Task ConfirmedCrashesAlwaysRecoverWithBackoff()
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);var id=Id(f);var now=DateTimeOffset.UtcNow;
        f.Infra.Containers.Clear();await f.Pool.Reconcile(default);await f.Pool.TickFeatures(default,now);await f.Drain(1);Assert.Equal(2,f.Infra.Starts);
        f.Infra.Containers.Clear();await f.Pool.Reconcile(default);await f.Pool.TickFeatures(default,now.AddSeconds(10));await f.Drain(1);Assert.Equal(2,f.Infra.Starts);
        await f.Pool.TickFeatures(default,now.AddMinutes(2));await f.Drain(1);Assert.Equal(3,f.Infra.Starts);f.Infra.Containers.Clear();await f.Pool.Reconcile(default);await f.Pool.TickFeatures(default,now.AddMinutes(3));await f.Pool.TickFeatures(default,now.AddMinutes(5));await f.Drain(1);Assert.Equal(4,f.Infra.Starts);Assert.Equal(1,f.Infra.Rents);
        for(var attempt=1;attempt<=8;attempt++){f.Infra.Containers.Clear();await f.Pool.Reconcile(default);await f.Pool.TickFeatures(default,now.AddMinutes(5+attempt*6));await f.Drain(1);}
        Assert.Equal(12,f.Infra.Starts);Assert.Equal(1,f.Infra.Rents);

    }
    [Fact] public async Task NetworkLossAndManualStopsDoNotRecover()
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);var id=Id(f);f.Infra.FailProbe=true;await f.Pool.Reconcile(default);await f.Pool.TickFeatures(default);await f.Drain(1);Assert.Equal(1,f.Infra.Starts);
        f.Infra.FailProbe=false;f.Pool.Stop(Fixture.User(1),new(Guid.NewGuid(),false,id));await f.Drain(1);await f.Pool.Reconcile(default);await f.Pool.TickFeatures(default);await f.Drain(1);Assert.Equal(1,f.Infra.Starts);
    }
    [Fact] public async Task QuotaIsEnforcedDuringConcurrentStartReservations()
    {
        using var f=new Fixture();var accounts=new Accounts(f.Store);accounts.Bootstrap("long-admin-password");var owner=new User("admin","admin",true);var grant=accounts.CreateUser(owner,"Limited");accounts.SetPolicy(owner,grant.User.Id,new(null,1,true));var user=new User(grant.User.Id,grant.User.Name,false);
        f.Pool.Start(user,new(Guid.NewGuid(),"Server one","",""));Assert.Throws<InvalidOperationException>(()=>f.Pool.Start(user,new(Guid.NewGuid(),"Server two","","")));await f.Drain(1);Assert.Single(f.Pool.Snapshot(user).Instances);
    }
    [Fact] public void ExpiredInvitationAndExistingSessionAreRejected()
    {
        using var f=new Fixture();var accounts=new Accounts(f.Store);accounts.Bootstrap("long-admin-password");var owner=new User("admin","admin",true);var grant=accounts.CreateUser(owner,"Expires");var session=accounts.KeyLogin(new(grant.Key));accounts.SetPolicy(owner,grant.User.Id,new(DateTimeOffset.UtcNow.AddSeconds(-1),2));Assert.Null(accounts.Authenticate(session.Token));Assert.Throws<UnauthorizedAccessException>(()=>accounts.KeyLogin(new(grant.Key)));
    }
    [Theory][InlineData("Invalid world at start","diagLayer")][InlineData("Address already in use","diagPorts")][InlineData("Workshop download failed","diagMod")][InlineData("no space left on device","diagDisk")][InlineData("Critical error","diagCrash")]
    public void DiagnosisUsesSpecificEvidence(string logs,string expected)=>Assert.Equal(expected,StartupDiagnosis.Evaluate("error",null,null,logs).Code);
    [Fact] public async Task ReadyNoticesArePrivateAndPersistAfterRead()
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);f.Infra.GameStatus=new("gameReady");await f.Pool.Reconcile(default);await f.Pool.Reconcile(default);Assert.Single(f.Pool.Features(Fixture.User(1),null).Notices);Assert.Empty(f.Pool.Features(Fixture.User(2),null).Notices);f.Pool.ReadNotices(Fixture.User(1));var restored=new PoolCoordinator(f.Store,f.Infra);Assert.True(restored.Features(Fixture.User(1),null).Notices.Single().Read);
    }
    [Fact] public async Task BudgetAlertUsesSharedCostAndFiresOnlyOnce()
    {
        using var f=new Fixture();var now=DateTimeOffset.UtcNow;var state=new PoolState();
        state.Rentals.Add(new(){Id="host",StartedAt=now.AddMinutes(-10),HourlyRate=1, Currency="EUR"});
        state.CostEvents.Add(new(now.AddMinutes(-10),"host",[new("user1","User 1"),new("user2","User 2")]));f.Store.Save("pool",state);
        var pool=new PoolCoordinator(f.Store,f.Infra);pool.SetBudget(Fixture.User(1),new(.4m,"EUR"));await pool.TickFeatures(default,now);await pool.TickFeatures(default,now.AddMinutes(1));
        var view=pool.Features(Fixture.User(1),null);Assert.Equal(.5m,view.Costs.Single().Spent);Assert.Single(view.Notices,n=>n.Code=="noticeBudget");Assert.Empty(pool.Features(Fixture.User(2),null).Notices);
    }
    [Fact] public async Task RecoveryContinuesAfterServiceRestart()
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);f.Infra.Containers.Clear();await f.Pool.Reconcile(default);
        var restored=new PoolCoordinator(f.Store,f.Infra);await restored.TickFeatures(default);await restored.ProcessNext(default);
        Assert.Equal(2,f.Infra.Starts);Assert.Equal(1,f.Infra.Rents);
    }
    [Fact] public async Task TemplatesCannotCreateAnUnconfirmedRental()
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);var template=await f.Pool.SaveTemplate(Fixture.User(1),new("Saved template",Id(f)),default);f.Pool.Stop(Fixture.User(1),new(Guid.NewGuid(),true,Id(f)));await f.Drain(1);
        Assert.Throws<InvalidOperationException>(()=>f.Pool.StartTemplate(Fixture.User(1),template.Id,new(Guid.NewGuid())));Assert.Equal(1,f.Infra.Rents);
    }
}
