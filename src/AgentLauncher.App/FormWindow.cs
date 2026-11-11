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
    private readonly Dictionary<string,ArgumentEditor> argumentEditors=[];
    /// <summary>Replaces one argument list in place so actions apply to the same editor the user sees.</summary>
    public void SetArguments(string key,string[] arguments)
    {
        if(!argumentEditors.TryGetValue(key,out var editor))throw new ArgumentException("没有该参数编辑器："+key,nameof(key));
        editor.Replace(arguments);ArgumentValues[key]=[..arguments];
    }
    public FormWindow(Window owner,string title,(string Key,string Label,string Value,bool Multiline)[] fields,string note,Dictionary<string,(string Value,string Label)[]>? choices=null,string[]? bools=null,Dictionary<string,string[]>? suggestions=null,Dictionary<string,string[]>? argumentLists=null,Dictionary<string,Action<Window>>? actions=null)
    {
        Owner=owner;Title=title;Width=Math.Min(660,SystemParameters.WorkArea.Width-32);Height=Math.Min(620,SystemParameters.WorkArea.Height-32);WindowStartupLocation=WindowStartupLocation.CenterOwner;
        // Implicit styles do not apply to derived Window classes; opt in explicitly or the dialog renders unthemed (white in dark mode).
        Style=(Style)Application.Current.Resources[typeof(Window)];
        var root=new DockPanel{Margin=new(24)};
        // Actions are docked outside the scrolling form so Enter/Esc keep the same reachable buttons at large text scales.
        var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,16,0,0)};DockPanel.SetDock(footer,Dock.Bottom);
        var cancel=new Button{Content="取消",IsCancel=true,Margin=new(0,0,8,0),MinWidth=88};var apply=new Button{Content="应用",IsDefault=true,MinWidth=88};footer.Children.Add(cancel);footer.Children.Add(apply);root.Children.Add(footer);
        var panel=new StackPanel();
        if(!string.IsNullOrEmpty(note))panel.Children.Add(new TextBlock{Text=note,Margin=new(0,0,0,16),Foreground=(Brush)FindResource("Muted")});
        if(actions!=null){var actionRow=new WrapPanel{Margin=new(0,0,0,12)};foreach(var action in actions){var button=new Button{Content=action.Key,Margin=new(0,0,8,4)};button.Click+=(_,_)=>action.Value(this);actionRow.Children.Add(button);}panel.Children.Add(actionRow);}
        var readers=new Dictionary<string,Func<string>>();Control? first=null;
        foreach(var f in fields)
        {
            if(argumentLists?.TryGetValue(f.Key,out var arguments)==true)
            {
                var editor=new ArgumentEditor(arguments);argumentEditors[f.Key]=editor;
                panel.Children.Add(new TextBlock{Text=f.Label,FontWeight=FontWeights.SemiBold,Margin=new(0,8,0,8)});panel.Children.Add(editor);continue;
            }
            if(choices?.TryGetValue(f.Key,out var options)==true)
            {
                var combo=new ComboBox();
                foreach(var (value,text) in options)combo.Items.Add(new ComboBoxItem{Content=text,Tag=value});
                var selected=Array.FindIndex(options,o=>o.Value==f.Value);
                if(selected<0){combo.Items.Add(new ComboBoxItem{Content=f.Value,Tag=f.Value});selected=combo.Items.Count-1;}
                combo.SelectedIndex=selected;
                AutomationProperties.SetName(combo,f.Label);readers[f.Key]=()=>(combo.SelectedItem as ComboBoxItem)?.Tag as string??f.Value;
                panel.Children.Add(FieldLabel(f.Label,combo));panel.Children.Add(FieldRow(combo));first??=combo;continue;
            }
            if(bools?.Contains(f.Key)==true)
            {
                var check=new CheckBox{Content=Wrapping(f.Label),IsChecked="true".Equals(f.Value,StringComparison.OrdinalIgnoreCase),Margin=new(0,4,0,12)};
                AutomationProperties.SetName(check,f.Label);
                readers[f.Key]=()=>check.IsChecked==true?"true":"false";panel.Children.Add(check);first??=check;continue;
            }
            if(suggestions?.TryGetValue(f.Key,out var found)==true)
            {
                // Discovered local installs offered as an editable dropdown; typing and the picker button still work.
                var combo=new ComboBox{IsEditable=true,IsTextSearchEnabled=false,Text=f.Value};
                foreach(var path in found)combo.Items.Add(path);
                AutomationProperties.SetName(combo,f.Label);readers[f.Key]=()=>combo.Text;
                panel.Children.Add(FieldLabel(f.Label,combo));
                if(f.Key is "exe" or "terminal" or "cwd" or "worktree")panel.Children.Add(FieldRow(combo,Picker(f.Label,f.Key is "cwd" or "worktree",path=>combo.Text=path)));
                else panel.Children.Add(FieldRow(combo));
                first??=combo;continue;
            }
            var box=new TextBox{Text=f.Value,AcceptsReturn=f.Multiline,MinHeight=f.Multiline?80:36,TextWrapping=f.Multiline?TextWrapping.Wrap:TextWrapping.NoWrap,HorizontalScrollBarVisibility=f.Multiline?ScrollBarVisibility.Disabled:ScrollBarVisibility.Hidden,VerticalScrollBarVisibility=f.Multiline?ScrollBarVisibility.Auto:ScrollBarVisibility.Disabled};
            AutomationProperties.SetName(box,f.Label);readers[f.Key]=()=>box.Text;
            var rowButtons=new List<Button>();
            if(f.Key=="cwd")rowButtons.Add(InheritButton(box));
            if(f.Key is "exe" or "terminal" or "cwd" or "worktree")rowButtons.Add(Picker(f.Label,f.Key is "cwd" or "worktree",path=>box.Text=path));
            if(f.Key=="note")rowButtons.Add(CopyButton(box));
            panel.Children.Add(FieldLabel(f.Label,box));panel.Children.Add(FieldRow(box,rowButtons.ToArray()));first??=box;
        }
        root.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});Content=root;
        UiScale.Apply(this);
        apply.Click+=(_,_)=>{foreach(var pair in readers)Values[pair.Key]=pair.Value();foreach(var pair in argumentEditors)ArgumentValues[pair.Key]=pair.Value.Arguments;DialogResult=true;};
        Loaded+=(_,_)=>first?.Focus();
        Label FieldLabel(string text,Control? target)=>new(){Target=target,Padding=new(0,4,0,0),Content=Wrapping(text)};
        Grid FieldRow(Control input,params Button[] buttons)
        {
            // Input stays the first logical child so Tab reaches the text before the side buttons; the star column keeps long values readable.
            var grid=new Grid{Margin=new(0,4,0,12)};
            grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            input.Margin=new(0);Grid.SetColumn(input,0);grid.Children.Add(input);
            if(buttons.Length>0)
            {
                var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Top,Margin=new(8,0,0,0)};
                for(var i=0;i<buttons.Length;i++){var button=buttons[i];button.VerticalAlignment=VerticalAlignment.Top;button.Margin=new(0,0,i==buttons.Length-1?0:8,0);actions.Children.Add(button);}
                Grid.SetColumn(actions,1);grid.Children.Add(actions);
            }
            return grid;
        }
        Button Picker(string fieldLabel,bool folder,Action<string> assign)
        {
            // File and folder pickers are labeled by their effect so the two kinds stay distinguishable.
            var button=new Button{Content=folder?"选择文件夹…":"选择文件…"};
            AutomationProperties.SetName(button,(folder?"选择文件夹：":"选择文件：")+fieldLabel);
            button.Click+=(_,_)=>{if(folder){var picker=new OpenFolderDialog();if(picker.ShowDialog(this)==true)assign(picker.FolderName);}else{var picker=new OpenFileDialog{Filter="程序|*.exe;*.cmd|所有文件|*.*"};if(picker.ShowDialog(this)==true)assign(picker.FileName);}};
            return button;
        }
        Button InheritButton(TextBox box)
        {
            var inherit=new Button{Content="使用项目目录"};AutomationProperties.SetName(inherit,"使用项目目录：清空并继承项目目录");
            inherit.Click+=(_,_)=>{box.Text="";Values["inheritDirectory"]="true";};
            return inherit;
        }
        Button CopyButton(TextBox box)
        {
            var copy=new Button{Content="复制分工"};AutomationProperties.SetName(copy,"复制分工备注到剪贴板");
            copy.Click+=(_,_)=>{try{Clipboard.SetText(box.Text);}catch(System.Runtime.InteropServices.COMException){MessageBox.Show(this,"剪贴板正忙，请稍后重试。");}};
            return copy;
        }
    }
    private static TextBlock Wrapping(string text)=>new(){Text=text,TextWrapping=TextWrapping.Wrap};
}
