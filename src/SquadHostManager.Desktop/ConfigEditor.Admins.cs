using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SquadHostManager.Desktop;

public sealed partial class ConfigEditor
{
    private sealed record Group(string Name,string Permissions);
    private sealed record Member(string SteamId,string Group);
    private void BuildAdminForm()
    {
        var groups=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,HeadersVisibility=DataGridHeadersVisibility.Column,EnableRowVirtualization=false,SelectionMode=DataGridSelectionMode.Single};
        var members=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,HeadersVisibility=DataGridHeadersVisibility.Column,EnableRowVirtualization=false,SelectionMode=DataGridSelectionMode.Single};
        foreach(var table in new[]{groups,members}){ScrollViewer.SetVerticalScrollBarVisibility(table,ScrollBarVisibility.Disabled);ScrollViewer.SetHorizontalScrollBarVisibility(table,ScrollBarVisibility.Disabled);}
        DataGridTextColumn Column(string key,string binding){var c=new DataGridTextColumn{Header=Localization.T(key),Binding=new Binding(binding),Width=new DataGridLength(1,DataGridLengthUnitType.Star)};var cell=new Style(typeof(TextBlock));cell.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.Wrap));c.ElementStyle=cell;return c;}
        groups.Columns.Add(Column("adminGroup","Name"));groups.Columns.Add(Column("permissions","Permissions"));members.Columns.Add(new DataGridTextColumn{Header="Steam ID",Binding=new Binding("SteamId"),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});members.Columns.Add(Column("adminGroup","Group"));
        form.Children.Add(Text("adminGroup"));groups.Margin=new(0,8,0,14);form.Children.Add(groups);
        var groupName=new TextBox{MaxLength=40,Margin=new(0,6,0,12)};form.Children.Add(Text("groupEditHelp"));form.Children.Add(groupName);
        var flags=new System.Windows.Controls.Primitives.UniformGrid{Columns=2};
        flags.Margin=new(0,0,0,8);form.Children.Add(flags);
        foreach(var permission in MainWindow.Permissions){var permissionLabel=Text("permission_"+permission);permissionLabel.MaxWidth=230;var box=new CheckBox{Tag=permission,Content=permissionLabel,Margin=new(0,0,14,10),ToolTip=permission};flags.Children.Add(box);}
        string[] unknown=[];
        var buttons=new WrapPanel{Margin=new(0,0,0,20)};form.Children.Add(buttons);
        void Button(Panel panel,string key,Action action){var b=new Button{Margin=new(0,0,8,8)};b.SetResourceReference(ContentControl.ContentProperty,key);b.Click+=(_,_)=>action();panel.Children.Add(b);}
        var selectedGroup=new ComboBox{MinWidth=180,Margin=new(0,6,0,12)};
        var steam=new TextBox{MaxLength=17,Margin=new(0,6,0,12)};
        void Refresh()
        {
            var name=groupName.Text;var selected=selectedGroup.SelectedItem as string;
            groups.ItemsSource=Regex.Matches(raw.Text,@"(?im)^\s*Group=([^:\r\n]+):([^\r\n]*)").Select(m=>new Group(m.Groups[1].Value.Trim(),m.Groups[2].Value.Split("//")[0].Trim())).ToList();
            selectedGroup.ItemsSource=((IEnumerable<Group>)groups.ItemsSource).Select(g=>g.Name).ToList();selectedGroup.SelectedItem=selected;
            members.ItemsSource=Regex.Matches(raw.Text,@"(?im)^\s*Admin=(\d+):([^\r\n]*)").Select(m=>new Member(m.Groups[1].Value,m.Groups[2].Value.Split("//")[0].Trim())).ToList();
            groupName.Text=name;
        }
        groups.SelectionChanged+=(_,_)=>{if(groups.SelectedItem is not Group group)return;groupName.Text=group.Name;var perms=group.Permissions.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);unknown=perms.Except(MainWindow.Permissions).ToArray();foreach(CheckBox b in flags.Children)b.IsChecked=perms.Contains((string)b.Tag);selectedGroup.SelectedItem=group.Name;};
        Button(buttons,"setGroup",()=>
        {
            var name=groupName.Text.Trim();var permissions=string.Join(',',flags.Children.Cast<CheckBox>().Where(b=>b.IsChecked==true).Select(b=>(string)b.Tag).Concat(unknown));
            if(!Regex.IsMatch(name,@"^[\p{L}\p{N}_ -]{1,40}$") || permissions.Length==0){status.Text=Localization.T("groupEditHelp");return;}
            var pattern=@"(?im)^\s*Group="+Regex.Escape(name)+@":[^\r\n]*";var line="Group="+name+":"+permissions;
            raw.Text=Regex.IsMatch(raw.Text,pattern)?Regex.Replace(raw.Text,pattern,_=>line):raw.Text.TrimEnd()+"\n"+line+"\n";Refresh();selectedGroup.SelectedItem=name;
        });
        Button(buttons,"remove",()=>
        {
            var name=groupName.Text.Trim();if(name.Length==0)return;
            if(Regex.IsMatch(raw.Text,@"(?im)^\s*Admin=\d+:"+Regex.Escape(name)+@"(?:\s*(?://.*)?)?$")){status.Text=Localization.T("groupInUse");return;}
            raw.Text=Regex.Replace(raw.Text,@"(?im)^\s*Group="+Regex.Escape(name)+@":[^\r\n]*\r?\n?","");groupName.Clear();Refresh();
        });
        form.Children.Add(Text("gameAdmins"));members.Margin=new(0,8,0,14);form.Children.Add(members);form.Children.Add(Text("adminAddHelp"));form.Children.Add(new TextBlock{Text="Steam ID"});form.Children.Add(steam);form.Children.Add(Text("adminGroup"));form.Children.Add(selectedGroup);
        members.SelectionChanged+=(_,_)=>{if(members.SelectedItem is Member member){steam.Text=member.SteamId;selectedGroup.SelectedItem=member.Group;}};
        var adminButtons=new WrapPanel();form.Children.Add(adminButtons);
        Button(adminButtons,"addAdmin",()=>
        {
            var id=steam.Text.Trim();var group=selectedGroup.SelectedItem as string;
            if(!Regex.IsMatch(id,@"^7656119\d{10}$") || string.IsNullOrEmpty(group)){status.Text=Localization.T("invalidAdmin");return;}
            raw.Text=Regex.Replace(raw.Text,@"(?im)^\s*Admin="+Regex.Escape(id)+@":[^\r\n]*\r?\n?","").TrimEnd()+"\nAdmin="+id+":"+group+"\n";steam.Clear();Refresh();
        });
        Button(adminButtons,"removeAdmin",()=>{var id=steam.Text.Trim();if(!Regex.IsMatch(id,@"^\d{17}$")){status.Text=Localization.T("invalidAdmin");return;}raw.Text=Regex.Replace(raw.Text,@"(?im)^\s*Admin="+Regex.Escape(id)+@":[^\r\n]*\r?\n?","");steam.Clear();Refresh();});
        Refresh();
    }
}
