using System.IO;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;

public sealed partial class SharedHostingDeployment
{
    public async Task<SharedHostingProfile> Recover(SharedHostingTarget target,HostingSshAccess access,CancellationToken ct=default)
    {
        target.Validate();
        var profile=await Task.Run(()=>
        {
            ct.ThrowIfCancellationRequested();using var ssh=Open(target,access);var elevate=Elevation(ssh);
            var installations=Run(ssh,elevate+"sh -c "+Quote("if test -f "+target.Root+"/install.json; then printf '%s\\n' "+target.Root+"/install.json; else find /opt -maxdepth 2 -name install.json -path '/opt/squad-host-manager-app-*/install.json'; fi"));
            var paths=installations.Split('\n',StringSplitOptions.RemoveEmptyEntries);
            if(paths.Length!=1)throw new HostingSetupException("sharedConfigurationMissing");
            var match=System.Text.RegularExpressions.Regex.Match(paths[0],@"^/opt/squad-host-manager-app-(\d+)/install.json$");
            if(!match.Success || !int.TryParse(match.Groups[1].Value,out var detectedPort))throw new HostingSetupException("sharedConfigurationMissing");
            target=target with{ServicePort=detectedPort};target.Validate();
            using var marker=JsonDocument.Parse(Run(ssh,elevate+"cat "+Quote(target.Root+"/install.json")));
            if(marker.RootElement.GetProperty("application").GetString()!="squad-host-manager")throw new HostingSetupException("sharedConfigurationMissing");
            using var config=JsonDocument.Parse(Run(ssh,elevate+"cat "+Quote(target.Root+"/app/appsettings.Production.json")));
            var certificate=config.RootElement.GetProperty("Kestrel").GetProperty("Endpoints").GetProperty("Https").GetProperty("Certificate");
            using var cert=X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(Run(ssh,elevate+"base64 -w0 "+Quote(target.Root+"/private/tls.pfx"))),certificate.GetProperty("Password").GetString(),X509KeyStorageFlags.EphemeralKeySet);
            return new SharedHostingProfile(target.Endpoint.ToString(),Convert.ToHexString(SHA256.HashData(cert.RawData)),config.RootElement.GetProperty("Admin").GetProperty("Password").GetString()!,access.Fingerprint!,target.Address+":"+target.ServicePort);
        },ct);
        using var client=HostingTransport.Client(new(profile.Endpoint),profile.CertificateSha256);
        using var response=await client.PostAsJsonAsync("api/login",new LoginRequest("admin",profile.AdminPassword),ct);response.EnsureSuccessStatusCode();
        var session=await response.Content.ReadFromJsonAsync<SessionResponse>(cancellationToken:ct);
        if(session?.IsAdmin!=true)throw new HostingSetupException("sharedVerifyFailed");
        client.DefaultRequestHeaders.Authorization=new("Bearer",session.Token);using var logout=await client.PostAsync("api/logout",null,ct);logout.EnsureSuccessStatusCode();return profile;
    }
}

