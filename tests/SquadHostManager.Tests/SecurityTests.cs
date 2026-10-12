using System.Security.Cryptography;
using System.Text.Json;
using SquadHostManager.Core;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;
public class SecurityTests
{
    [Fact] public async Task SnapshotHidesPeerSettingsAndFreeFormEventsButKeepsCounters()
    {
        using var f=new Fixture();f.Pool.Start(Fixture.User(0),new(Guid.NewGuid(),"Peer private title","peer password","9876543210",100,StartLayer:"Private_Layer"));f.Start(1);await f.Drain(2);
        var own=f.Pool.Snapshot(Fixture.User(1));Assert.Single(own.Instances);Assert.True(own.Instances[0].IsMine);Assert.Equal(2,own.OccupiedServers);Assert.Equal(1,own.OtherActiveServers);Assert.Null(own.LastEvent);Assert.Empty(own.RentalHistory!);
        var json=JsonSerializer.Serialize(own);foreach(var secret in new[]{"Peer private title","peer password","9876543210","Private_Layer"})Assert.DoesNotContain(secret,json);
        var owner=f.Pool.Snapshot(new("observer","observer",true));Assert.Equal(2,owner.Instances.Count);Assert.All(owner.Instances,i=>Assert.Null(i.Password));
    }
    [Fact] public async Task NewFriendCannotRentOrUseStartAsAnOrderingBypass()
    {
        using var f=new Fixture();var accounts=new Accounts(f.Store);accounts.Bootstrap("long-admin-password");var grant=accounts.CreateUser(new("admin","admin",true),"No spending");var friend=new User(grant.User.Id,grant.User.Name,false);
        Assert.False(f.Pool.Snapshot(friend).CanRent);Assert.Throws<UnauthorizedAccessException>(()=>f.Pool.Start(friend,new(Guid.NewGuid(),"No paid host","","")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Pool.RentHost(friend,new(Guid.NewGuid(),"DE-Frankfurt",.666m,"EUR",42),default));Assert.Equal(0,f.Infra.Rents);Assert.False(f.Pool.Snapshot(friend).Busy);
        f.Start(0);await f.Drain(1);f.Pool.Start(friend,new(Guid.NewGuid(),"Shared host","",""));await f.Drain(1);Assert.Equal(1,f.Infra.Rents);Assert.Single(f.Pool.Snapshot(friend).Instances);
    }
    [Fact] public async Task RevokingSpendingPermissionBeforeQueueExecutionPreventsRental()
    {
        using var f=new Fixture();f.Start(0);f.Store.Save("access-policy-user0",new AccessPolicy(null,16,false));await f.Drain(1);Assert.Equal(0,f.Infra.Rents);
    }
    [Fact] public void ContainerHasOnlyPrivateRconReadOnlyCacheAndExplicitGamePorts()
    {
        var instance=new Instance{CpuSet="1,17"};var script=RemoteDocker.BuildStartScript(instance);var command=script.Split('\n').Single(l=>l.TrimStart().StartsWith("docker run -d "));
        Assert.DoesNotContain("--network host",command);Assert.Contains("--network shm_"+instance.Id+"_net",command);Assert.Contains("-p 127.0.0.1:21114:21114/tcp",command);Assert.DoesNotContain("-p 21114",command);Assert.Contains("-p 7788:7788/udp",command);Assert.Contains("cache:/shm-cache:ro",command);Assert.Contains("--read-only",command);Assert.Contains("--user 1000:1000",command);Assert.Contains("--cap-drop ALL",command);Assert.Contains("--security-opt no-new-privileges",command);Assert.Contains("--pids-limit 512",command);Assert.Contains("--memory 12g",command);Assert.Contains("exec 8</shm-cache/cache.lock",RemoteDocker.LaunchScript);Assert.Contains("DOCKER-USER",ContainerSecurity.FirewallScript);Assert.Contains("Before=docker.service",ContainerSecurity.FirewallUnit);
    }
    [Theory][InlineData("1;reboot")][InlineData("1,2,3")][InlineData("-1,2")]
    public void CpuPairCannotInjectShellCommands(string cpu)=>Assert.Throws<ArgumentException>(()=>RemoteDocker.BuildStartScript(new(){CpuSet=cpu}));
    private static (RSA Key,UpdateManifest Manifest,byte[] Bytes,string Signature,string Public) Signed()
    {
        var rsa=RSA.Create(2048);var manifest=new UpdateManifest(1,Product.Repository,"0.2.0","SquadHostManager.exe",123,new string('A',64),DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddDays(10));var bytes=JsonSerializer.SerializeToUtf8Bytes(manifest);return(rsa,manifest,bytes,Convert.ToBase64String(rsa.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss)),Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
    }
    [Fact] public void ValidIndependentSignatureIsAccepted()
    {
        var s=Signed();using var key=s.Key;Assert.Equal(s.Manifest,UpdateTrust.Verify(s.Bytes,s.Signature,"0.2.0",s.Manifest.Sha256,123,s.Public));
    }
    [Fact] public void ReplacedHashFileOrExecutableCannotPassSignature()
    {
        var s=Signed();using var key=s.Key;Assert.Throws<InvalidOperationException>(()=>UpdateTrust.Verify(s.Bytes,s.Signature,"0.2.0",new string('B',64),123,s.Public));Assert.Throws<InvalidOperationException>(()=>UpdateTrust.Verify(s.Bytes,s.Signature,"0.3.0",s.Manifest.Sha256,123,s.Public));
        var changed=JsonSerializer.SerializeToUtf8Bytes(s.Manifest with{Sha256=new string('B',64)});Assert.Throws<InvalidOperationException>(()=>UpdateTrust.Verify(changed,s.Signature,"0.2.0",new string('B',64),123,s.Public));
    }
    [Fact] public void DifferentSigningKeyIsRejected()
    {
        var s=Signed();using var key=s.Key;using var attacker=RSA.Create(2048);var signature=Convert.ToBase64String(attacker.SignData(s.Bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss));Assert.Throws<InvalidOperationException>(()=>UpdateTrust.Verify(s.Bytes,signature,"0.2.0",s.Manifest.Sha256,123,s.Public));
    }
    [Fact] public void ExpiredSignedManifestIsRejected()
    {
        var s=Signed();using var key=s.Key;var expired=s.Manifest with{IssuedAt=DateTimeOffset.UtcNow.AddDays(-20),ValidUntil=DateTimeOffset.UtcNow.AddDays(-1)};var bytes=JsonSerializer.SerializeToUtf8Bytes(expired);var signature=Convert.ToBase64String(key.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss));Assert.Throws<InvalidOperationException>(()=>UpdateTrust.Verify(bytes,signature,"0.2.0",expired.Sha256,123,s.Public));
    }
}
