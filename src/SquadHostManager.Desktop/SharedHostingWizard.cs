using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;
public partial class MainWindow
{
    private async Task ConnectSharedOwner(SharedHostingProfile profile)
    {
        Endpoint.Text=profile.Endpoint;certificateSha256=profile.CertificateSha256;rememberedInvitation=null;
        using var client=HostingTransport.Client(new(profile.Endpoint),profile.CertificateSha256);
        await Connect(await Post<SessionResponse>("api/login",new LoginRequest("admin",profile.AdminPassword),client));
    }
    private void RefreshSharedProfiles()
    {
        try{var profiles=SharedHostingProfiles.Read();SharedOwners.ItemsSource=profiles;SharedOwners.SelectedIndex=profiles.Count>0?0:-1;SharedOwners.Visibility=profiles.Count>1?Visibility.Visible:Visibility.Collapsed;SharedOwnerLabel.Text=profiles.Count==1?profiles[0].Label:"";SharedOwnerEntry.Visibility=profiles.Count>0?Visibility.Visible:Visibility.Collapsed;}
        catch{SharedOwnerEntry.Visibility=Visibility.Collapsed;}
    }
    private async void SharedOwnerLoginClick(object sender,RoutedEventArgs e)=>await Safe(async()=>{if(SharedOwners.SelectedItem is SharedHostingProfile profile)await ConnectSharedOwner(profile);});
    private void RemoveSharedHostingClick(object sender,RoutedEventArgs e)
    {
        var profile=SharedHostingProfiles.Read().FirstOrDefault(p=>p.Endpoint==Endpoint.Text) ?? SharedOwners.SelectedItem as SharedHostingProfile;
        if(profile==null)return;
        new SharedHostingWizard(this,_=>Task.CompletedTask,profile,async()=>{SharedHostingProfiles.Remove(profile.Endpoint);RefreshSharedProfiles();if(Endpoint.Text==profile.Endpoint)await DisconnectLocal();Status.Text=L("sharedRemoved");}).ShowDialog();
    }
    private void SharedHostingClick(object sender,RoutedEventArgs e)=>new SharedHostingWizard(this,async profile=>
    {
        SharedHostingProfiles.Save(profile);RefreshSharedProfiles();await ConnectSharedOwner(profile);Tabs.SelectedItem=AdminPanel;ShowConnectionDetails(profile);
    }).ShowDialog();
}

