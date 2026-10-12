using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public class RecoveryTests
{
    [Fact]
    public async Task RestartAfterCompletedDeleteConfirmsAbsenceWithoutSsh()
    {
        using var f = new Fixture();
        f.Store.Save("pool", new PoolState { HostId = "123", Ip = "192.0.2.1", HostPhase = "deleting", Operations = [new Operation { Id = "pending", Kind = "stop", UserId = "user0", Release = true, Phase = "running" }] });
        var restored = new PoolCoordinator(f.Store, f.Infra); await restored.ProcessNext(default);
        Assert.Equal("empty", restored.Snapshot(Fixture.User(0)).Host.Phase); Assert.Equal(0, f.Infra.Deletes);
    }
    [Fact]
    public async Task UnknownOrderWithoutProofNeverCreatesSecondHost()
    {
        using var f = new Fixture(); f.Store.Save("pool", new PoolState { HostPhase = "ordering", Instances = [new Instance { OwnerId = "user0" }], Operations = [new Operation { Id = "pending", Kind = "start", InstanceId = "missing", UserId = "user0", Phase = "running" }] });
        var restored = new PoolCoordinator(f.Store, f.Infra); await restored.ProcessNext(default); Assert.Equal(0, f.Infra.Rents);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restored.Recover(new("admin", "admin", true), false, default));
    }
}
