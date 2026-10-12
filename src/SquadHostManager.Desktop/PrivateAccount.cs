using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;
public partial class MainWindow
{
    private Func<HttpClient> cherryAccountHttp=()=>new HttpClient{Timeout=TimeSpan.FromSeconds(30)};
    private async Task ConnectPersonalAccount()
    {
        rememberedInvitation=null;
        certificateSha256=null;
        await LocalCoordinator.EnsureStarted();Endpoint.Text=LocalCoordinator.Endpoint;
        using var client=new HttpClient{BaseAddress=new(LocalCoordinator.Endpoint),Timeout=TimeSpan.FromSeconds(30)};
        var account=LocalCoordinator.Read();
        var login=await Post<SessionResponse>("api/login",new LoginRequest("admin",account.Password),client);
        client.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",login.Token);
        using var update=await client.PostAsJsonAsync("api/admin/cherry-key",new{account.Token,account.TeamId,account.ProjectId});await Check(update);
        await Connect(login);
    }
    private async void OwnCherryClick(object sender,RoutedEventArgs e)
    {
        var editing=sender is FrameworkElement{Tag:"edit"};
        if(LocalCoordinator.Configured && !editing){await Safe(ConnectPersonalAccount);return;}
        var dialog=new Window{Owner=this,Title=L("personalPath"),Width=740,Height=820,MinHeight=600,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new StackPanel{Margin=new Thickness(32)};dialog.Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        panel.Children.Add(new TextBlock{Text=L("accountStepOneHelp"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16)});
        panel.Children.Insert(0,new TextBlock{Text=L("accountStepOne"),FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12)});
        var register=new Button{Content=L("cherryRegister"),Margin=new Thickness(0,0,0,12)};register.Click+=CherryClick;panel.Children.Add(register);
        panel.Children.Add(new TextBlock{Text=L("accountStepTwo"),FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,16,0,12)});
        panel.Children.Add(new TextBlock{Text=L("accountKeyHelp"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16)});
        var portal=new Button{Content=L("openApiKeys"),Margin=new Thickness(0,0,0,12)};portal.Click+=(_,_)=>Process.Start(new ProcessStartInfo("https://portal.cherryservers.com/settings/api-keys"){UseShellExecute=true});panel.Children.Add(portal);
        panel.Children.Add(new TextBlock{Text=L("cherryApiToken")});var token=new PasswordBox{Padding=new Thickness(9),Margin=new Thickness(0,6,0,12)};panel.Children.Add(token);
        var projects=new ComboBox{DisplayMemberPath="Name",Padding=new Thickness(9),Margin=new Thickness(0,0,0,12)};var projectHelp=new TextBlock{Text=L("projectExplained"),TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed,Margin=new Thickness(0,0,0,12)};panel.Children.Add(projectHelp);panel.Children.Add(projects);projects.Visibility=Visibility.Collapsed;
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};
        var inspect=new Button{Content=L("loadCherryProjects"),Margin=new Thickness(0,0,0,12)};panel.Children.Add(inspect);panel.Children.Remove(projectHelp);panel.Children.Remove(projects);panel.Children.Add(projectHelp);panel.Children.Add(projects);
        var connect=new Button{Content=L("connectOwnAccount"),IsEnabled=false,Style=(Style)FindResource("PrimaryButton")};panel.Children.Add(connect);panel.Children.Add(new TextBlock{Text=L("noRentDuringSetup"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,16,0,0),Opacity=.7});panel.Children.Add(status);
        var projectPortal=new Button{Content=L("openCherryPortal"),Visibility=Visibility.Collapsed,Margin=new Thickness(0,12,0,0)};projectPortal.Click+=(_,_)=>Open("https://portal.cherryservers.com/");panel.Children.Add(projectPortal);
        string? validatedToken=null;var working=false;using var cancellation=new CancellationTokenSource();
        dialog.Closing+=(_,args)=>{if(working){args.Cancel=true;status.Text=L("waitClose");}else cancellation.Cancel();};
        projects.SelectionChanged+=(_,_)=>connect.IsEnabled=validatedToken!=null && projects.SelectedItem is CherryProject;
        token.PasswordChanged+=(_,_)=>{validatedToken=null;projects.ItemsSource=null;connect.IsEnabled=false;projects.Visibility=projectHelp.Visibility=Visibility.Collapsed;status.Text="";};
        inspect.Click+=async(_,_)=>
        {
            working=true;status.Text=L("checkingAccount");inspect.IsEnabled=false;connect.IsEnabled=false;token.IsEnabled=false;validatedToken=null;projects.ItemsSource=null;
            try
            {
                using var client=cherryAccountHttp();
                var list=await new CherryAccountClient(client).Inspect(token.Password,cancellation.Token);
                validatedToken=token.Password.Trim();projects.ItemsSource=list;projects.SelectedIndex=list.Count==1?0:-1;
                projects.Visibility=projectHelp.Visibility=list.Count>1?Visibility.Visible:Visibility.Collapsed;
                connect.IsEnabled=list.Count==1;status.Text=list.Count==0?L("noCherryProjects"):list.Count==1?list[0].Name:L("chooseCherryProject");
                projectPortal.Visibility=list.Count==0?Visibility.Visible:Visibility.Collapsed;
            }
            catch(CherryAccountException error){status.Text=L(error.MessageKey);}
            finally{working=false;inspect.IsEnabled=token.IsEnabled=true;}
        };
        connect.Click+=async(_,_)=>
        {
            if(projects.SelectedItem is not CherryProject project || validatedToken==null)return;working=true;status.Text=L("preparingAccount");connect.IsEnabled=inspect.IsEnabled=token.IsEnabled=projects.IsEnabled=false;
            try
            {
                var account=LocalCoordinator.Configured?LocalCoordinator.Read():new LocalCoordinator.Account(validatedToken,project.TeamId,project.Id,Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
                if(account.TeamId!=project.TeamId || account.ProjectId!=project.Id)throw new InvalidOperationException(L("privateAccountAlreadyConfigured"));
                account=account with{Token=validatedToken};
                LocalCoordinator.Save(account);await ConnectPersonalAccount();working=false;dialog.Close();Status.Text=L("accountReady");
            }
            catch(Exception error){status.Text=error is InvalidOperationException or TimeoutException?error.Message:L("accountNetworkError");}
            finally{working=false;connect.IsEnabled=inspect.IsEnabled=token.IsEnabled=projects.IsEnabled=true;}
        };
        dialog.ShowDialog();
    }
}
