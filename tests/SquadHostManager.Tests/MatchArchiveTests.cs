using SquadHostManager.Core;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public class MatchArchiveTests
{
    [Fact] public async Task LogsAreSavedBeforeStopAndSurviveRentalEndAndServiceRestart()
    {
        using var f=new Fixture();f.Infra.ArchiveData=[1,2,3,4];f.Start(1);await f.Drain(1);
        var instance=f.Pool.Snapshot(Fixture.User(1)).Instances.Single();
        f.Pool.Stop(Fixture.User(1),new(Guid.NewGuid(),true,instance.Id));await f.Drain(1);
        Assert.Equal(new[]{"archive","stop"},f.Infra.ArchiveOrder);Assert.Null(f.Infra.Host);
        var restored=new PoolCoordinator(f.Store,f.Infra);var match=Assert.Single(restored.Tools(Fixture.User(1),null).Matches);
        Assert.Equal(instance.Name,match.ServerName);Assert.NotNull(match.ArchiveId);Assert.True(match.At>DateTimeOffset.UtcNow.AddMinutes(-1));
        using var archive=restored.DownloadMatchArchive(Fixture.User(1),match.Id);using var copy=new MemoryStream();archive.CopyTo(copy);Assert.Equal(f.Infra.ArchiveData,copy.ToArray());
        Assert.Empty(restored.Tools(Fixture.User(2),null).Matches);
        Assert.Throws<UnauthorizedAccessException>(()=>restored.DownloadMatchArchive(Fixture.User(2),match.Id));
        Assert.Throws<UnauthorizedAccessException>(()=>restored.DeleteMatch(Fixture.User(2),match.Id));
        archive.Dispose();restored.DeleteMatch(Fixture.User(1),match.Id);
        Assert.Empty(restored.Tools(Fixture.User(1),null).Matches);Assert.Empty(Directory.GetFiles(Path.Combine(f.Store.DirectoryPath,"match-archives")));
    }
    [Fact] public async Task FailedArchiveBlocksStopAndRentalDeletionAndCanBeRetried()
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);var id=f.Pool.Snapshot(Fixture.User(1)).Instances.Single().Id;
        f.Infra.FailArchive=true;f.Pool.Stop(Fixture.User(1),new(Guid.NewGuid(),true,id));await f.Drain(1);
        Assert.Equal(0,f.Infra.Stops);Assert.Equal(0,f.Infra.Deletes);Assert.Empty(f.Pool.Tools(Fixture.User(1),null).Matches);
        await f.Pool.TickFeatures(default);await f.Drain(1);Assert.Equal(1,f.Infra.Starts);
        f.Infra.FailArchive=false;f.Pool.Stop(Fixture.User(1),new(Guid.NewGuid(),true,id));await f.Drain(1);
        Assert.Equal(1,f.Infra.Stops);Assert.Equal(1,f.Infra.Deletes);Assert.Single(f.Pool.Tools(Fixture.User(1),null).Matches);
    }
    [Fact] public async Task ExistingMatchRowsShareArchiveAndDeletingOneKeepsOtherDownload()
    {
        using var f=new Fixture();f.Start(1);await f.Drain(1);var instance=f.Pool.Snapshot(Fixture.User(1)).Instances.Single();
        var state=f.Store.Load<PoolState>("pool")!;foreach(var id in new[]{"match1","match2"})state.Matches.Add(new("user1",new(id,instance.Id,DateTimeOffset.UtcNow,"Layer","draw",10,[])));f.Store.Save("pool",state);
        var pool=new PoolCoordinator(f.Store,f.Infra);pool.Stop(Fixture.User(1),new(Guid.NewGuid(),true,instance.Id));await pool.ProcessNext(default);
        var matches=pool.Tools(Fixture.User(1),null).Matches;Assert.Equal(2,matches.Count);Assert.Equal(matches[0].ArchiveId,matches[1].ArchiveId);
        pool.DeleteMatch(Fixture.User(1),matches[0].Id);using var archive=pool.DownloadMatchArchive(Fixture.User(1),matches[1].Id);
        Assert.NotNull(archive);Assert.Contains(matches[0].Id,f.Store.Load<PoolState>("pool")!.DeletedMatches);
    }
}
