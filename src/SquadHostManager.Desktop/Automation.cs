using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;
public partial class MainWindow
{
    private ListBox templateList=null!,noticeList=null!;
    private TextBox templateName=null!,budgetAmount=null!,budgetCurrency=null!;
    private CheckBox noticeSound=null!;
    private TextBlock featureCosts=null!,spentValue=null!,savedValue=null!,rateValue=null!,templatePreview=null!,templateEmpty=null!,noticeEmpty=null!,noticeSummary=null!,templateSource=null!,diagnosisText=null!,featureSelection=null!;
    private Button saveTemplateButton=null!,startTemplateButton=null!,deleteTemplateButton=null!,markNoticesButton=null!;
    private Diagnostic? lastDiagnosis;
    private Button? diagnosisNext;
    private string? featuresLoadedId;
    private bool budgetLoaded;
    private BudgetPolicy loadedBudget=new();
    private System.Windows.Controls.Primitives.Popup? noticePopup;
    private bool noticeBellDismissed;
    private int unreadNotices;
    private bool featureApiAvailable;
    private readonly HashSet<string> seenNotices=[];
    private sealed record TemplateRow(TemplateView Value){public override string ToString()=>Value.Name+"\n"+Value.Settings.Name+" · "+Value.Settings.MaxPlayers+" "+L("slots");}
    private sealed record NoticeRow(Notice Value){public override string ToString()=>L(Value.Code)+"\n"+Value.Detail+"\n"+Value.At.ToLocalTime().ToString("g")+(Value.Read?"":" · "+L("newEvent"));}
    private void BuildFeatures()
    {
        if(FeatureHost==null)return;
        var root=new DockPanel{Margin=new Thickness(24)};FeatureHost.Content=root;
        var top=new StackPanel{Visibility=Visibility.Collapsed};DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        featureSelection=new TextBlock{FontSize=24,FontWeight=FontWeights.SemiBold};featureSelection.SetResourceReference(ForegroundProperty,"TextBrush");top.Children.Add(featureSelection);
        var pages=new TabControl();pages.SetResourceReference(StyleProperty,"ToolSections");root.Children.Add(pages);
        StackPanel Page(string key)
        {var content=new StackPanel{Margin=new Thickness(0,0,0,0)};pages.Items.Add(new TabItem{Header=L(key),Content=new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});return content;}
        void Help(Panel p,string key){p.Children.Add(new TextBlock{Text=L(key),TextWrapping=TextWrapping.Wrap,MaxWidth=800,HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,0,0,18)});}
        void Field(Panel p,string key,Control control){p.Children.Add(new Label{Content=L(key),Target=control,Padding=new(0)});control.MaxWidth=420;control.HorizontalAlignment=HorizontalAlignment.Left;control.MinWidth=220;control.Margin=new(0,6,0,16);p.Children.Add(control);System.Windows.Automation.AutomationProperties.SetName(control,L(key));}
        void Action(Panel p,string key,Func<Task> run){var button=new Button{Content=L(key),HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,0,12,12)};button.Click+=async(_,_)=>await Safe(async()=>{await run();await LoadFeatures();});p.Children.Add(button);}
        async Task Send(string url,object value){using var response=await http!.PostAsJsonAsync(url,value);await Check(response);}
        string Selected(){if(Mine==null)throw new InputException(L("selectServer"));return Mine.Id;}
        TextBlock Text(string key,int size=14){var text=new TextBlock{Text=L(key),FontSize=size,FontWeight=size>=20?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12)};text.SetResourceReference(ForegroundProperty,size>=20?"TextBrush":"MutedBrush");return text;}
        Button Button(string key,Func<Task> run,bool primary=false){var button=new Button{Content=L(key),HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,0,12,12)};if(primary)button.SetResourceReference(StyleProperty,"PrimaryButton");button.Click+=async(_,_)=>{button.IsEnabled=false;try{await Safe(async()=>{await run();await LoadFeatures();});}finally{button.IsEnabled=true;UpdateFeatureActions();}};return button;}
        Grid Columns(Panel parent,UIElement left,UIElement right){var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(36)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});Grid.SetColumn(right,2);grid.Children.Add(left);grid.Children.Add(right);parent.Children.Add(grid);return grid;}
        var templates=Page("templates");Help(templates,"templatesHelp");
        var library=new StackPanel();library.Children.Add(Text("savedTemplates",20));templateEmpty=Text("templatesEmpty");library.Children.Add(templateEmpty);
        templateList=new(){MinHeight=100,MaxHeight=300};library.Children.Add(templateList);templatePreview=Text("selectTemplate");library.Children.Add(templatePreview);
        var templateActions=new WrapPanel();library.Children.Add(templateActions);
        startTemplateButton=Button("startTemplate",async()=>{if(templateList.SelectedItem is not TemplateRow row)return;if(pool?.Host.Id==null){Tabs.SelectedItem=SharedHostTab;throw new InputException(L("templateHostRequired"));}if(!Confirm(L("templateStartConfirm",row.Value.Name),L("startTemplate")))return;await Post<OperationView>($"api/templates/{row.Value.Id}/start",new TemplateStart(Guid.NewGuid()));await Refresh();},true);templateActions.Children.Add(startTemplateButton);
        deleteTemplateButton=Button("deleteTemplate",async()=>{if(templateList.SelectedItem is not TemplateRow row)return;if(!Confirm(L("deleteTemplateConfirm",row.Value.Name),L("deleteTemplate")))return;using var response=await http!.DeleteAsync("api/templates/"+row.Value.Id);await Check(response);});templateActions.Children.Add(deleteTemplateButton);
        templateList.SelectionChanged+=(_,_)=>{RenderTemplatePreview();UpdateFeatureActions();};
        var capture=new StackPanel();capture.Children.Add(Text("createTemplate",20));templateSource=Text("templateSourceMissing");capture.Children.Add(templateSource);templateName=new();Field(capture,"templateName",templateName);
        saveTemplateButton=Button("saveTemplate",async()=>{await Post<TemplateView>("api/templates",new TemplateRequest(templateName.Text,Selected()));templateName.Clear();},true);capture.Children.Add(saveTemplateButton);capture.Children.Add(Text("templateContents"));templateName.TextChanged+=(_,_)=>UpdateFeatureActions();Columns(templates,library,capture);
        var recovery=Page("diagnosticSection");Help(recovery,"diagnosticHelp");lastDiagnosis=null;
        Action(recovery,"diagnose",async()=>{using var response=await http!.GetAsync($"api/instances/{Selected()}/diagnosis");await Check(response);var result=(await response.Content.ReadFromJsonAsync<Diagnostic>())!;lastDiagnosis=result;if(diagnosisNext!=null){diagnosisNext.Visibility=result.Code=="gameReady"?Visibility.Collapsed:Visibility.Visible;diagnosisNext.Content=L(result.Action switch{"retry"=>"restartServer","chooseLayer"=>"match","connectionSettings"=>"settings",_=>result.Action});}diagnosisText.Text=L(result.Code)+(string.IsNullOrWhiteSpace(result.Detail)?"":"\n"+result.Detail);});diagnosisText=new(){TextWrapping=TextWrapping.Wrap,MaxWidth=800,HorizontalAlignment=HorizontalAlignment.Left};recovery.Children.Add(diagnosisText);diagnosisNext=Button("diagnosisAction",async()=>{if(lastDiagnosis==null)return;switch(lastDiagnosis.Action){case "retry":await Post<OperationView>($"api/instances/{Selected()}/restart",new RestartRequest(Guid.NewGuid()));await Refresh();break;case "mods":Tabs.SelectedIndex=4;break;case "files":Tabs.SelectedIndex=6;break;case "chooseLayer":Tabs.SelectedIndex=9;break;case "connectionSettings":Tabs.SelectedIndex=8;break;default:Tabs.SelectedItem=ConsoleTab;break;}});diagnosisNext.Visibility=Visibility.Collapsed;recovery.Children.Add(diagnosisNext);
        var costs=Page("myCosts");Help(costs,"personalCostsHelp");
        var figures=new Grid{Margin=new(0,8,0,24)};costs.Children.Add(figures);
        TextBlock Metric(string key,int column){figures.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});var panel=new StackPanel{Margin=new(0,0,24,0)};Grid.SetColumn(panel,column);figures.Children.Add(panel);var caption=Text(key,13);caption.SetResourceReference(ForegroundProperty,"MutedBrush");panel.Children.Add(caption);var value=new TextBlock{FontSize=28,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,8)};value.SetResourceReference(ForegroundProperty,"TextBrush");panel.Children.Add(value);return value;}
        spentValue=Metric("spent",0);savedValue=Metric("savedMoney",1);rateValue=Metric("currentHourlyShare",2);savedValue.SetResourceReference(ForegroundProperty,"AccentBrush");
        featureCosts=Text("statisticsEmpty",12);featureCosts.SetResourceReference(ForegroundProperty,"MutedBrush");costs.Children.Add(featureCosts);
        var budgetPanel=new StackPanel{Margin=new(0,24,0,0)};costs.Children.Add(budgetPanel);budgetPanel.Children.Add(Text("costAlert",20));Help(budgetPanel,"costAlertHelp");
        budgetAmount=new(){Text="0",MinWidth=120,MaxWidth=220};Field(budgetPanel,"budgetAmount",budgetAmount);budgetCurrency=new(){Text="EUR",IsReadOnly=true,Visibility=Visibility.Collapsed};budgetPanel.Children.Add(budgetCurrency);
        budgetPanel.Children.Add(Button("saveCostAlert",async()=>{if(!decimal.TryParse(budgetAmount.Text,out var amount))throw new InputException(L("invalidInput"));await Send("api/budget",new BudgetPolicy(amount,budgetCurrency.Text.Trim().ToUpperInvariant(),noticeSound.IsChecked==true));},true));
        if(noticePopup!=null)noticePopup.IsOpen=false;
        var notices=new StackPanel();
        System.Windows.Documents.TextElement.SetFontFamily(notices,FontFamily);System.Windows.Documents.TextElement.SetFontSize(notices,14);System.Windows.Documents.TextElement.SetForeground(notices,(System.Windows.Media.Brush)FindResource("TextBrush"));
        noticePopup=new System.Windows.Controls.Primitives.Popup{PlacementTarget=NoticeBell,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true,VerticalOffset=8,HorizontalOffset=-320};
        noticeBellDismissed=false;
        noticePopup.Closed+=(_,_)=>{noticeBellDismissed=NoticeBell.IsMouseOver && System.Windows.Input.Mouse.LeftButton==System.Windows.Input.MouseButtonState.Pressed;};
        var surface=new Border{Width=380,Padding=new Thickness(18),CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(1),Child=notices};surface.SetResourceReference(Border.BackgroundProperty,"PanelBrush");surface.SetResourceReference(Border.BorderBrushProperty,"LineBrush");noticePopup.Child=surface;
        surface.PreviewKeyDown+=(_,args)=>{if(args.Key==System.Windows.Input.Key.Escape){noticePopup.IsOpen=false;NoticeBell.Focus();args.Handled=true;}};
        notices.Children.Add(Text("notifications",20));
        var noticeToolbar=new DockPanel{Margin=new(0,0,0,8)};notices.Children.Add(noticeToolbar);noticeSummary=Text("notifications",13);
        markNoticesButton=Button("markRead",async()=>await Send("api/notices/read",new{}));markNoticesButton.HorizontalAlignment=HorizontalAlignment.Right;markNoticesButton.Padding=new Thickness(10,6,10,6);DockPanel.SetDock(markNoticesButton,Dock.Right);noticeToolbar.Children.Add(markNoticesButton);noticeToolbar.Children.Add(noticeSummary);
        noticeEmpty=Text("notificationsEmpty");notices.Children.Add(noticeEmpty);noticeList=new(){MaxHeight=330};notices.Children.Add(noticeList);
        var rowStyle=new Style(typeof(ListBoxItem));rowStyle.Setters.Add(new Setter(CursorProperty,System.Windows.Input.Cursors.Hand));
        noticeList.SelectionChanged+=async(_,_)=>{if(noticeList.SelectedItem is not NoticeRow row)return;noticeList.SelectedItem=null;await Safe(()=>OpenNotice(row.Value));};
        noticeSound=new(){Content=L("notificationSound"),Margin=new(0,20,0,12)};notices.Children.Add(noticeSound);
        noticeSound.Click+=async(_,_)=>await Safe(async()=>{await Send("api/budget",loadedBudget with{Sound=noticeSound.IsChecked==true});await LoadFeatures();});
        BuildServerTools(pages);
        foreach(var check in new[]{noticeSound})check.Content=new TextBlock{Text=check.Content?.ToString(),TextWrapping=TextWrapping.Wrap};
        foreach(var list in new[]{templateList,noticeList})list.SetResourceReference(StyleProperty,"FeatureListStyle");
        rowStyle.BasedOn=(Style)((Style)FindResource("FeatureListStyle")).Setters.OfType<Setter>().Single(setter=>setter.Property==ItemsControl.ItemContainerStyleProperty).Value;
        noticeList.ItemContainerStyle=rowStyle;
        featuresLoadedId=null;budgetLoaded=false;
    }
    private async Task OpenNotice(Notice notice)
    {
        if(noticePopup!=null)noticePopup.IsOpen=false;
        var target=pool?.Instances.FirstOrDefault(i=>i.IsMine && i.Id==notice.InstanceId);
        if(target!=null && (Mine?.Id!=target.Id || draft))
        {
            if(HasPendingChanges() && !Confirm(L("discardChanges"),L("selectedServer")))return;
            selectedId=target.Id;draft=false;displayedInstance=null;ClearInstanceViews();RenderPool();
        }
        if(notice.Code is "noticeHostPreparing" or "noticeSquadDownloading" or "noticeHostReady" or "noticeHostFailed")Tabs.SelectedItem=SharedHostTab;
        else if(notice.Code is "noticeBudget" or "noticeArchive")
        {
            Tabs.SelectedItem=FeaturesTab;
            var sections=(TabControl)((DockPanel)FeatureHost.Content).Children[1];
            sections.SelectedItem=sections.Items.Cast<TabItem>().FirstOrDefault(t=>t.Header?.ToString()==L(notice.Code=="noticeBudget"?"myCosts":"matchHistory"));
            await LoadFeatures();await LoadServerTools();
        }
        else if(target!=null && notice.Code is "noticeCrashed" or "noticeRecovering" or "noticeRecoveryLimit")Tabs.SelectedItem=ConsoleTab;
        else Tabs.SelectedItem=MyServersTab;
        TabChanged(Tabs,new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,Array.Empty<object>(),Array.Empty<object>()){Source=Tabs});
    }
    private async void NoticeBellClick(object sender,RoutedEventArgs e)
    {
        if(noticePopup==null || session==null)return;
        if(noticeBellDismissed){noticeBellDismissed=false;return;}
        noticePopup.IsOpen=!noticePopup.IsOpen;
        if(noticePopup.IsOpen){await Safe(LoadFeatures);if(noticePopup.IsOpen)noticeSound.Focus();}
    }
    private async Task LoadFeatures()
    {
        if(http==null || templateList==null)return;
        using var response=await http.GetAsync("api/features"+(Mine==null?"":"?instanceId="+Uri.EscapeDataString(Mine.Id)));await Check(response);var view=(await response.Content.ReadFromJsonAsync<AutomationView>())!;
        var templateId=(templateList.SelectedItem as TemplateRow)?.Value.Id;
        string RowKey(object row)=>row switch{TemplateRow t=>t.Value.Id+"|"+t,NoticeRow n=>n.Value.Id+"|"+n,_=>row.ToString()??""};
        void Update(ListBox list,List<object> rows){if(!list.Items.Cast<object>().Select(RowKey).SequenceEqual(rows.Select(RowKey)))list.ItemsSource=rows;}
        Update(templateList,view.Templates.Select(t=>(object)new TemplateRow(t)).ToList());Update(noticeList,view.Notices.Select(n=>(object)new NoticeRow(n)).ToList());
        templateList.SelectedItem=templateList.Items.Cast<TemplateRow>().FirstOrDefault(r=>r.Value.Id==templateId);
        featureSelection.Text=L("serverToolsTitle");
        if(featuresLoadedId!=Mine?.Id){featuresLoadedId=Mine?.Id;diagnosisText.Text="";lastDiagnosis=null;if(diagnosisNext!=null)diagnosisNext.Visibility=Visibility.Collapsed;}
        loadedBudget=view.Budget;
        if(!budgetLoaded){budgetLoaded=true;budgetAmount.Text=view.Budget.Amount.ToString();budgetCurrency.Text=view.Budget.Currency;budgetAmount.ToolTip=view.Budget.Currency;noticeSound.IsChecked=view.Budget.Sound;}
        OverviewSpent.Text=view.HostSpent is {} hostSpent?hostSpent.ToString("0.00")+" "+view.ForecastCurrency:"—";
        string Money(Func<CostUserRow,decimal> amount)=>view.Costs.Count==0?"—":string.Join("\n",view.Costs.GroupBy(c=>c.Currency).Select(g=>g.Sum(amount).ToString("F2")+" "+g.Key));
        spentValue.Text=Money(c=>c.Spent);savedValue.Text=Money(c=>c.Saved);rateValue.Text=view.HourlyForecast is {} rate?rate.ToString("F3")+" "+view.ForecastCurrency+" / "+L("hourShort"):"—";
        featureCosts.Text=L(view.Costs.Count==0?"statisticsEmpty":"personalCostsNote")+(view.HistoryIncomplete?"\n"+L("statisticsIncomplete"):"");
        templateEmpty.Visibility=view.Templates.Count==0?Visibility.Visible:Visibility.Collapsed;templateList.Visibility=view.Templates.Count==0?Visibility.Collapsed:Visibility.Visible;
        templateSource.Text=Mine==null?L("templateSourceMissing"):L("templateSource",Mine.Name);
        var unread=view.Notices.Count(n=>!n.Read);unreadNotices=unread;NoticeBadge.Visibility=unread>0?Visibility.Visible:Visibility.Collapsed;NoticeBadgeCount.Text=unread>99?"99+":unread.ToString();noticeSummary.Text=L("unreadCount",unread);markNoticesButton.IsEnabled=unread>0;noticeEmpty.Visibility=view.Notices.Count==0?Visibility.Visible:Visibility.Collapsed;noticeList.Visibility=view.Notices.Count==0?Visibility.Collapsed:Visibility.Visible;
        ((StackPanel)budgetAmount.Parent).Children.OfType<Label>().Single().Content=L("budgetAmount")+" ("+view.Budget.Currency+")";
        RenderTemplatePreview();UpdateFeatureActions();
        if(Tabs.SelectedItem==FeaturesTab)await LoadServerTools();
        var fresh=view.Notices.Where(n=>!n.Read && seenNotices.Add(n.Id)).ToList();if(fresh.Count>0){Status.Text=L(fresh[0].Code)+": "+fresh[0].Detail;if(view.Budget.Sound)System.Media.SystemSounds.Asterisk.Play();}
    }
    private void UpdateFeatureActions()
    {
        if(saveTemplateButton==null)return;
        saveTemplateButton.IsEnabled=Mine!=null && pool?.Host.Phase=="ready" && !string.IsNullOrWhiteSpace(templateName.Text);
        startTemplateButton.IsEnabled=templateList.SelectedItem is TemplateRow && pool?.Host.Phase=="ready" && pool?.Busy!=true;
        deleteTemplateButton.IsEnabled=templateList.SelectedItem is TemplateRow;
        markNoticesButton.IsEnabled=unreadNotices>0;
        startTemplateButton.ToolTip=pool?.Host.Phase=="ready"?L("templateStartHelp"):L("templateHostRequired");
    }
    private void RenderTemplatePreview()
    {
        if(templatePreview==null)return;
        if(templateList.SelectedItem is not TemplateRow row){templatePreview.Text=templateList.Items.Count==0?"":L("selectTemplate");return;}
        var settings=row.Value.Settings;
        templatePreview.Text=settings.Name+"\n"+L("slots")+": "+settings.MaxPlayers+"\n"+L("mods")+": "+(string.IsNullOrWhiteSpace(settings.Mods)?L("noMods"):settings.Mods)+"\n"+L("configs")+": "+row.Value.Configs.Count;
    }
    private async void AccessLimitsClick(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        var selected=SelectedUser();var value=(Users.ItemsSource as IEnumerable<UserRow>)!.First(r=>r.Value.Id==selected.Id).Value;
        var window=new Window{Owner=this,Title=L("accessLimits"),Width=540,Height=490,WindowStartupLocation=WindowStartupLocation.CenterOwner};var panel=new StackPanel{Margin=new(28)};window.Content=panel;
        panel.Children.Add(new TextBlock{Text=selected.Name,FontSize=22,Margin=new(0,0,0,16)});panel.Children.Add(new TextBlock{Text=L("maxServers")});var limit=new TextBox{Text=value.MaxServers.ToString(),Margin=new(0,6,0,16)};panel.Children.Add(limit);
        var expiry=new DatePicker{SelectedDate=value.ExpiresAt?.LocalDateTime.AddTicks(-1).Date,Margin=new(0,6,0,16)};panel.Children.Add(new TextBlock{Text=L("accessExpiry")});panel.Children.Add(expiry);var canRent=new CheckBox{Content=new TextBlock{Text=L("allowPaidRental"),TextWrapping=TextWrapping.Wrap},IsChecked=value.CanRent,Margin=new(0,0,0,16)};panel.Children.Add(canRent);var status=new TextBlock{TextWrapping=TextWrapping.Wrap};panel.Children.Add(status);var save=new Button{Content=L("save"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,16,0,0)};panel.Children.Add(save);
        save.Click+=async(_,_)=>{save.IsEnabled=false;try{if(!int.TryParse(limit.Text,out var max))throw new InputException(L("invalidInput"));var at=expiry.SelectedDate is {} day?new DateTimeOffset(DateTime.SpecifyKind(day.AddDays(1),DateTimeKind.Local)).ToUniversalTime():(DateTimeOffset?)null;using var response=await http!.PostAsJsonAsync($"api/admin/users/{selected.Id}/policy",new AccessPolicy(at,max,canRent.IsChecked==true));await Check(response);window.Close();await RefreshUsers();}catch(Exception error){status.Text=error.Message;}finally{save.IsEnabled=true;}};window.ShowDialog();
    });
}

