using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Controls;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;

public partial class MainWindow
{
    private ConfigEditor? configurationEditor;
    private string? configurationContext;
    private bool configurationForAdmins;
    private string ConfigurationContext => BaseUri()+"|"+session?.UserId;
    private async void BuildConfigurationPage(bool admins=false)
    {
        if(http==null || session==null)return;
        if(configurationEditor!=null)await configurationEditor.Flush();
        ConfigEditor.MigrateLegacyDrafts(ConfigurationContext,pool?.Instances.Where(i=>i.IsMine).Select(i=>i.Id)??[]);
        ConfigurationHost.Content=null;
        var panel=new DockPanel{Margin=new(24)};
        var client=http;
        configurationEditor=new ConfigEditor(ConfigurationContext+"|defaults",async request=>
        {
            using var response=await client.PostAsJsonAsync("api/server-defaults/config",request);await Check(response);
            var result=await response.Content.ReadFromJsonAsync<JsonElement>();if(request.Content!=null && result.TryGetProperty("failed",out var failed) && failed.GetArrayLength()>0)throw new ConfigApplyException(string.Join(", ",failed.EnumerateArray().Select(i=>i.GetString())));return result.GetProperty("output").GetString()??"";
        },true,async file=>{using var response=await client.GetAsync("api/server-defaults/versions?file="+Uri.EscapeDataString(file));await Check(response);return (await response.Content.ReadFromJsonAsync<List<ConfigRevision>>())??[];},async file=>{using var response=await client.GetAsync("api/server-defaults/stock?file="+Uri.EscapeDataString(file));await Check(response);return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("output").GetString()??"";});
        configurationEditor.Saved+=(file,text)=>
        {
            if(file=="Server.cfg" && Mine==null){MaxPlayers.Text=SquadConfig.Get(text,"MaxPlayers")??MaxPlayers.Text;SaveServerDraft();}
            if(file=="Admins.cfg"){AdminsEditor.Text=text;adminsOriginal=text;adminsLoadedId=AdminTarget;AdminEditorPanel.IsEnabled=true;}
        };
        if(admins)configurationEditor.RestrictToAdmins();
        panel.Children.Add(configurationEditor);if(admins)LegacyAdminsTab.Content=panel;else ConfigurationHost.Content=panel;configurationContext=ConfigurationContext;configurationForAdmins=admins;
    }
    private void OpenConfiguration()
    {
        Tabs.SelectedItem=ConfigurationTab;
        if(configurationEditor==null || configurationContext!=ConfigurationContext)BuildConfigurationPage();
    }
}
