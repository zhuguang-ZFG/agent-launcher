using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Tests;

internal static class PaneLayoutChecks
{
    private static readonly int[][] VerticalRows=
    [
        [1],[1,1],[1,2],[2,2],[2,1,1,1],[2,2,1,1],[2,2,2,1],[2,2,2,2],
        [3,2,2,2],[3,3,2,2],[3,3,3,2],[3,3,3,3],[4,3,3,3],[4,4,3,3],[4,4,4,3],[4,4,4,4]
    ];

    public static void Run(Action<string,Action> Test)
    {
        Test("ACC-L01/L03 every enabled count has a packed column-major map",()=>
        {
            for(var n=1;n<=16;n++)foreach(var layout in new[]{"vertical","horizontal"})
            {
                var rows=n==2&&layout=="horizontal"?new[]{2}:VerticalRows[n-1];
                Assert(PaneLayout.Columns(n,layout)==rows.Length,$"{n}/{layout}: columns");
                Assert(PaneLayout.RowsPerColumn(n,layout).SequenceEqual(rows),$"{n}/{layout}: allocation");
                var cells=PaneLayout.Map(n,layout);Assert(cells.Count==n,$"{n}/{layout}: placeholder pane");
                Assert(cells.Select(c=>c.CreationOrder).Order().SequenceEqual(Enumerable.Range(0,n)),"creation IDs are not a permutation");
                var enabled=0;
                for(var column=0;column<rows.Length;column++)for(var row=0;row<rows[column];row++)
                {
                    var cell=cells[enabled];
                    Assert(cell.EnabledIndex==enabled&&cell.Column==column&&cell.Row==row,$"{n}/{layout}: enabled order {enabled}");
                    Assert(cell.ColumnsInRow==rows.Length&&cell.RowsInColumn==rows[column],"cell dimensions differ from allocation");enabled++;
                }
            }
        });
        Test("ACC-L03 creation IDs differ from the stable enabled order",()=>
        {
            // Enabled order is column-major; WT creates all column heads before their lower rows.
            foreach(var (n,ids) in new (int,int[])[]{(3,[0,1,2]),(4,[0,2,1,3]),(8,[0,4,1,5,2,6,3,7]),(9,[0,4,5,1,6,2,7,3,8]),(16,[0,4,5,6,1,7,8,9,2,10,11,12,3,13,14,15])})
                Assert(PaneLayout.Map(n,"vertical").Select(c=>c.CreationOrder).SequenceEqual(ids),$"N={n}: creation map");
        });
        Test("ACC-L03 all layout entry points reject zero and seventeen panes",()=>
        {
            foreach(var n in new[]{0,17})
            {
                Reject<ArgumentOutOfRangeException>(()=>PaneLayout.Columns(n,"vertical"));
                Reject<ArgumentOutOfRangeException>(()=>PaneLayout.RowsPerColumn(n,"vertical"));
                Reject<ArgumentOutOfRangeException>(()=>PaneLayout.Map(n,"vertical"));
                Reject<ArgumentOutOfRangeException>(()=>PaneLayout.SplitSteps(n,"vertical"));
                Reject<ArgumentOutOfRangeException>(()=>Commands.ComposeTerminalArguments(Items(n),"vertical"));
            }
        });
        Test("ACC-L01/L03 actual split commands leave equal columns and equal rows",()=>
        {
            for(var n=1;n<=16;n++)foreach(var layout in new[]{"vertical","horizontal"})
            {
                CheckGeometry(PaneLayout.SplitSteps(n,layout),n,layout);
                var items=Items(n);var arguments=Commands.ComposeTerminalArguments(items,layout);
                CheckGeometry(arguments,n,layout);
                Assert(arguments[0]=="-w"&&arguments[1]=="agent-"+items[0].BatchId.ToString("N"),"batch window identity changed");
                Assert(arguments.Contains("--maximized")== (n>1),"maximization does not match pane count");
                Assert(arguments.TakeLast(4).SequenceEqual(new[]{";","focus-pane","--target","0"}),"final focus is not first enabled pane");
                var panes=CommandsOf(arguments).Where(c=>c.Contains("new-tab")||c[0]=="split-pane").ToArray();
                Assert(panes.Length==n,"arguments contain empty or missing panes");
                Assert(panes.Count(c=>c.Contains("new-tab"))==1,"more than one tab created");
                var map=PaneLayout.Map(n,layout);
                for(var creation=0;creation<n;creation++)
                {
                    var item=items[map.Single(c=>c.CreationOrder==creation).EnabledIndex];var pane=panes[creation];
                    var worker=Array.IndexOf(pane,"pane");Assert(worker>0&&worker+1==pane.Length-1,"worker command has extra or missing arguments");
                    Assert(pane[worker-1]==Store.WorkerPath.Replace(";","\\;"),"worker path is not escaped literal data");
                    Assert(pane[worker+1]==item.Id.ToString("D"),$"N={n}, creation={creation}: wrong process in pane");
                    Assert(Value(pane,"--startingDirectory")==item.Cwd.Replace(";","\\;"),"working directory changed");
                    Assert(Value(pane,"--title").StartsWith($"{item.Position:00}  {item.Name} · ",StringComparison.Ordinal),"title uses creation order rather than stable slot position");
                    Assert(pane.Contains("--suppressApplicationTitle"),"application can overwrite pane identity");
                }
            }
        });
        Test("ACC-L01 eight panes use right then down splits with invariant size fractions",()=>
        {
            var before=CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
                var commands=CommandsOf(PaneLayout.SplitSteps(8,"vertical"));var splits=commands.Where(c=>c[0]=="split-pane").ToArray();
                Assert(splits.Select(c=>c[1]).SequenceEqual(new[]{"-V","-V","-V","-H","-H","-H","-H"}),"rows split before columns");
                Assert(splits.Select(c=>Value(c,"--size")).SequenceEqual(new[]{"0.75","0.6666666666666666","0.5","0.5","0.5","0.5","0.5"}),"fractions are rounded or locale dependent");
                Assert(!commands.SelectMany(c=>c).Contains("--percent"),"unsupported percentage option");
                Assert(splits.All(c=>!c.Contains("--target")),"target must be selected with focus-pane, not split-pane");
            }
            finally{CultureInfo.CurrentCulture=before;}
        });
        Test("ACC-T01 every pane receives its Agent color and old names retain their color",()=>
        {
            var colors=new Dictionary<string,string>{{"pi","#61AFEF"},{"codex","#E5C07B"},{"claude","#C678DD"},{"gemini","#56B6C2"},{"opencode","#98C379"},{"hermes","#E06C75"},{"omp","#D19A66"},{"kimi","#BE5046"},{"qoder","#528BFF"},{"custom","#ABB2BF"}};
            var items=Items(Agents.All.Length);
            for(var i=0;i<items.Length;i++)items[i]=items[i] with{AgentId=Agents.All[i].Id,Name="Codex"};
            CheckColors(items,colors);
            for(var i=0;i<items.Length;i++)items[i]=items[i] with{AgentId=null,Name=Agents.All[i].Name.ToUpperInvariant()};
            CheckColors(items,colors);
            var custom=Items(3);custom[0]=custom[0] with{AgentId="custom",Name="Codex"};custom[1]=custom[1] with{AgentId="unknown",Name="Pi"};custom[2]=custom[2] with{AgentId=null,Name="User-defined agent"};
            Assert(PaneCommands(Commands.ComposeTerminalArguments(custom,"vertical")).All(c=>Value(c,"--tabColor")==colors["custom"]),"explicit/unknown/custom identity was guessed from display name");
        });
        Test("ACC-T01 system colors suppress both Terminal color overrides on every pane",()=>
        {
            var items=Items(16);var ordinary=Commands.ComposeTerminalArguments(items,"vertical");var system=Commands.ComposeTerminalArguments(items,"vertical",true);
            Assert(!system.Contains("--colorScheme")&&!system.Contains("--tabColor"),"system colors overridden");
            var withoutColors=new List<string>();
            for(var i=0;i<ordinary.Count;i++){if(ordinary[i] is "--colorScheme" or "--tabColor"){i++;continue;}withoutColors.Add(ordinary[i]);}
            Assert(withoutColors.SequenceEqual(system),"high contrast changed layout or pane process identity");
            Assert(PaneCommands(system).Count==16,"system color mode dropped panes");
        });
        Test("ACC-L06 pane text stays literal while display titles remain bounded",()=>
        {
            var items=Items(1);items[0]=items[0] with{Name="Pi;literal\u001b\t"+new string('n',2000),Cwd=@"C:\project;literal\folder;tail\"};
            var pane=PaneCommands(Commands.ComposeTerminalArguments(items,"vertical"))[0];var title=Value(pane,"--title");
            Assert(title.Contains("Pi\\;literal",StringComparison.Ordinal)&&!title.Any(char.IsControl),"title control or semicolon escaped incorrectly");
            Assert(title.Contains('…')&&title.Length<120,"display-title truncation changed");
            Assert(Value(pane,"--startingDirectory")==items[0].Cwd.Replace(";","\\;"),"real working directory was truncated or reformatted");
            Assert(pane.Count(s=>s=="pane")==1,"literal title became a Terminal command");
        });
        Test("ACC-L06 quoted length matches ProtectedProcess for empty quotes slashes and Unicode",()=>
        {
            var values=new[]{"","plain","two words","中文",";","\t","line\nvalue","\"","a\"b","\\","tail\\",@"C:\Program Files\Terminal\",new string('\\',4)+"\"",new string('\\',5)+"\"tail\\"};
            foreach(var value in values)Assert(Commands.QuotedLength(value)==ProtectedProcess.Quote(value).Length,$"quoted length differs for {value.Length} characters");
            Reject<ArgumentException>(()=>Commands.QuotedLength("nul\0tail"));
        });
        Test("ACC-L06 24000 boundary counts executable every quoted argument and spaces without starting",()=>
        {
            const string terminal=@"C:\Program Files\Windows Terminal\WindowsTerminal.exe";
            var items=Items(16);var originalLength=CommandLength(terminal,Commands.ComposeTerminalArguments(items,"vertical"));
            foreach(var target in new[]{ResourcePolicy.CommandLineLimit-1,ResourcePolicy.CommandLineLimit,ResourcePolicy.CommandLineLimit+1})
            {
                var adjusted=items.ToArray();adjusted[^1]=adjusted[^1] with{Cwd=DirectoryOfLength(checked(items[^1].Cwd.Length+(int)(target-originalLength)))};
                var arguments=Commands.ComposeTerminalArguments(adjusted,"vertical");Assert(CommandLength(terminal,arguments)==target,"boundary fixture does not reach exact complete length");
                if(target<=ResourcePolicy.CommandLineLimit)
                {
                    var start=CreateStart(terminal,arguments);Assert(start.FileName==terminal&&!start.UseShellExecute&&start.ArgumentList.SequenceEqual(arguments),"accepted command was truncated or sent through a shell");
                    if(target==ResourcePolicy.CommandLineLimit)RejectLongCommand(()=>CreateStart(terminal+"x",arguments));
                }
                else RejectLongCommand(()=>CreateStart(terminal,arguments));
            }
        });
        Test("ACC-L06 overlong real working directories fail before Terminal submission",()=>
        {
            var items=Items(16);for(var i=0;i<items.Length;i++)items[i]=items[i] with{Cwd=DirectoryOfLength(1800)};
            var terminal=Path.Combine(Path.GetTempPath(),"AgentLauncher-no-process-"+Guid.NewGuid().ToString("N"),"wt.exe");
            Assert(CommandLength(terminal,Commands.ComposeTerminalArguments(items,"vertical",true))>ResourcePolicy.CommandLineLimit,"fixture only exceeds the limit when color overrides are present");
            // The nonexistent executable also makes this safe against regression: no child can execute.
            RejectLongCommand(()=>Commands.SubmitTerminal(terminal,items,"vertical"));
        });
    }

