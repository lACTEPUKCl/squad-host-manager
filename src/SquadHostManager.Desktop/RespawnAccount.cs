using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using SquadHostManager.Core;
namespace SquadHostManager.Desktop;

public partial class MainWindow
{
    private Func<HttpClient> respawnAccountHttp=()=>new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(30)};
    private async void OwnRespawnClick(object sender,RoutedEventArgs e)
    {
        var editing=sender is FrameworkElement{Tag:"edit"};
        if(LocalCoordinator.Configured && LocalCoordinator.Read().Provider=="respawn" && !editing)
        {await Safe(ConnectPersonalAccount);return;}
        var dialog=new Window{Owner=this,Title=L("respawnOwnAccount"),Width=620,SizeToContent=SizeToContent.Height,MaxHeight=760,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new StackPanel{Margin=new(28)};
        var border=new Border{Child=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},CornerRadius=new(12),BorderThickness=new(1)};
        border.SetResourceReference(Border.BackgroundProperty,"PanelBrush");border.SetResourceReference(Border.BorderBrushProperty,"LineBrush");dialog.Content=border;
        panel.Children.Add(new TextBlock{Text=L("respawnOwnAccount"),FontSize=22,FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,16)});
        panel.Children.Add(new TextBlock{Text=L("respawnOwnHelp"),TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,18)});
        var register=new Button{Content=L("respawnRegister"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,0,0,12)};
        register.Click+=(_,_)=>Open(Product.RespawnRegistrationUrl);panel.Children.Add(register);
        var keys=new Button{Content=L("respawnOpenKeys"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,0,0,18)};
        keys.Click+=(_,_)=>Open("https://panel.respawnhost.com/en/api-keys");panel.Children.Add(keys);
        panel.Children.Add(new TextBlock{Text=L("respawnOwnerKeyHelp"),TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,8)});
        var token=new PasswordBox{Padding=new(10),Margin=new(0,0,0,18)};panel.Children.Add(token);
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,0)};
        var buttons=new WrapPanel();panel.Children.Add(buttons);
        var connect=new Button{Content=L("connectRespawnAccount"),Style=(Style)FindResource("PrimaryButton"),Margin=new(0,0,12,0)};buttons.Children.Add(connect);
        var cancel=new Button{Content=L("dialogCancel")};buttons.Children.Add(cancel);cancel.Click+=(_,_)=>dialog.Close();
        panel.Children.Add(new TextBlock{Text=L("noRentDuringSetup"),TextWrapping=TextWrapping.Wrap,Margin=new(0,16,0,0),Opacity=.7});panel.Children.Add(status);
        var working=false;using var cancellation=new CancellationTokenSource();
        dialog.Closing+=(_,args)=>{if(working){args.Cancel=true;status.Text=L("waitClose");}else cancellation.Cancel();};
        connect.Click+=async(_,_)=>
        {
            working=true;connect.IsEnabled=cancel.IsEnabled=token.IsEnabled=false;status.Text=L("checkingAccount");
            var previous=LocalCoordinator.Configured?LocalCoordinator.Read():null;var saved=false;
            try
            {
                using var client=respawnAccountHttp();var verified=await new RespawnAccountClient(client).Inspect(token.Password,cancellation.Token);
                var account=previous??new LocalCoordinator.Account("","","",Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
                LocalCoordinator.Save(account with{Provider="respawn",RespawnToken=verified});saved=true;
                await ConnectPersonalAccount();token.Clear();working=false;dialog.Close();Status.Text=L("respawnAccountReady");
            }
            catch(Exception error)
            {
                if(saved && previous!=null)LocalCoordinator.Save(previous);
                status.Text=error is RespawnAccountException rejected?L(rejected.MessageKey):error is InvalidOperationException or TimeoutException?error.Message:L("respawnAccountNetworkError");
            }
            finally{working=false;connect.IsEnabled=cancel.IsEnabled=token.IsEnabled=true;}
        };
        dialog.ShowDialog();
    }
}
