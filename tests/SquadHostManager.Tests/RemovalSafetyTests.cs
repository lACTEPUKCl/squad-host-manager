using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public class RemovalSafetyTests
{
    [Fact] public void EmptyAndHistoricalPoolCanBeRemoved()
    {
        Assert.True(RemovalSafety.CanRemove(new()));
        Assert.True(RemovalSafety.CanRemove(new(){Instances=[new(){Phase="stopped"}],Operations=[new(){Phase="done"}]}));
    }
    [Fact] public void ExistingRentalBlocksRemoval()=>Assert.False(RemovalSafety.CanRemove(new(){HostId="existing"}));
    [Theory][InlineData("ordering")][InlineData("order-uncertain")][InlineData("deleting")][InlineData("ready")][InlineData("unavailable")]
    public void UnresolvedHostStateBlocksRemoval(string phase)=>Assert.False(RemovalSafety.CanRemove(new(){HostPhase=phase}));
    [Theory][InlineData("queued")][InlineData("running")][InlineData("stopping")][InlineData("failed")]
    public void NonStoppedInstanceBlocksRemoval(string phase)=>Assert.False(RemovalSafety.CanRemove(new(){Instances=[new(){Phase=phase}]}));
    [Theory][InlineData("queued")][InlineData("running")]
    public void PendingOperationBlocksRemoval(string phase)=>Assert.False(RemovalSafety.CanRemove(new(){Operations=[new(){Phase=phase}]}));
}