public partial class MainWindow
{
    public static readonly DependencyProperty CompactSidebarProperty=DependencyProperty.Register(nameof(CompactSidebar),typeof(bool),typeof(MainWindow),new PropertyMetadata(false));
    public bool CompactSidebar{get=>(bool)GetValue(CompactSidebarProperty);set=>SetValue(CompactSidebarProperty,value);}
    private void ConnectionDetailsClick(object sender,RoutedEventArgs e)
    {
        var profile=SharedHostingProfiles.Read().FirstOrDefault(p=>p.Endpoint==BaseUri().ToString());
        if(profile==null){AppDialog.Show(this,L("recoverConnectionHelp"),L("connectionDetails"));return;}
        ShowConnectionDetails(profile);
    }
    private void ShowConnectionDetails(SharedHostingProfile profile)
    {
        var dialog=new Window{Owner=this,Title=L("connectionDetails"),Width=540,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new StackPanel{Margin=new Thickness(24)};dialog.Content=panel;
        void Field(string key,string value){panel.Children.Add(new TextBlock{Text=L(key),Margin=new Thickness(0,0,0,6)});panel.Children.Add(new TextBox{Text=value,IsReadOnly=true,Margin=new Thickness(0,0,0,16)});}
        Field("managementAddress",profile.Endpoint);Field("username","admin");
        panel.Children.Add(new TextBlock{Text=L("accountPassword"),Margin=new Thickness(0,0,0,6)});var secret=new PasswordBox{Password=profile.AdminPassword,Margin=new Thickness(0,0,0,16)};panel.Children.Add(secret);
        var actions=new WrapPanel();panel.Children.Add(actions);
        var copy=new Button{Content=L("copyConnection"),Margin=new Thickness(0,0,8,8)};copy.Click+=(_,_)=>Clipboard.SetText(profile.Endpoint+"\nadmin\n"+profile.AdminPassword);actions.Children.Add(copy);
        var save=new Button{Content=L("saveConnection"),Margin=new Thickness(0,0,8,8)};save.Click+=(_,_)=>
        {
            if(!Confirm(L("connectionFileWarning"),L("saveConnection")))return;
            var picker=new Microsoft.Win32.SaveFileDialog{FileName="squad-hosting-connection.json",Filter="JSON|*.json"};if(picker.ShowDialog(dialog)!=true)return;
            try{File.WriteAllText(picker.FileName,JsonSerializer.Serialize(profile,new JsonSerializerOptions{WriteIndented=true}));}catch(Exception error){AppDialog.Show(dialog,error.Message,L("saveConnection"));}
        };actions.Children.Add(save);dialog.ShowDialog();
    }
    private async void ImportConnectionClick(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        var picker=new Microsoft.Win32.OpenFileDialog{Filter="JSON|*.json"};if(picker.ShowDialog(this)!=true)return;
        if(new FileInfo(picker.FileName).Length>16384)throw new InputException(L("invalidInput"));
        var profile=HostingConnectionBackup.Parse(await File.ReadAllTextAsync(picker.FileName));
        if(!Confirm(profile.Endpoint,L("importConnection")))return;
        await ConnectSharedOwner(profile);SharedHostingProfiles.Save(profile);RefreshSharedProfiles();
    });
    private void RecoverConnectionClick(object sender,RoutedEventArgs e)
    {
        var dialog=new Window{Owner=this,Title=L("recoverConnection"),Width=560,Height=660,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new StackPanel{Margin=new Thickness(24)};dialog.Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        panel.Children.Add(new TextBlock{Text=L("recoverConnectionHelp"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,20)});
        void Field(string key,Control control){panel.Children.Add(new Label{Content=L(key),Padding=new Thickness(0)});control.Margin=new Thickness(0,6,0,16);panel.Children.Add(control);}
        var address=new TextBox();var user=new TextBox{Text="root"};var password=new PasswordBox();var key=new TextBox();var keyPassword=new PasswordBox();var port=new TextBox{Text="18443"};var sshPort=new TextBox{Text="22"};
        Field("sharedAddress",address);Field("sharedSshUser",user);Field("sharedSshPassword",password);Field("sharedChooseKey",key);var browse=new Button{Content=L("sharedChooseKey"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,12)};browse.Click+=(_,_)=>{var picker=new Microsoft.Win32.OpenFileDialog();if(picker.ShowDialog(dialog)==true)key.Text=picker.FileName;};panel.Children.Add(browse);Field("sharedKeyPassword",keyPassword);Field("sharedSshPort",sshPort);Field("sharedPublicPort",port);
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap};panel.Children.Add(status);var action=new Button{Content=L("recoverConnection"),HorizontalAlignment=HorizontalAlignment.Left};panel.Children.Add(action);
        string? fingerprint=null;var working=false;dialog.Closing+=(_,args)=>args.Cancel=working;
        action.Click+=async(_,_)=>
        {
            if(!int.TryParse(port.Text,out var publicPort) || !int.TryParse(sshPort.Text,out var sshPortValue)){status.Text=L("sharedInvalidPort");return;}
            working=true;action.IsEnabled=false;
            try
            {
                var target=new SharedHostingTarget(address.Text.Trim(),sshPortValue,user.Text.Trim(),publicPort);
                var access=new HostingSshAccess(password.Password,string.IsNullOrWhiteSpace(key.Text)?null:key.Text.Trim(),keyPassword.Password,fingerprint);
                SharedHostingProfile profile;
                try{profile=await new SharedHostingDeployment().Recover(target,access);}
                catch(SshTrustRequired trust){if(!Confirm(L("sharedTrustRequired")+"\n"+trust.Fingerprint,L("recoverConnection")))return;fingerprint=trust.Fingerprint;profile=await new SharedHostingDeployment().Recover(target,access with{Fingerprint=fingerprint});}
                SharedHostingProfiles.Save(profile);RefreshSharedProfiles();await Safe(()=>ConnectSharedOwner(profile));working=false;dialog.Close();ShowConnectionDetails(profile);
            }
            catch(Exception error){status.Text=error is HostingSetupException setup?L(setup.MessageKey):error.Message;}
            finally{working=false;action.IsEnabled=true;}
        };
        dialog.Closed+=(_,_)=>{password.Clear();keyPassword.Clear();};dialog.ShowDialog();
    }
}
