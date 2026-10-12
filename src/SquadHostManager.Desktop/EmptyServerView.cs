using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SquadHostManager.Desktop;
public sealed class EmptyServerView : UserControl
{
    public EmptyServerView()
    {
        SetBinding(VisibilityProperty,new Binding(nameof(MainWindow.NoServerHelpVisibility)){RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(Window),1)});
        var panel=new StackPanel();
        var heading=new TextBlock{FontSize=24,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12)};heading.SetResourceReference(TextBlock.TextProperty,"noServerTitle");panel.Children.Add(heading);
        var help=new TextBlock{TextWrapping=TextWrapping.Wrap,MaxWidth=580,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,24)};help.SetResourceReference(TextBlock.TextProperty,"noServerHelp");help.SetResourceReference(ForegroundProperty,"MutedBrush");panel.Children.Add(help);
        var button=new Button{HorizontalAlignment=HorizontalAlignment.Left};button.SetResourceReference(ContentProperty,"server");button.SetResourceReference(StyleProperty,"PrimaryButton");button.SetBinding(Button.CommandProperty,new Binding(nameof(MainWindow.ServerSetupShortcut)){RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(Window),1)});panel.Children.Add(button);
        var border=new Border{CornerRadius=new CornerRadius(16),Padding=new Thickness(32),Margin=new Thickness(24),VerticalAlignment=VerticalAlignment.Top,Child=panel};border.SetResourceReference(BackgroundProperty,"PanelBrush");Content=border;
    }
}
