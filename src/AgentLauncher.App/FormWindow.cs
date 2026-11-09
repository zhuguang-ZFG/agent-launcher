using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace AgentLauncher.App;
public sealed class FormWindow : Window
{
    public Dictionary<string,string> Values { get; }=[];
    public FormWindow(Window owner,string title,(string Key,string Label,string Value,bool Multiline)[] fields,string note,Dictionary<string,(string Value,string Label)[]>? choices=null,string[]? bools=null)
    {
        Owner=owner;Title=title;Width=Math.Min(660,SystemParameters.WorkArea.Width-32);Height=Math.Min(620,SystemParameters.WorkArea.Height-32);WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new(24)};var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(footer,Dock.Bottom);
        var cancel=new Button{Content="取消",IsCancel=true};var apply=new Button{Content="应用",IsDefault=true};footer.Children.Add(cancel);footer.Children.Add(apply);root.Children.Add(footer);
        var panel=new StackPanel();panel.Children.Add(new TextBlock{Text=note,Margin=new(0,0,0,16),Foreground=(Brush)FindResource("Muted")});
        var readers=new Dictionary<string,Func<string>>();Control? first=null;
        foreach(var f in fields)
        {
            if(choices?.TryGetValue(f.Key,out var options)==true)
            {
                var choiceLabel=new Label{Content=f.Label,Padding=new(0,4,0,0)};var combo=new ComboBox{Margin=new(0,4,0,12)};
                foreach(var (value,text) in options)combo.Items.Add(new ComboBoxItem{Content=text,Tag=value});
                var selected=Array.FindIndex(options,o=>o.Value==f.Value);
                if(selected<0){combo.Items.Add(new ComboBoxItem{Content=f.Value,Tag=f.Value});selected=combo.Items.Count-1;}
                combo.SelectedIndex=selected;
                AutomationProperties.SetName(combo,f.Label);choiceLabel.Target=combo;readers[f.Key]=()=>(combo.SelectedItem as ComboBoxItem)?.Tag as string??f.Value;
                panel.Children.Add(choiceLabel);panel.Children.Add(combo);first??=combo;continue;
            }
            if(bools?.Contains(f.Key)==true)
            {
                var check=new CheckBox{Content=f.Label,IsChecked="true".Equals(f.Value,StringComparison.OrdinalIgnoreCase),Margin=new(0,4,0,12)};
                readers[f.Key]=()=>check.IsChecked==true?"true":"false";panel.Children.Add(check);first??=check;continue;
            }
            var label=new Label{Content=f.Label,Padding=new(0,4,0,0)};var row=new DockPanel();
            var box=new TextBox{Text=f.Value,AcceptsReturn=f.Multiline,MinHeight=f.Multiline?80:36,TextWrapping=f.Multiline?TextWrapping.Wrap:TextWrapping.NoWrap,VerticalScrollBarVisibility=f.Multiline?ScrollBarVisibility.Auto:ScrollBarVisibility.Disabled,Margin=new(0,4,0,12)};
            if(f.Key is "exe" or "terminal" or "cwd")
            {
                var browse=new Button{Content="浏览",VerticalAlignment=VerticalAlignment.Top};DockPanel.SetDock(browse,Dock.Right);row.Children.Add(browse);
                browse.Click+=(_,_)=>{if(f.Key=="cwd"){var picker=new OpenFolderDialog();if(picker.ShowDialog(this)==true)box.Text=picker.FolderName;}else{var picker=new OpenFileDialog{Filter="程序|*.exe;*.cmd|所有文件|*.*"};if(picker.ShowDialog(this)==true)box.Text=picker.FileName;}};
            }
            AutomationProperties.SetName(box,f.Label);label.Target=box;readers[f.Key]=()=>box.Text;panel.Children.Add(label);row.Children.Add(box);panel.Children.Add(row);first??=box;
        }
        root.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});Content=root;
        apply.Click+=(_,_)=>{foreach(var pair in readers)Values[pair.Key]=pair.Value();DialogResult=true;};
        Loaded+=(_,_)=>first?.Focus();
    }
}