    private static void CheckColors(LaunchItem[] items,IReadOnlyDictionary<string,string> colors)
    {
        var panes=PaneCommands(Commands.ComposeTerminalArguments(items,"vertical"));var map=PaneLayout.Map(items.Length,"vertical");
        for(var creation=0;creation<panes.Count;creation++)
        {
            var index=map.Single(c=>c.CreationOrder==creation).EnabledIndex;var expected=colors[Agents.All[index].Id];
            Assert(Value(panes[creation],"--tabColor")==expected,"Agent color follows display text or creation position");
            Assert(Value(panes[creation],"--colorScheme")=="One Half Dark","non-built-in scheme selected");
        }
    }

    private static void CheckGeometry(IReadOnlyList<string> arguments,int n,string layout)
    {
        var rectangles=new List<(double X,double Y,double Width,double Height)>();var focused=-1;var previous="";var directions=new List<string>();
        foreach(var command in CommandsOf(arguments))
        {
            if(command.Contains("new-tab")){Assert(rectangles.Count==0,"unexpected additional tab");rectangles.Add((0,0,1,1));focused=0;previous="new-tab";continue;}
            if(command[0]=="focus-pane")
            {
                focused=int.Parse(Value(command,"--target"),CultureInfo.InvariantCulture);Assert(focused>=0&&focused<rectangles.Count,"focus references a pane not yet created");previous="focus-pane";continue;
            }
            Assert(command[0]=="split-pane"&&previous=="focus-pane","split must explicitly focus its parent first");
            var size=double.Parse(Value(command,"--size"),CultureInfo.InvariantCulture);Assert(size>=0.01&&size<=0.99,"invalid new-pane ratio");
            var rectangle=rectangles[focused];var vertical=command.Contains("-V");Assert(vertical!=command.Contains("-H"),"split direction missing or ambiguous");directions.Add(vertical?"-V":"-H");
            // WT creates the new pane on the right (-V) or below (-H); --size is its share of its parent.
            if(vertical){rectangles[focused]=(rectangle.X,rectangle.Y,rectangle.Width*(1-size),rectangle.Height);rectangles.Add((rectangle.X+rectangle.Width*(1-size),rectangle.Y,rectangle.Width*size,rectangle.Height));}
            else{rectangles[focused]=(rectangle.X,rectangle.Y,rectangle.Width,rectangle.Height*(1-size));rectangles.Add((rectangle.X,rectangle.Y+rectangle.Height*(1-size),rectangle.Width,rectangle.Height*size));}
            focused=rectangles.Count-1;previous="split-pane";
        }
        var rows=n==2&&layout=="horizontal"?new[]{2}:VerticalRows[n-1];
        Assert(rectangles.Count==n&&focused==0,"pane count or final focus changed");
        Assert(directions.SequenceEqual(Enumerable.Repeat("-V",rows.Length-1).Concat(Enumerable.Repeat("-H",n-rows.Length))),"columns must be completed before rows");
        foreach(var cell in PaneLayout.Map(n,layout))
        {
            var rectangle=rectangles[cell.CreationOrder];
            Assert(Near(rectangle.X,(double)cell.Column/rows.Length)&&Near(rectangle.Width,1d/rows.Length),$"N={n}: unequal column width or order");
            Assert(Near(rectangle.Y,(double)cell.Row/rows[cell.Column])&&Near(rectangle.Height,1d/rows[cell.Column]),$"N={n}: unequal row height or order");
        }
    }

