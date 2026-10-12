using SquadHostManager.Core;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public sealed class MultipleServersTests
{
    [Fact] public async Task FailedStartupStillAllowsOwnLogsButNotRcon()
    {
        using var f=new Fixture();f.Infra.FailStartAfterCreating=true;f.Start(0);await f.Drain(1);var i=f.Pool.Snapshot(Fixture.User(0)).Instances.Single();
        Assert.Equal("error",i.Phase);Assert.Equal("logs",await f.Pool.Access(Fixture.User(0),i.Id,null,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Pool.Access(Fixture.User(0),i.Id,"ListPlayers",default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Pool.Access(Fixture.User(1),i.Id,null,default));
    }
    [Fact] public async Task QueuePositionAndStoppingStateDoNotReleasePortsEarly()
    {
        using var f=new Fixture();f.Start(0);f.Start(0);
        var view=f.Pool.Snapshot(Fixture.User(0));Assert.Equal(1,view.Instances[0].QueuePosition);Assert.Equal(2,view.Instances[1].QueuePosition);
        await f.Drain(1);view=f.Pool.Snapshot(Fixture.User(0));Assert.Null(view.Instances[0].QueuePosition);Assert.Equal(1,view.Instances[1].QueuePosition);
        f.Pool.Stop(Fixture.User(0),new(Guid.NewGuid(),false,view.Instances[0].Id));
        view=f.Pool.Snapshot(Fixture.User(0));Assert.Equal("stopping",view.Instances[0].Phase);Assert.Equal(10,view.Instances.SelectMany(i=>i.Ports.All).Distinct().Count());
        await f.Drain(2);Assert.Equal("stopped",f.Pool.Snapshot(Fixture.User(0)).Instances[0].Phase);
    }
    [Fact]
    public void WindowsSourceProducesOneCompleteLinuxDockerCommandAndLfLaunchScript()
    {
        var script = RemoteDocker.BuildStartScript(new Instance { OwnerId = "owner", Name = "Test", MaxPlayers = 20, CpuSet = "1,17" });
        Assert.DoesNotContain('\r', script);
        var command = script.Split('\n').Single(l => l.TrimStart().StartsWith("docker run -d ", StringComparison.Ordinal));
        Assert.Contains(RemoteDocker.Image+" /shm-launch.sh", command); Assert.Contains("-e PORT=7787", command);
        var launch = script.Split('\n').Single(l => l.Contains("/launch.sh\"", StringComparison.Ordinal) && l.Contains("base64 -d", StringComparison.Ordinal));
        var data = System.Text.RegularExpressions.Regex.Match(launch, "printf %s '([^']+)'").Groups[1].Value;
        Assert.DoesNotContain('\r', System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data)));
    }
    [Fact]
    public async Task OneOwnerCanReserveSixteenServersConcurrentlyOnOneHost()
    {
        using var f = new Fixture(); await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => f.Start(0))));
        await f.Drain(16); var view = f.Pool.Snapshot(Fixture.User(0));
        Assert.Equal(1, f.Infra.Rents); Assert.Equal(16, view.Instances.Count(i => i.IsMine));
        Assert.Equal(80, view.Instances.SelectMany(i => i.Ports.All).Distinct().Count());
        Assert.Throws<InvalidOperationException>(() => f.Start(0));
        Assert.Throws<ArgumentException>(() => f.Pool.Stop(Fixture.User(0), new(Guid.NewGuid())));
    }
    [Fact]
    public async Task ExplicitStopCannotAffectAnotherServerOfTheSameOwner()
    {
        using var f = new Fixture(); f.Start(0); f.Start(0); f.Start(1); await f.Drain(3);
        var own = f.Pool.Snapshot(Fixture.User(0)).Instances.Where(i => i.IsMine).ToList();
        Assert.Throws<UnauthorizedAccessException>(() => f.Pool.Stop(Fixture.User(1), new(Guid.NewGuid(), false, own[0].Id)));
        f.Pool.Stop(Fixture.User(0), new(Guid.NewGuid(), true, own[0].Id)); await f.Drain(1);
        Assert.Equal(0, f.Infra.Deletes); Assert.Equal(2, f.Infra.Containers.Count); Assert.Contains(f.Infra.Containers, i => i.Id == own[1].Id);
    }
    [Fact]
    public async Task IndependentStopsCanBeQueuedAndOnlyTheLastReleases()
    {
        using var f = new Fixture(); f.Start(0); f.Start(0); await f.Drain(2);
        var own = f.Pool.Snapshot(Fixture.User(0)).Instances;
        var request = new StopRequest(Guid.NewGuid(), false, own[0].Id);
        Assert.Equal(f.Pool.Stop(Fixture.User(0), request), f.Pool.Stop(Fixture.User(0), request));
        f.Pool.Stop(Fixture.User(0), new(Guid.NewGuid(), true, own[1].Id)); await f.Drain(2);
        Assert.Equal(2, f.Infra.Stops); Assert.Equal(1, f.Infra.Deletes);
    }
    [Fact]
    public async Task ResumeUsesExplicitIdentityAndNewStartNeverOverwritesStoppedData()
    {
        using var f = new Fixture(); f.Pool.Start(Fixture.User(0), new(Guid.NewGuid(), "Original", "secret", "2848273764", 20)); await f.Drain(1);
        var original = f.Pool.Snapshot(Fixture.User(0)).Instances.Single();
        f.Pool.Stop(Fixture.User(0), new(Guid.NewGuid(), false, original.Id)); await f.Drain(1);
        Assert.Equal("config", await f.Pool.Config(Fixture.User(0), original.Id, new("Admins.cfg"), default));
        Assert.Empty(await f.Pool.Archive(Fixture.User(0), original.Id, default));
        f.Start(0); await f.Drain(1);
        f.Pool.Start(Fixture.User(0), new(Guid.NewGuid(), "Original", null, "2848273764", 20, original.Id)); await f.Drain(1);
        var state = f.Store.Load<PoolState>("pool")!;
        Assert.Equal(2, state.Instances.Count); Assert.All(state.Instances, i => Assert.Equal("running", i.Phase));
        Assert.Equal("secret", state.Instances.Single(i => i.Id == original.Id).Password);
        Assert.Throws<UnauthorizedAccessException>(() => f.Pool.Start(Fixture.User(1), new(Guid.NewGuid(), "Other", "", "", 20, original.Id)));
    }
    [Fact]
    public async Task SettingsAreScopedToSelectedServerAndPersistAcrossRestart()
    {
        using var f = new Fixture(); f.Start(0); f.Start(0); await f.Drain(2); var view = f.Pool.Snapshot(Fixture.User(0)); var id = view.Instances[0].Id;
        Assert.Throws<UnauthorizedAccessException>(() => f.Pool.Settings(Fixture.User(1), id, new("Renamed", null, "", 20)));
        f.Pool.Settings(Fixture.User(0), id, new("Renamed", "secret", "123, 123 456", 24));
        var restored = new PoolCoordinator(f.Store, f.Infra); var updated = restored.Snapshot(Fixture.User(0));
        Assert.Equal("Renamed", updated.Instances[0].Name); Assert.Equal("123 456", updated.Instances[0].Mods); Assert.Equal(24, updated.Instances[0].MaxPlayers);
        Assert.Equal(view.Instances[1], updated.Instances[1]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Pool.Download(Fixture.User(1), id, "Logs/test.log", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Pool.Files(Fixture.User(1), id, default));
    }
    [Theory]
    [InlineData("SHM_STAGE gameDownloading\nprogress: 52.0", "gameDownloading", 52)]
    [InlineData("progress: 100\nSHM_STAGE modsDownloading 123\nprogress: 12.5", "modsDownloading", 12.5)]
    [InlineData("SHM_STAGE modsDownloading 123\nprogress: 99\nSHM_STAGE gameStarting", "gameStarting", -1)]
    public void ProgressBelongsToCurrentStage(string logs, string stage, double percent)
    { var status = RemoteDocker.ParseStatus(logs); Assert.Equal(stage, status.State); Assert.Equal(percent < 0 ? null : (double?)percent, status.DownloadPercent); }
    [Fact] public void LongLogTailCannotTurnGameStartupBackIntoDownload()
    {
        Assert.Equal("gameStarting",RemoteDocker.ParseStatus("LogSquad: Loading rotation").State);
        Assert.Equal("modsDownloading",RemoteDocker.ParseStatus("progress: 53.2","modsDownloading").State);
        Assert.Equal(53.2,RemoteDocker.ParseStatus("progress: 53.2","modsDownloading").DownloadPercent);
        Assert.Null(RemoteDocker.ParseStatus("SHM_STAGE gameDownloading\nprogress: 100","modsDownloading").DownloadPercent);
    }
    [Theory]
    [InlineData("../config/Rcon.cfg")]
    [InlineData("Logs/../../config/Rcon.cfg")]
    [InlineData("Logs/../Logs/test")]
    [InlineData("/Logs/test")]
    [InlineData("Logs\\test")]
    [InlineData("Demos/match.demo")]
    [InlineData("Logs/test\nsecret")]
    public void FileTraversalIsRejected(string path) => Assert.Throws<ArgumentException>(() => RemoteDocker.ValidateFilePath(path));
    [Fact]
    public void EstimateUsesRecentRateAndResetsAtStageOrProgressRollback()
    {
        var e = new DownloadEstimate(); var now = DateTimeOffset.UtcNow;
        Assert.Null(e.Observe("gameDownloading", 10, now));
        Assert.Equal(TimeSpan.FromSeconds(240), e.Observe("gameDownloading", 20, now.AddSeconds(30)));
        Assert.Null(e.Observe("modsDownloading", 20, now.AddSeconds(40)));
        Assert.Null(e.Observe("modsDownloading", 5, now.AddSeconds(70)));
        Assert.Null(e.Observe("gameStarting", null, now.AddSeconds(90)));
    }
    [Fact]
    public void PlayerParsingDoesNotIncludeDisconnectedPlayers()
    {
        var players = SquadPlayers.Parse("----- Active Players -----\nID: 3 | Online IDs: EOS: abc steam: 76561198012345678 | Name: Тест | Team ID: 1 | Squad ID: N/A | Is Leader: False\n----- Recently Disconnected Players -----\nID: 4 | Name: Old | Team ID: 2 | Squad ID: 1");
        var p = Assert.Single(players); Assert.Equal("3", p.Id); Assert.Equal("Тест", p.Name); Assert.Equal("76561198012345678", p.SteamId);
    }
}
