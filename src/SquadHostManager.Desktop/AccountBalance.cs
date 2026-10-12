using System.Net.Http.Json;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;

public partial class MainWindow
{
    private DateTimeOffset balanceLoaded;
    private async Task LoadBalance(bool force=false)
    {
        if(http==null || !force && DateTimeOffset.UtcNow-balanceLoaded<TimeSpan.FromSeconds(30))return;
        balanceLoaded=DateTimeOffset.UtcNow;
        try
        {
            using var response=await http.GetAsync("api/balance");await Check(response);
            var balance=await response.Content.ReadFromJsonAsync<BalanceView>();
            AccountBalance.Text=balance?.Available is {} amount?amount.ToString("N2")+" "+balance.Currency:L("balanceUnavailable");
            AccountBalance.ToolTip=balance?.Stale==true?L("balanceStale"):balance?.CheckedAt is {} at?L("balanceCheckedAt",at.ToLocalTime().ToString("T")):null;
            if(balance?.Stale==true)AccountBalance.Text+=" · "+L("balanceStaleShort");
        }
        catch{AccountBalance.Text=L("balanceUnavailable");AccountBalance.ToolTip=L("balanceStale");}
    }
}
