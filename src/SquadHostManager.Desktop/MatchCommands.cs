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
    private static readonly HashSet<string> supportedRcon=new(StringComparer.OrdinalIgnoreCase){"ListPlayers","ListSquads","ShowCurrentMap","ShowNextMap","ListLayers","AdminKick","AdminBan","AdminWarn","AdminForceTeamChange","AdminKickById","AdminBanById","AdminWarnById","AdminBroadcast","AdminSetNextLayer","AdminChangeLayer","AdminClearNextLayer","AdminEndMatch","AdminRestartMatch","AdminForceTeamChangeById","AdminReloadServerConfig"};
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
        MatchCommandHelp.Text=command.Name is "AdminBan" or "AdminBanById"?L("rconBanHelp"):command.Description;
        foreach(var definition in RconCommands.Parameters(command.Usage))
        {
            var field=new StackPanel{Margin=new Thickness(0,0,12,12)};
            var parameter=definition.Name;
            var label=L(parameter.Contains("NameOr",StringComparison.Ordinal)?"rconPlayerIdentity":parameter switch{"PlayerId"=>"rconPlayerId","KickReason" or "BanReason" or "WarnReason"=>"rconReason","BanLength"=>"rconBanLength","Message"=>"rconMessage",_=>parameter});
            field.Children.Add(new TextBlock{Text=label+(definition.Optional?" "+L("rconOptional"):""),TextWrapping=TextWrapping.Wrap,MaxWidth=300});
            field.Children.Add(new TextBox{MinWidth=210,Padding=new Thickness(9,6,9,6)});MatchParameters.Children.Add(field);
        }
    }
    private async void RunMatchCommandClick(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        if(MatchCommandPicker.SelectedItem is not MatchCommand command)throw new InputException(L("selectRconCommand"));
        var args=MatchParameters.Children.Cast<StackPanel>().Select(p=>p.Children.OfType<TextBox>().Single().Text.Trim()).ToList();
        string text;try{text=RconCommands.FromUsage(command.Name,command.Usage,args);}catch(ArgumentException){throw new InputException(L("rconInvalidParameters"));}
        var mine=RequiredMine();
        if(command.Name.StartsWith("Admin",StringComparison.Ordinal)&&!Confirm(text,L("match")))return;
        if(Mine?.Id!=mine.Id)throw new InputException(L("playerChanged"));
        MatchCommandResult.Text=L("rconSending");
        try
        {
            var reply=(await Post<JsonElement>($"api/instances/{mine.Id}/rcon",new CommandRequest(text))).GetProperty("output").GetString()??"";
            if(Mine?.Id!=mine.Id)return;
            MatchCommandResult.Text=reply.Length==0?L("rconNoConfirmation"):reply;EnsureRconReply(reply);
        }
        catch{if(MatchCommandResult.Text==L("rconSending"))MatchCommandResult.Text=L("actionFailed");throw;}
    });
    private async void ResumeMatchClick(object sender,RoutedEventArgs e)=>await Safe(()=>Rcon("AdminUnpauseMatch"));
}