public sealed class SharedHostingWizard:Window
{
    private readonly SharedHostingDeployment deployment=new();
    private Func<HttpClient> accountHttp=()=>new HttpClient{Timeout=TimeSpan.FromSeconds(30)};
    private readonly Func<SharedHostingProfile,Task> completed;
    private readonly SharedHostingProfile? removal;
    private readonly Func<Task>? removed;
    private readonly StackPanel vds=new(),cherry=new(),review=new();
    private readonly TextBox address=new(){Name="VdsAddress"},user=new(){Text="root",Name="VdsUser"},sshPort=new(){Text="22",Name="VdsSshPort"},publicPort=new(){Text="18443",Name="VdsPublicPort"},keyPath=new(){Name="VdsKeyPath",IsReadOnly=true};
    private readonly PasswordBox password=new(){Name="VdsPassword"},keyPassword=new(){Name="VdsKeyPassword"},token=new(){Name="SharedCherryToken"};
    private readonly ComboBox auth=new(){Name="VdsAuthentication",SelectedIndex=0},projects=new(){Name="SharedCherryProjects",DisplayMemberPath="Name",Visibility=Visibility.Collapsed};
    private readonly StackPanel passwordPanel=new(),keyPanel=new(){Visibility=Visibility.Collapsed};
    private readonly TextBlock heading=new(){FontSize=26,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,16)},status=new(){Name="SharedSetupStatus",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)},summary=new(){Name="SharedSetupSummary",TextWrapping=TextWrapping.Wrap},fingerprint=new(){Name="VdsFingerprint",TextWrapping=TextWrapping.Wrap};
    private readonly Button next=new(){Name="SharedNext",MinWidth=160},back=new(){Name="SharedBack"},trust=new(){Name="VdsTrust",Visibility=Visibility.Collapsed},checkCherry=new(){Name="SharedCheckCherry"};
    private readonly ProgressBar activity=new(){Height=5,Margin=new Thickness(0,16,0,0),Visibility=Visibility.Collapsed};
    private readonly CancellationTokenSource cancellation=new();
    private string? sshFingerprint,observedFingerprint,checkedToken;
    private SharedHostingTarget? target;
    private VdsInspection? inspected;
    private int step;
    private bool working;
    private static string T(string key,params object?[] args)=>Localization.T(key,args);
    public SharedHostingWizard(Window owner,Func<SharedHostingProfile,Task> completed,SharedHostingProfile? removal=null,Func<Task>? removed=null)
    {
        this.completed=completed;this.removal=removal;this.removed=removed;Owner=owner;Title=T(removal==null?"sharedSetupTitle":"sharedRemoveTitle");Width=800;Height=880;MinHeight=600;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var layout=new DockPanel{Margin=new Thickness(32)};Content=layout;
        var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);
        footer.Children.Add(activity);footer.Children.Add(status);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,20,0,0)};buttons.Children.Add(back);buttons.Children.Add(next);back.Margin=new Thickness(0,0,12,0);next.SetResourceReference(StyleProperty,"PrimaryButton");footer.Children.Add(buttons);
        var body=new StackPanel();layout.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});body.Children.Add(heading);body.Children.Add(vds);body.Children.Add(cherry);body.Children.Add(review);
        void Help(Panel panel,string key){var text=new TextBlock{Text=T(key),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16)};panel.Children.Add(text);}
        void Field(Panel panel,string key,Control control){panel.Children.Add(new Label{Content=T(key),Target=control,Padding=new Thickness(0),Margin=new Thickness(0,0,0,6)});control.Margin=new Thickness(0,0,0,16);System.Windows.Automation.AutomationProperties.SetName(control,T(key));panel.Children.Add(control);}
        Help(vds,removal==null?"sharedVdsHelp":"sharedRemoveSshHelp");Field(vds,"sharedAddress",address);Field(vds,"sharedSshUser",user);
        auth.Items.Add(T("sharedPasswordAuth"));auth.Items.Add(T("sharedKeyAuth"));Field(vds,"sharedAuthentication",auth);
        Field(passwordPanel,"sharedSshPassword",password);vds.Children.Add(passwordPanel);
        var browse=new Button{Content=T("sharedChooseKey"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,12)};browse.Click+=(_,_)=>{var picker=new Microsoft.Win32.OpenFileDialog{Title=T("sharedChooseKey"),Filter="SSH key|*.*"};if(picker.ShowDialog(this)==true)keyPath.Text=picker.FileName;};keyPanel.Children.Add(browse);keyPanel.Children.Add(keyPath);Field(keyPanel,"sharedKeyPassword",keyPassword);vds.Children.Add(keyPanel);
        var advanced=new Expander{Header=T("advanced"),Margin=new Thickness(0,4,0,16)};var advancedFields=new StackPanel{Margin=new Thickness(0,12,0,0)};Field(advancedFields,"sharedSshPort",sshPort);Field(advancedFields,"sharedPublicPort",publicPort);advanced.Content=advancedFields;vds.Children.Add(advanced);
        vds.Children.Add(fingerprint);trust.Content=T("sharedTrustButton");trust.HorizontalAlignment=HorizontalAlignment.Left;vds.Children.Add(trust);
        auth.SelectionChanged+=(_,_)=>{passwordPanel.Visibility=auth.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;keyPanel.Visibility=auth.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed;Invalidate();};
        foreach(var field in new[]{address,user,sshPort,publicPort,keyPath})field.TextChanged+=(_,_)=>Invalidate();password.PasswordChanged+=(_,_)=>Invalidate();keyPassword.PasswordChanged+=(_,_)=>Invalidate();
        Help(cherry,"sharedCherryHelp");
        var register=new Button{Content=T("cherryRegister"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,12)};register.Click+=(_,_)=>OpenLink(Product.ReferralUrl);cherry.Children.Add(register);
        Help(cherry,"accountKeyHelp");var api=new Button{Content=T("openApiKeys"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,12)};api.Click+=(_,_)=>OpenLink("https://portal.cherryservers.com/settings/api-keys");cherry.Children.Add(api);
        Field(cherry,"cherryApiToken",token);checkCherry.Content=T("loadCherryProjects");checkCherry.HorizontalAlignment=HorizontalAlignment.Left;checkCherry.Margin=new Thickness(0,0,0,16);cherry.Children.Add(checkCherry);
        var projectHelp=new TextBlock{Text=T("projectExplained"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};cherry.Children.Add(projectHelp);cherry.Children.Add(projects);
        var portal=new Button{Content=T("openCherryPortal"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,12,0,0)};portal.Click+=(_,_)=>OpenLink("https://portal.cherryservers.com/");cherry.Children.Add(portal);
        token.PasswordChanged+=(_,_)=>{checkedToken=null;projects.ItemsSource=null;if(step==1)next.IsEnabled=false;};projects.SelectionChanged+=(_,_)=>{if(step==1)next.IsEnabled=checkedToken!=null && projects.SelectedItem is CherryProject;};
        checkCherry.Click+=async(_,_)=>await Work(async()=>{using var client=accountHttp();var list=await new CherryAccountClient(client).Inspect(token.Password,cancellation.Token);checkedToken=token.Password.Trim();projects.ItemsSource=list;projects.Visibility=list.Count>1?Visibility.Visible:Visibility.Collapsed;projects.SelectedItem=list.FirstOrDefault(p=>p.Id==inspected?.ProjectId && p.TeamId==inspected?.TeamId) ?? (list.Count==1?list[0]:null);status.Text=list.Count==0?T("noCherryProjects"):projects.SelectedItem is CherryProject chosen?chosen.Name:T("chooseCherryProject");});
        if(removal==null)Help(review,"sharedReviewHelp");review.Children.Add(summary);
        back.Content=T("sharedBack");back.Click+=(_,_)=>{if(!working && step>0)Show(removal!=null?0:step-1);};
        trust.Click+=async(_,_)=>{sshFingerprint=observedFingerprint;await CheckVds();};
        next.Click+=async(_,_)=>
        {
            if(step==0){await CheckVds();return;}
            if(step==1){if(projects.SelectedItem is not CherryProject project || checkedToken==null)return;summary.Text=T("sharedSummary",target!.Address,inspected!.System,target.Endpoint,project.Name);Show(2);return;}
            if(removal!=null)
            {
                var save=new Microsoft.Win32.SaveFileDialog{Title=T("sharedBackupTitle"),FileName="squad-hosting-backup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".tar.gz",Filter="Archive|*.tar.gz",OverwritePrompt=true};
                if(save.ShowDialog(this)!=true)return;
                if(File.Exists(save.FileName)){status.Text=T("sharedBackupNewFile");return;}
                await Work(async()=>{status.Text=T("sharedRemoving");await deployment.Remove(target!,Access(),removal,save.FileName,cancellation.Token);if(removed!=null)await removed();working=false;Close();});return;
            }
            await Work(async()=>
            {
                var progress=new Progress<HostingSetupProgress>(p=>{status.Text=T(p.Stage)+(p.UploadPercent is {} percent?$" {percent:0.0}%":"");activity.IsIndeterminate=p.UploadPercent==null;activity.Value=p.UploadPercent??0;});
                var profile=await deployment.Deploy(target!,Access(),(CherryProject)projects.SelectedItem,checkedToken!,progress,ct:cancellation.Token);
                SharedHostingProfiles.Save(profile);await completed(profile);working=false;Close();
            });
        };
        Closing+=(_,args)=>{if(working){args.Cancel=true;status.Text=T("waitClose");}else cancellation.Cancel();};Closed+=(_,_)=>{password.Clear();keyPassword.Clear();token.Clear();cancellation.Dispose();};Show(0);
        if(removal!=null){var endpoint=new Uri(removal.Endpoint);address.Text=endpoint.Host;publicPort.Text=endpoint.Port.ToString();sshFingerprint=removal.SshFingerprint;}
    }
    private void OpenLink(string url){try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url){UseShellExecute=true});}catch{status.Text=T("actionFailed");}}
    private void Invalidate(){inspected=null;sshFingerprint=observedFingerprint=null;trust.Visibility=Visibility.Collapsed;fingerprint.Text="";}
    private HostingSshAccess Access()=>new(auth.SelectedIndex==0?password.Password:null,auth.SelectedIndex==1?keyPath.Text:null,keyPassword.Password,sshFingerprint);
    private async Task CheckVds()=>await Work(async()=>
    {
        if(!int.TryParse(sshPort.Text,out var ssh)||!int.TryParse(publicPort.Text,out var service))throw new HostingSetupException("sharedInvalidPort");
        target=new(address.Text.Trim(),ssh,user.Text.Trim(),service);target.Validate();
        if(auth.SelectedIndex==1&&!File.Exists(keyPath.Text))throw new HostingSetupException("sharedChooseKey");
        if(auth.SelectedIndex==0&&password.Password.Length==0)throw new HostingSetupException("sharedPasswordRequired");
        status.Text=T("sharedCheckVds");
        try{inspected=await deployment.Inspect(target,Access(),cancellation.Token);trust.Visibility=Visibility.Collapsed;
            if(removal!=null){if(!inspected.ExistingInstallation || target.Endpoint.ToString()!=removal.Endpoint)throw new HostingSetupException("sharedRemovalMismatch");summary.Text=T("sharedRemoveSummary",target.Address,target.ServicePort);Show(2);}else Show(1);}
        catch(SshTrustRequired info){observedFingerprint=info.Fingerprint;fingerprint.Text=T("sharedTrustHelp",target.Address,info.Fingerprint);trust.Visibility=Visibility.Visible;status.Text=T("sharedTrustRequired");}
    });
    private async Task Work(Func<Task> action)
    {
        if(working)return;working=true;back.IsEnabled=next.IsEnabled=false;vds.IsEnabled=cherry.IsEnabled=review.IsEnabled=false;activity.Visibility=Visibility.Visible;activity.IsIndeterminate=true;
        try{await action();}
        catch(Exception error){status.Text=error is HostingSetupException setup?T(setup.MessageKey):error is CherryAccountException account?T(account.MessageKey):error is ArgumentException input?T(input.Message):T("sharedSetupFailed");}
        finally{working=false;back.IsEnabled=step>0;next.IsEnabled=step!=1 || checkedToken!=null && projects.SelectedItem is CherryProject;vds.IsEnabled=cherry.IsEnabled=review.IsEnabled=true;activity.Visibility=Visibility.Collapsed;}
    }
    private void Show(int value)
    {
        step=value;vds.Visibility=step==0?Visibility.Visible:Visibility.Collapsed;cherry.Visibility=step==1?Visibility.Visible:Visibility.Collapsed;review.Visibility=step==2?Visibility.Visible:Visibility.Collapsed;
        heading.Text=T(step==0?"sharedStepVds":removal!=null?"sharedRemoveTitle":step==1?"sharedStepCherry":"sharedStepInstall");next.Content=T(step==2?(removal!=null?"sharedRemoveButton":"sharedInstallButton"):"sharedNext");next.IsEnabled=step!=1 || checkedToken!=null && projects.SelectedItem is CherryProject;back.IsEnabled=step>0;status.Text="";
    }
}
