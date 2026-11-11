using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentLauncher.App;
public sealed class ArgumentEditor : StackPanel
{
    private readonly List<string> values;
    private readonly StackPanel rows=new();
    public string[] Arguments=>values.ToArray();
    public ArgumentEditor(IEnumerable<string> arguments)
    {
        values=arguments.ToList();
        Children.Add(new TextBlock{Text="每项都是一个完整参数，不需要外层引号。空白项表示空参数；没有参数时删除全部项。",Style=(Style)Application.Current.Resources["Caption"],Margin=new(0,0,0,8)});
        Children.Add(rows);
        var add=new Button{Content="添加参数",HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,4,0,12)};
        AutomationProperties.SetAutomationId(add,"AddArgument");add.Click+=(_,_)=>{values.Add("");Render(values.Count-1);};Children.Add(add);Render();
    }
    private void Render(int focus=-1)
    {
        rows.Children.Clear();
        if(values.Count==0)rows.Children.Add(new TextBlock{Text="没有额外参数，使用 CLI 默认行为。",Style=(Style)Application.Current.Resources["Caption"],Margin=new(0,0,0,8)});
        for(var i=0;i<values.Count;i++)
        {
            var index=i;var row=new Grid{Margin=new(0,0,0,8)};
            row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
            var box=new TextBox{Text=values[i],AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=120,Margin=new(0,0,8,0)};
            AutomationProperties.SetName(box,$"参数 {i+1}");AutomationProperties.SetAutomationId(box,$"Argument{i+1}");box.TextChanged+=(_,_)=>values[index]=box.Text;row.Children.Add(box);
            var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Top};Grid.SetColumn(actions,1);row.Children.Add(actions);
            AddAction("↑","上移",i>0,()=>Move(index,index-1));AddAction("↓","下移",i<values.Count-1,()=>Move(index,index+1));AddAction("移除","移除",true,()=>{values.RemoveAt(index);Render(Math.Min(index,values.Count-1));});
            rows.Children.Add(row);
            if(index==focus)box.Loaded+=(_,_)=>{box.Focus();box.BringIntoView();};
            void AddAction(string caption,string name,bool enabled,Action action)
            {
                var button=new Button{Content=caption,IsEnabled=enabled,MinWidth=36,Padding=new(8,4,8,4),Margin=new(0,0,4,0)};AutomationProperties.SetName(button,$"第 {index+1} 个参数{name}");button.Click+=(_,_)=>action();actions.Children.Add(button);
            }
        }
    }
    private void Move(int from,int to){(values[from],values[to])=(values[to],values[from]);Render(to);}
    public void Replace(IEnumerable<string> arguments){values.Clear();values.AddRange(arguments);Render();}
}