    private static LaunchItem[] Items(int n)
    {
        var batch=Guid.NewGuid();var cwd=DirectoryOfLength(32);
        return Enumerable.Range(0,n).Select(i=>new LaunchItem(Guid.NewGuid(),batch,i+1,"Agent "+i,cwd,new(cwd,"workspace-"+i),false,new("fixture.exe",[],"exe"),ResourcePolicy.DefaultJobMemoryLimit,AgentId:Agents.All[i%Agents.All.Length].Id)).ToArray();
    }

    private static string DirectoryOfLength(int length)
    {
        const string suffix=@"\leaf";if(length<9)throw new ArgumentOutOfRangeException(nameof(length));
        var text=new StringBuilder(@"C:\");var segment=0;
        while(text.Length<length-suffix.Length)
        {
            if(segment==200&&text.Length<length-suffix.Length-1){text.Append('\\');segment=0;}
            else{text.Append('d');segment++;}
        }
        return text.Append(suffix).ToString();
    }

    private static IReadOnlyList<string[]> CommandsOf(IReadOnlyList<string> arguments)
    {
        var commands=new List<string[]>();var command=new List<string>();
        foreach(var argument in arguments){if(argument==";"){Assert(command.Count>0,"empty Terminal command");commands.Add(command.ToArray());command.Clear();}else command.Add(argument);}
        Assert(command.Count>0,"trailing empty Terminal command");commands.Add(command.ToArray());return commands;
    }
    private static IReadOnlyList<string[]> PaneCommands(IReadOnlyList<string> arguments)=>CommandsOf(arguments).Where(c=>c.Contains("new-tab")||c[0]=="split-pane").ToArray();
    private static string Value(string[] arguments,string option){var index=Array.IndexOf(arguments,option);Assert(index>=0&&index+1<arguments.Length,"missing "+option);return arguments[index+1];}
    private static long CommandLength(string terminal,IReadOnlyList<string> arguments)=>ProtectedProcess.Quote(terminal).Length+arguments.Sum(a=>1L+ProtectedProcess.Quote(a).Length);
    private static ProcessStartInfo CreateStart(string terminal,IReadOnlyList<string> arguments)
    {
        var factory=typeof(Commands).GetMethod("CreateTerminalStartInfo",BindingFlags.Static|BindingFlags.NonPublic)??throw new MissingMethodException(typeof(Commands).FullName,"CreateTerminalStartInfo");
        try{return (ProcessStartInfo)factory.Invoke(null,[terminal,arguments])!;}
        catch(TargetInvocationException error)when(error.InnerException is IOException io){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(io).Throw();throw;}
    }
    private static void RejectLongCommand(Action action){var error=Reject<IOException>(action);Assert(error.Message.Contains("24000",StringComparison.Ordinal)||error.Message.Contains("命令行",StringComparison.Ordinal),"length failure is not actionable");}
    private static T Reject<T>(Action action)where T:Exception{try{action();}catch(T error){return error;}throw new Exception("Expected "+typeof(T).Name);}
    private static bool Near(double actual,double expected)=>Math.Abs(actual-expected)<1e-12;
    private static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
}
