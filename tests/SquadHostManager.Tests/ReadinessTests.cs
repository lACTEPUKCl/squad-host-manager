using Microsoft.Extensions.Configuration;
using SquadHostManager.Core;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public sealed class ReadinessTests
{
    [Fact]
    public async Task RestartUpdatesOnlyOwnInstanceAndRetainsIdentityAndPorts()
    {
        using var f = new Fixture(); f.Start(1); f.Start(2); await f.Drain(2);
        var before = f.Pool.Snapshot(Fixture.User(1)).Instances.Single(i => i.IsMine); var request = new RestartRequest(Guid.NewGuid());
        Assert.Throws<UnauthorizedAccessException>(() => f.Pool.Restart(Fixture.User(2), before.Id, request));
        var op = f.Pool.Restart(Fixture.User(1), before.Id, request); await f.Drain(1);
        var after = f.Pool.Snapshot(Fixture.User(1)).Instances.Single(i => i.IsMine);
        Assert.Equal(before.Id, after.Id); Assert.Equal(before.Ports, after.Ports); Assert.Equal("running", after.Phase);
        Assert.Equal(2, f.Infra.Containers.Count); Assert.Equal(1, f.Infra.Stops); Assert.Equal(3, f.Infra.Starts); Assert.Equal(1, f.Infra.Rents); Assert.Equal(0, f.Infra.Deletes);
        Assert.Equal(op.Id, f.Pool.Restart(Fixture.User(1), before.Id, request).Id); await f.Drain(1); Assert.Equal(1, f.Infra.Stops);
    }
    [Fact]
    public async Task RunningContainerRequiresIndependentGameProbe()
    {
        using var f = new Fixture(); f.Start(1); await f.Drain(1);
        Assert.Null(f.Pool.Snapshot(Fixture.User(1)).Instances.Single().GameState);
        f.Infra.GameStatus = new("gameDownloading", 42.5); await f.Pool.Reconcile(default);
        var view = f.Pool.Snapshot(Fixture.User(1)).Instances.Single(); Assert.Equal("gameDownloading", view.GameState); Assert.Equal(42.5, view.DownloadPercent);
        f.Infra.GameStatus = new("gameReady"); await f.Pool.Reconcile(default);
        Assert.Equal("gameReady", f.Pool.Snapshot(Fixture.User(1)).Instances.Single().GameState);
        f.Infra.FailProbe = true; await f.Pool.Reconcile(default);
        view = f.Pool.Snapshot(Fixture.User(1)).Instances.Single(); Assert.Equal("gameUnavailable", view.GameState); Assert.Equal("running", view.Phase); Assert.Equal(0, f.Infra.Stops);
    }
    [Fact]
    public async Task ReleaseLockRejectsDeletionButAllowsOwnServerStop()
    {
        using var f = new Fixture(); f.Start(1); await f.Drain(1);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Cherry:DisableRelease"] = "true" }).Build();
        var pool = new PoolCoordinator(f.Store, f.Infra, config); var user = Fixture.User(1);
        Assert.Throws<InvalidOperationException>(() => pool.Stop(user, new(Guid.NewGuid(), true)));
        pool.Stop(user, new(Guid.NewGuid(), false)); await pool.ProcessNext(default);
        Assert.False(pool.Snapshot(user).CanRelease); Assert.Equal(0, f.Infra.Deletes); Assert.NotNull(f.Infra.Host);
    }
}
