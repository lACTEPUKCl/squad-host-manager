using SquadHostManager.Core;
using SquadHostManager.Service;
namespace SquadHostManager.Tests;
public class CostStatisticsTests
{
    private static readonly DateTimeOffset Start=DateTimeOffset.Parse("2026-10-12T00:00:00Z");
    private static RentalRecord Rental()=>new(){Id="host",StartedAt=Start,HourlyRate=.67m,Currency="EUR",Rates=[new(Start,.67m)]};
    [Fact] public void JoiningMidHourRecalculatesWholePrepaidHourWithoutDoubleCharging()
    {
        var events=new[]{new CostParticipation(Start,"host",[new("a","A")]),new CostParticipation(Start.AddMinutes(30),"host",[new("a","A"),new("b","B")])};
        var before=CostAccounting.Calculate([Rental()],events,Start.AddMinutes(20));
        Assert.Equal(.67m,before.Users.Single().Spent);
        var report=CostAccounting.Calculate([Rental()],events,Start.AddMinutes(40));
        Assert.Equal(.67m,report.Users.Sum(u=>u.Spent));Assert.All(report.Users,u=>Assert.Equal(.335m,u.Spent));
        Assert.Equal(.67m,report.Users.Sum(u=>u.Saved));
        Assert.Equal(report.Users,CostAccounting.Calculate([Rental()],events,Start.AddMinutes(40)).Users);
    }
    [Fact] public void NextHourUsesRemainingParticipantsAndCurrenciesStaySeparate()
    {
        var changes=new[]{new CostParticipation(Start,"host",[new("a","A"),new("b","B")]),new CostParticipation(Start.AddMinutes(50),"host",[new("a","A")])};
        var report=CostAccounting.Calculate([Rental()],changes,Start.AddMinutes(61));
        Assert.Equal(1.005m,report.Users.Single(u=>u.UserId=="a").Spent);
        Assert.Equal(.335m,report.Users.Single(u=>u.UserId=="b").Spent);
        Assert.Equal(2,report.Hours.Count);
    }
    [Fact] public void UnknownHistoricParticipantsAreNotInvented()
    {
        var report=CostAccounting.Calculate([Rental()],[],Start.AddMinutes(10));
        Assert.True(report.HistoryIncomplete);Assert.Empty(report.Users);
    }
    [Fact] public void PhysicalCorePairsUseTopologyAndReserveOneCoreForHost()
    {
        var pairs=RemoteDocker.ParseCpuSets("#CPU,Core,Socket,Online\n0,0,0,Y\n4,0,0,Y\n1,1,0,Y\n5,1,0,Y\n2,2,0,Y\n6,2,0,Y\n3,3,0,N\n7,3,0,Y\n");
        Assert.Equal(new[]{"1,5","2,6"},pairs);
        Assert.Equal(4,pairs.SelectMany(p=>p.Split(',')).Distinct().Count());
    }
}
