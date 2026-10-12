using System.Windows;
using System.Windows.Controls;
namespace SquadHostManager.Desktop;
public static class AppDialog
{
    public static MessageBoxResult Show(Window owner,string message,string title="",MessageBoxButton buttons=MessageBoxButton.OK,MessageBoxImage icon=MessageBoxImage.None,MessageBoxResult defaultResult=MessageBoxResult.None)
    {
        var result=buttons==MessageBoxButton.YesNo?MessageBoxResult.No:MessageBoxResult.Cancel;
        var window=new Window{Owner=owner,Title=title,Width=520,SizeToContent=SizeToContent.Height,MaxHeight=SystemParameters.WorkArea.Height*.85,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,WindowStyle=WindowStyle.None,ShowInTaskbar=false};
        var panel=new StackPanel{Margin=new Thickness(24)};var border=new Border{BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Child=panel};border.SetResourceReference(Border.BackgroundProperty,"PanelBrush");border.SetResourceReference(Border.BorderBrushProperty,"LineBrush");window.Content=border;
        panel.Children.Add(new TextBlock{Text=title,FontSize=20,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16)});
        panel.Children.Add(new ScrollViewer{MaxHeight=SystemParameters.WorkArea.Height*.5,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap}});
        var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,24,0,0)};panel.Children.Add(actions);
        void Add(string key,MessageBoxResult value,bool primary=false){var button=new Button{Content=Localization.T(key),Margin=new Thickness(8,0,0,0),IsDefault=value==defaultResult,IsCancel=value==MessageBoxResult.Cancel || buttons==MessageBoxButton.YesNo && value==MessageBoxResult.No};if(primary)button.SetResourceReference(FrameworkElement.StyleProperty,"PrimaryButton");button.Click+=(_,_)=>{result=value;window.Close();};actions.Children.Add(button);}
        if(buttons==MessageBoxButton.OK)Add("dialogOk",MessageBoxResult.OK,true);else{if(buttons==MessageBoxButton.YesNoCancel)Add("dialogCancel",MessageBoxResult.Cancel);Add("dialogNo",MessageBoxResult.No);Add("dialogYes",MessageBoxResult.Yes,true);}
        window.PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape)window.Close();};window.ShowDialog();return result;
    }
}
