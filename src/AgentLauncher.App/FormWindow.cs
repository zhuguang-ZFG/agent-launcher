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
    public Dictionary<string,string[]> ArgumentValues { get; }=[];
    public FormWindow(Window owner,string title,(string Key,string Label,string Value,bool Multiline)[] fields,string note,Dictionary<string,(string Value,string Label)[]>? choices=null,string[]? bools=null,Dictionary<string,string[]>? suggestions=null,Dictionary<string,string[]>? argumentLists=null,Dictionary<string,Action<Window>>? actions=null)
    {
        Owner=owner;Title=title;Width=Math.Min(660,SystemParameters.WorkArea.Width-32);Height=Math.Min(620,SystemParameters.WorkArea.Height-32);WindowStartupLocation=WindowStartupLocation.CenterOwner;
        // Implicit styles do not apply to derived Window classes; opt in explicitly or the dialog renders unthemed (white in dark mode).
        Style=(Style)Application.Current.Resources[typeof(Window)];
        var root=new DockPanel{Margin=new(24)};var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(footer,Dock.Bottom);
        var cancel=new Button{Content="取消",IsCancel=true};var apply=new Button{Content="应用",IsDefault=true};footer.Children.Add(cancel);footer.Children.Add(apply);root.Children.Add(footer);
        var panel=new StackPanel();panel.Children.Add(new TextBlock{Text=note,Margin=new(0,0,0,16),Foreground=(Brush)FindResource("Muted")});
        if(actions!=null){var actionRow=new WrapPanel{Margin=new(0,0,0,12)};foreach(var action in actions){var button=new Button{Content=action.Key,Margin=new(0,0,8,4)};button.Click+=(_,_)=>action.Value(this);actionRow.Children.Add(button);}panel.Children.Add(actionRow);}
        var readers=new Dictionary<string,Func<string>>();Control? first=null;
        var argumentReaders=new Dictionary<string,ArgumentEditor>();
        foreach(var f in fields)
        {
            if(argumentLists?.TryGetValue(f.Key,out var arguments)==true)
            {
                var editor=new ArgumentEditor(arguments);argumentReaders[f.Key]=editor;
                panel.Children.Add(new TextBlock{Text=f.Label,FontWeight=FontWeights.SemiBold,Margin=new(0,8,0,8)});panel.Children.Add(editor);continue;
            }
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
            if(suggestions?.TryGetValue(f.Key,out var found)==true)
            {
                // Discovered local installs offered as an editable dropdown; typing and 浏览 still work.
                var suggestLabel=new Label{Content=f.Label,Padding=new(0,4,0,0)};var suggestRow=new DockPanel();
                var combo=new ComboBox{IsEditable=true,IsTextSearchEnabled=false,Text=f.Value,Margin=new(0,4,0,12)};
                foreach(var path in found)combo.Items.Add(path);
                if(f.Key is "exe" or "terminal" or "cwd" or "worktree")
                {
                    var browse=new Button{Content="浏览",VerticalAlignment=VerticalAlignment.Top};DockPanel.SetDock(browse,Dock.Right);suggestRow.Children.Add(browse);
                    browse.Click+=(_,_)=>{if(f.Key is "cwd" or "worktree"){var picker=new OpenFolderDialog();if(picker.ShowDialog(this)==true)combo.Text=picker.FolderName;}else{var picker=new OpenFileDialog{Filter="程序|*.exe;*.cmd|所有文件|*.*"};if(picker.ShowDialog(this)==true)combo.Text=picker.FileName;}};
                }
                AutomationProperties.SetName(combo,f.Label);suggestLabel.Target=combo;readers[f.Key]=()=>combo.Text;panel.Children.Add(suggestLabel);suggestRow.Children.Add(combo);panel.Children.Add(suggestRow);first??=combo;continue;
            }
            var label=new Label{Content=f.Label,Padding=new(0,4,0,0)};var row=new DockPanel();
            var box=new TextBox{Text=f.Value,AcceptsReturn=f.Multiline,MinHeight=f.Multiline?80:36,TextWrapping=f.Multiline?TextWrapping.Wrap:TextWrapping.NoWrap,VerticalScrollBarVisibility=f.Multiline?ScrollBarVisibility.Auto:ScrollBarVisibility.Disabled,Margin=new(0,4,0,12)};
            if(f.Key is "exe" or "terminal" or "cwd" or "worktree")
            {
                var browse=new Button{Content="浏览",VerticalAlignment=VerticalAlignment.Top};DockPanel.SetDock(browse,Dock.Right);row.Children.Add(browse);
                browse.Click+=(_,_)=>{if(f.Key is "cwd" or "worktree"){var picker=new OpenFolderDialog();if(picker.ShowDialog(this)==true)box.Text=picker.FolderName;}else{var picker=new OpenFileDialog{Filter="程序|*.exe;*.cmd|所有文件|*.*"};if(picker.ShowDialog(this)==true)box.Text=picker.FileName;}};
            }
            if(f.Key=="cwd"){var inherit=new Button{Content="使用项目目录",VerticalAlignment=VerticalAlignment.Top};DockPanel.SetDock(inherit,Dock.Right);row.Children.Insert(0,inherit);inherit.Click+=(_,_)=>{box.Text="";Values["inheritDirectory"]="true";};}
            if(f.Key=="note"){var copy=new Button{Content="复制分工",VerticalAlignment=VerticalAlignment.Top};DockPanel.SetDock(copy,Dock.Right);row.Children.Insert(0,copy);copy.Click+=(_,_)=>{try{Clipboard.SetText(box.Text);}catch(System.Runtime.InteropServices.COMException){MessageBox.Show(this,"剪贴板正忙，请稍后重试。");}};}
            AutomationProperties.SetName(box,f.Label);label.Target=box;readers[f.Key]=()=>box.Text;panel.Children.Add(label);row.Children.Add(box);panel.Children.Add(row);first??=box;
        }
        root.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});Content=root;
        UiScale.Apply(this);
        apply.Click+=(_,_)=>{foreach(var pair in readers)Values[pair.Key]=pair.Value();foreach(var pair in argumentReaders)ArgumentValues[pair.Key]=pair.Value.Arguments;DialogResult=true;};
        Loaded+=(_,_)=>first?.Focus();
    }
}
