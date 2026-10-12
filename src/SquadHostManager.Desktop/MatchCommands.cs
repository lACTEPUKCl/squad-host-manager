using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;
public partial class MainWindow
{
    private sealed record MatchCommand(string Name,string Usage,string Description)
    {public string Label=>L("rcon_"+Name);}
    private static readonly HashSet<string> supportedRcon=new(StringComparer.OrdinalIgnoreCase){"ListPlayers","ListSquads","ShowCurrentMap","ShowNextMap","ListLayers","AdminKickById","AdminBanById","AdminWarnById","AdminBroadcast","AdminSetNextLayer","AdminChangeLayer","AdminEndMatch","AdminRestartMatch","AdminForceTeamChangeById","AdminReloadServerConfig"};
    private List<MatchCommand> matchCommands=[];
    private string? commandsLoadedId;
    private async Task LoadMatchCommands()
    {
        var mine=RequiredMine();if(commandsLoadedId==mine.Id)return;
        var response=await Post<JsonElement>($"api/instances/{mine.Id}/rcon",new CommandRequest("ListCommands 1"));
        if(Mine?.Id!=mine.Id)return;
        matchCommands=response.GetProperty("output").GetString()!.Split('\n').Select(l=>Regex.Match(l.Trim(),@"^(\S+)\s*(.*?)\s*\((.*)\)\s*$")).Where(m=>m.Success).Select(m=>new MatchCommand(m.Groups[1].Value,m.Groups[1].Value+" "+m.Groups[2].Value,m.Groups[3].Value)).ToList();
        matchCommands.RemoveAll(c=>!supportedRcon.Contains(c.Name));
        commandsLoadedId=mine.Id;MatchCommandPicker.ItemsSource=matchCommands;MatchCommandPicker.SelectedItem=matchCommands.FirstOrDefault(c=>c.Name=="ListPlayers")??matchCommands.FirstOrDefault(c=>!c.Name.StartsWith("Admin"));
        ResumeMatchButton.Visibility=Visibility.Collapsed;
        ResumeMatchButton.IsEnabled=ResumeMatchButton.Visibility==Visibility.Visible;
    }
    private void MatchCommandSearchChanged(object sender,TextChangedEventArgs e)
    { if(MatchCommandPicker!=null)MatchCommandPicker.ItemsSource=matchCommands.Where(c=>c.Label.Contains(MatchCommandSearch.Text,StringComparison.OrdinalIgnoreCase)||c.Usage.Contains(MatchCommandSearch.Text,StringComparison.OrdinalIgnoreCase)||c.Description.Contains(MatchCommandSearch.Text,StringComparison.OrdinalIgnoreCase)).ToList(); }
    private void MatchCommandSelected(object sender,SelectionChangedEventArgs e)
    {
        MatchParameters.Children.Clear();if(MatchRunButton!=null)MatchRunButton.IsEnabled=MatchCommandPicker.SelectedItem is MatchCommand && Mine?.GameState=="gameReady";if(MatchCommandPicker.SelectedItem is not MatchCommand command){MatchCommandHelp.Text=L("selectRconCommand");return;}
        MatchCommandHelp.Text=command.Name=="AdminBanById"?L("rconBanHelp"):command.Description;
        foreach(Match match in Regex.Matches(command.Usage,@"<([^<>]+)>"))
        {
            var field=new StackPanel{Margin=new Thickness(0,0,12,12)};
            field.Children.Add(new TextBlock{Text=L(match.Groups[1].Value switch{"PlayerId"=>"rconPlayerId","KickReason" or "BanReason" or "WarnReason"=>"rconReason","BanLength"=>"rconBanLength","Message"=>"rconMessage",_=>match.Groups[1].Value}),TextWrapping=TextWrapping.Wrap,MaxWidth=300});
            field.Children.Add(new TextBox{MinWidth=210,Padding=new Thickness(9,6,9,6)});MatchParameters.Children.Add(field);
        }
    }
    private async void RunMatchCommandClick(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        if(MatchCommandPicker.SelectedItem is not MatchCommand command)throw new InputException(L("selectRconCommand"));
        var args=MatchParameters.Children.Cast<StackPanel>().Select(p=>p.Children.OfType<TextBox>().Single().Text.Trim()).ToList();
        if(args.Any(string.IsNullOrWhiteSpace))throw new InputException(L("fillCommandParameters"));
        if(command.Name.StartsWith("Admin",StringComparison.Ordinal)&&!Confirm(command.Usage,L("match")))return;
        MatchCommandResult.Text=(await Post<JsonElement>($"api/instances/{RequiredMine().Id}/rcon",new CommandRequest(command.Name+(args.Count==0?"":" "+string.Join(' ',args))))).GetProperty("output").GetString();
    });
    private async void ResumeMatchClick(object sender,RoutedEventArgs e)=>await Safe(()=>Rcon("AdminUnpauseMatch"));
}
