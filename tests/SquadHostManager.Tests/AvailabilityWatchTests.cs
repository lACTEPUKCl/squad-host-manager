using SquadHostManager.Core;

namespace SquadHostManager.Tests;

public sealed class AvailabilityWatchTests
{
    private static RentalLocation City(string slug,int available)=>new(slug,slug,available,.67m,"EUR",1);
    [Fact] public void InitialStockDoesNotNotify()
    {var watch=new LocationAvailabilityWatch();watch.Selected.Add("Stockholm");Assert.Empty(watch.Observe([City("Stockholm",3)]));}
    [Fact] public void SelectedCityAlertsOnceWhenStockReturns()
    {var watch=new LocationAvailabilityWatch();watch.Selected.Add("Stockholm");watch.Observe([City("Stockholm",0)]);Assert.Single(watch.Observe([City("Stockholm",1)]));Assert.Empty(watch.Observe([City("Stockholm",1)]));Assert.Empty(watch.Observe([City("Stockholm",3)]));}
    [Fact] public void OtherCitiesDoNotNotify()
    {var watch=new LocationAvailabilityWatch();watch.Selected.Add("Stockholm");watch.Observe([City("Frankfurt",0)]);Assert.Empty(watch.Observe([City("Frankfurt",2)]));}
    [Fact] public void SoldOutAgainRearmsAndMatchingIsCaseInsensitive()
    {var watch=new LocationAvailabilityWatch();watch.Selected.Add("stockholm");watch.Observe([City("Stockholm",0)]);Assert.Single(watch.Observe([City("Stockholm",1)]));watch.Observe([City("Stockholm",0)]);Assert.Single(watch.Observe([City("Stockholm",2)]));}
    [Fact] public void ClearingSelectionAndResetPreventOldAccountNotifications()
    {var watch=new LocationAvailabilityWatch();watch.Selected.Add("Stockholm");watch.Observe([City("Stockholm",0)]);watch.Selected.Clear();Assert.Empty(watch.Observe([City("Stockholm",1)]));watch.Reset();watch.Selected.Add("Stockholm");Assert.Empty(watch.Observe([City("Stockholm",2)]));}
    [Fact] public void MissingSnapshotDoesNotInventZeroStock()
    {var watch=new LocationAvailabilityWatch();watch.Selected.Add("Stockholm");watch.Observe([City("Stockholm",1)]);watch.Observe([]);Assert.Empty(watch.Observe([City("Stockholm",1)]));}
}
