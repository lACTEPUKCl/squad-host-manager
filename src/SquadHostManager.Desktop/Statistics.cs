using System.Diagnostics;
using System.Net.Http.Json;
using System.Windows;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;
public partial class MainWindow
{
    public System.Windows.Input.ICommand SupportShortcut=>new SupportLinkCommand();
    private sealed class SupportLinkCommand:System.Windows.Input.ICommand
    {
        public bool CanExecute(object? parameter)=>true;
        public void Execute(object? parameter)=>Process.Start(new ProcessStartInfo(Product.SupportUrl){UseShellExecute=true});
        public event EventHandler? CanExecuteChanged{add{} remove{}}
    }
    private CostStatistics? costChartReport;
    private void StatisticsPeriodChanged(object sender,RoutedEventArgs e)
    {if(StatisticsFrom!=null)StatisticsFrom.IsEnabled=StatisticsAllTime.IsChecked!=true;}
    private sealed record StatisticsRow(CostUserRow Value)
    {
        public string Name=>Value.Name;
        public string Initial=>Name.Length==0?"?":System.Globalization.StringInfo.GetNextTextElement(Name).ToUpperInvariant();
        public string Currency=>Value.Currency;
        public decimal Spent=>Value.Spent;
        public decimal SeparateHosts=>Value.SeparateHosts;
        public decimal Saved=>Value.Saved;
        public double SavingsPercent=>SeparateHosts<=0?0:(double)(Saved/SeparateHosts)*100;
    }
    private void RenderCostStatistics(CostStatistics report)
    {
        var rows=report.Users.Select(u=>new StatisticsRow(u)).ToList();
        if(StatisticsUsers.ItemsSource is not List<StatisticsRow> previous || !previous.SequenceEqual(rows))StatisticsUsers.ItemsSource=rows;
        StatisticsTotals.Text=string.Join("\n",report.Users.GroupBy(u=>u.Currency).Select(g=>g.Sum(u=>u.Spent).ToString("F3")+" "+g.Key));
        StatisticsSavings.Text=string.Join("\n",report.Users.GroupBy(u=>u.Currency).Select(g=>L("savedMoney")+": "+g.Sum(u=>u.Saved).ToString("F3")+" "+g.Key));
        StatisticsNotes.Text=report.HistoryIncomplete?L("statisticsIncomplete"):"";
        if(report.Users.Count==0)StatisticsTotals.Text=L("statisticsEmpty");
        DrawCostChart();
    }
    private void StatisticsChartResized(object sender,SizeChangedEventArgs e)=>DrawCostChart();
    private void DrawCostChart()
    {
        if(StatisticsChart==null)return;
        StatisticsChart.Children.Clear();
        if(costChartReport is not {} report || report.Hours.Count==0 || report.Hours.Select(h=>h.Currency).Distinct().Count()!=1)return;
        var hours=report.Hours.OrderBy(h=>h.At).ToList();var width=StatisticsChart.ActualWidth;var height=StatisticsChart.ActualHeight;
        if(width<40 || height<40)return;
        var max=Math.Max(hours.Sum(h=>h.Rate*Math.Max(1,h.Users.Count)),0.01m);
        for(var n=0;n<4;n++)
        {
            var y=16+(height-42)*n/3;
            StatisticsChart.Children.Add(new System.Windows.Shapes.Line{X1=0,X2=width,Y1=y,Y2=y,Stroke=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(49,54,43)),StrokeDashArray=new System.Windows.Media.DoubleCollection{2,4}});
        }
        var paid=new System.Windows.Shapes.Polyline{Stroke=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(178,255,89)),StrokeThickness=2};
        var separate=new System.Windows.Shapes.Polyline{Stroke=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(214,193,68)),StrokeThickness=1.5};
        decimal paidValue=0,separateValue=0;
        paid.Points.Add(new(0,height-26));separate.Points.Add(new(0,height-26));
        for(var n=0;n<hours.Count;n++)
        {
            paidValue+=hours[n].Rate;separateValue+=hours[n].Rate*Math.Max(1,hours[n].Users.Count);
            var x=width*(n+1)/hours.Count;
            paid.Points.Add(new(x,height-26-(double)(paidValue/max)*(height-42)));
            separate.Points.Add(new(x,height-26-(double)(separateValue/max)*(height-42)));
        }
        StatisticsChart.Children.Add(separate);StatisticsChart.Children.Add(paid);
        var legend=new System.Windows.Controls.TextBlock{Text=L("spent")+" / "+L("separateHostsCost")+" · "+hours[0].Currency,FontSize=11,Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(163,172,154))};
        System.Windows.Controls.Canvas.SetTop(legend,height-20);StatisticsChart.Children.Add(legend);
    }
    private void IgnoreServerSelection(object sender,System.Windows.Input.MouseButtonEventArgs e)
    {
        for(var node=e.OriginalSource as DependencyObject;node!=null;node=System.Windows.Media.VisualTreeHelper.GetParent(node))
            if(node is System.Windows.Controls.DataGridRow){e.Handled=true;break;}
    }
    private async void StatisticsClick(object sender,RoutedEventArgs e)=>await Safe(LoadStatistics);
    private async Task LoadStatistics()
    {
        if(session?.IsAdmin!=true)return;
        var suffix=StatisticsAllTime.IsChecked==true?"":"?from="+Uri.EscapeDataString(new DateTimeOffset(StatisticsFrom.SelectedDate??DateTime.Today).ToUniversalTime().ToString("O"));
        using var response=await http!.GetAsync("api/admin/statistics"+suffix);await Check(response);
        var report=(await response.Content.ReadFromJsonAsync<CostStatistics>())!;
        costChartReport=report;RenderCostStatistics(report);
    }
    private async void GithubClick(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        using var client=new System.Net.Http.HttpClient{Timeout=TimeSpan.FromSeconds(20)};client.DefaultRequestHeaders.UserAgent.ParseAdd("SquadHostManager/0.1");
        using var response=await client.GetAsync("https://api.github.com/repos/"+Product.Repository);
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound){UpdateStatus.Text=Status.Text=L("updatesUnavailable");return;}
        response.EnsureSuccessStatusCode();Open("https://github.com/"+Product.Repository);
    });
    private void SupportClick(object sender,RoutedEventArgs e)=>Process.Start(new ProcessStartInfo(Product.SupportUrl){UseShellExecute=true});
}
