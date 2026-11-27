using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Tests;

internal static class PracticalFixChecks
{
    public static void Run(Action<string,Action> test,string temp)
    {
        void Check(bool value,string why){if(!value)throw new Exception(why);}
        TaskCard Card(TaskBook book)=>TaskBookOps.Create(book,"任务 \"中文\"",temp,"codex",new(){Executable=Environment.ProcessPath!},"修改第一行\n第二行；保留引号 \" 和反斜线 \\", ["验收完整"],"shared",null,0);
        test("UX12 exit outcome retains abnormal code and child-drain distinction",()=>
        {
            var entry=new ControlEntry{Plan=new(Guid.NewGuid(),Guid.NewGuid(),1,"test",temp,PhysicalDirectory.Workspace(temp),false,new("fixture.exe",[],"exe"),ResourcePolicy.AgentBudget),State="Settled",ExitCode=-1073740791,Detail="资源分配被拒绝"};
            Check(TaskOutcome.From(entry) is {State:"Failed",Detail:var detail}&&detail.Contains("-1073740791")&&detail.Contains("资源分配"),"abnormal exit lost");
            entry.State="Exited";Check(TaskOutcome.From(entry) is null,"descendants prematurely settled");
            entry.State="Settled";entry.ExitCode=0;Check(TaskOutcome.From(entry)?.State=="Review","zero exit auto-completed");
            entry.ExitCode=null;Check(TaskOutcome.From(entry)?.Detail.Contains("未确认")==true,"unknown exit claimed success");
            entry.Detail="进程已退出。退出码 8（0x00000008）。";Check(TaskOutcome.From(entry)?.State=="Failed","legacy failure displayed as review");
            entry.ExitCode=7;Check(JsonSerializer.Deserialize<ControlEntry>(JsonSerializer.Serialize(entry,Store.Json),Store.Json)?.ExitCode==7,"exit code lost in serialization");
        });
        test("UX12 automatic task edits invalidate frozen queue but acceptance checks do not",()=>
        {
            var book=new TaskBook();var task=Card(book);task.DeliveryMode="automatic";
            TaskBookOps.Enqueue(book,task.Id,new("fixture.exe",[TaskDelivery.Prompt(task)],"exe"),new('A',64));
            TaskBookOps.SetAcceptance(book,task.Id,task.Acceptance[0].Id,true);Check(book.Queue.Count==1,"check invalidates intent");
            TaskBookOps.Edit(book,task.Id,t=>t.Description+="新要求");Check(book.Queue.Count==0&&book.Tasks[0].Revision==2,"changed instructions remain queued");
            Check(!TaskDelivery.Prompt(task).Contains(temp,StringComparison.Ordinal)&&TaskDelivery.Prompt(task).Contains("当前启动工作目录"),"prompt points Agent at original checkout");
        });
        test("UX12 prompt delivery reaches child as exact argv and remains opt-in",()=>
        {
            var output=Path.Combine(temp,"delivery.json");var before=Environment.GetEnvironmentVariable("AGENT_LAUNCHER_DELIVERY_FIXTURE");
            try
            {
                Environment.SetEnvironmentVariable("AGENT_LAUNCHER_DELIVERY_FIXTURE",output);
                var task=Card(new());Check(TaskDelivery.PrepareAsync(task).GetAwaiter().GetResult().Arguments.Length==0,"manual mode sent prompt");
                task.DeliveryMode="automatic";var profile=TaskDelivery.PrepareAsync(task).GetAwaiter().GetResult();
                Check(profile.Arguments.SequenceEqual(new[]{"exec","--",TaskDelivery.Prompt(task)}),"wrong execution mode");
                var result=AgentProbe.RunAsync(Commands.Resolve(task.AgentId,profile),temp,TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Check(result.ExitCode==0&&JsonSerializer.Deserialize<string[]>(File.ReadAllText(output))!.SequenceEqual(profile.Arguments),"prompt changed across process boundary");
                task.AgentId="claude";profile=TaskDelivery.PrepareAsync(task).GetAwaiter().GetResult();Check(profile.Arguments[0]=="--print","print mode missing");
                task.Profile.Arguments=["--continue"];try{TaskDelivery.PrepareAsync(task).GetAwaiter().GetResult();throw new Exception("resume accepted initial prompt");}catch(IOException){}
            }
            finally{Environment.SetEnvironmentVariable("AGENT_LAUNCHER_DELIVERY_FIXTURE",before);}
        });
        test("UX12 upgraded session requires explicit fresh program binding",()=>
        {
            var book=new TaskBook();var task=Card(book);var old=new string('A',64);var current=new string('B',64);
            var run=new TaskRun{TaskId=task.Id,AttemptId=Guid.NewGuid(),State="Review",WorkspaceKey="ws",ProgramFingerprint=old,SessionRef="session"};book.Attempts.Add(run);
            Check(!QueuePolicy.CanResume(run,"ws",current,book.Attempts).Allowed,"upgrade implicitly trusted");
            TaskBookOps.RegisterSession(book,run.AttemptId,"session","ws",current,reverified:true);
            Check(run.ProgramFingerprint==old&&QueuePolicy.CanResume(run,"ws",current,book.Attempts).Allowed,"reverification rewrote history or blocked current program");
            Check(!QueuePolicy.CanResume(run,"elsewhere",current,book.Attempts).Allowed&&!QueuePolicy.CanResume(run,"ws",new('C',64),book.Attempts).Allowed,"binding escaped workspace/program");
        });
        test("UX12 local session discovery filters workspace and keeps only metadata",()=>
        {
            var root=Path.Combine(temp,"sessions");Directory.CreateDirectory(root);var other=Path.Combine(temp,"elsewhere");Directory.CreateDirectory(other);
            var id=Guid.NewGuid();File.WriteAllText(Path.Combine(root,"good.jsonl"),JsonSerializer.Serialize(new{type="session_meta",payload=new{id=id.ToString(),cwd=temp}})+"\n{\"secret\":\"conversation\"}");
            File.WriteAllText(Path.Combine(root,"other.jsonl"),JsonSerializer.Serialize(new{type="session_meta",payload=new{id=Guid.NewGuid().ToString(),cwd=other}}));
            File.WriteAllText(Path.Combine(root,"broken.jsonl"),"{partial");
            var result=SessionDiscovery.Find("codex",temp,root);Check(result.Sessions.Count==1&&result.Sessions[0].Reference==id.ToString(),"wrong workspace session exposed");
            Check(!JsonSerializer.Serialize(result).Contains("conversation"),"conversation leaked");
            Check(SessionDiscovery.Find("qoder",temp,root).Sessions.Count==0,"retired Qoder session discovery remained available");
        });
        test("UX12 malformed session metadata does not hide valid candidates",()=>
        {
            var root=Path.Combine(temp,"malformed-sessions");Directory.CreateDirectory(root);var id=Guid.NewGuid();
            foreach(var (name,content) in new[]{("array","[]"),("null","null"),("type","{\"type\":3}"),("payload","{\"type\":\"session_meta\",\"payload\":[]}")})
                File.WriteAllText(Path.Combine(root,name+".jsonl"),content);
            File.WriteAllText(Path.Combine(root,"valid.jsonl"),JsonSerializer.Serialize(new{type="session_meta",payload=new{id=id.ToString(),cwd=temp}}));
            Check(SessionDiscovery.Find("codex",temp,root).Sessions.Single().Reference==id.ToString(),"malformed Codex metadata aborted discovery");
            Check(SessionDiscovery.Find("qoder",temp,root).Sessions.Count==0,"retired Qoder metadata was discovered");
            File.WriteAllText(Path.Combine(root,"valid.jsonl"),JsonSerializer.Serialize(new{cwd=temp,sessionId=id.ToString()}));
            Check(SessionDiscovery.Find("claude",temp,root).Sessions.Single().Reference==id.ToString(),"malformed Claude metadata aborted discovery");
        });
        test("UX12 cleanup inventory handles rename and quoted unusual names",()=>
        {
            var files=CleanupInventory.Parse("R  新文件.txt\0旧文件.txt\0?? 新增\n文件.txt\0!! .env\0!! node_modules/\0");
            Check(files.Count==4&&files[0].OriginalPath=="旧文件.txt"&&files[1].Path=="新增\n文件.txt"&&files[2].Category=="忽略内容","misclassified blocking files");
            Check(files[1].Display.Contains("\\n")&&!files[1].Display.Contains('\n'),"filename displayed as fabricated row");
            Check(!WorkspaceCleanup.Evaluate(false,"!! .env").Allowed,"ignored secret cleanup allowed");
        });
        test("UX12 batch pane titles disambiguate identical launch configurations",()=>
        {
            var item=new LaunchItem(Guid.NewGuid(),Guid.NewGuid(),1,"实现",temp,PhysicalDirectory.Workspace(temp),false,new("fixture.exe",[],"exe"),ResourcePolicy.AgentBudget);
            Check(TerminalAppearance.PaneTitle(item)!=TerminalAppearance.PaneTitle(item with{BatchId=Guid.NewGuid()}),"batches share a title");
        });
        test("UX12 cancelled version probe stops promptly",()=>
        {
            using var cancel=new CancellationTokenSource(150);var clock=System.Diagnostics.Stopwatch.StartNew();
            try{AgentProbe.RunAsync(new(Environment.ProcessPath!,["--hold","8000"],"exe"),temp,TimeSpan.FromSeconds(8),ct:cancel.Token).GetAwaiter().GetResult();throw new Exception("cancel ignored");}
            catch(OperationCanceledException){}
            Check(clock.Elapsed<TimeSpan.FromSeconds(3),"probe kept editing busy after close");
        });
        test("UX12 Claude native npm shim resolves exact argv and refuses added shell commands",()=>
        {
            var root=Path.Combine(temp,"claude-native");var native=Path.Combine(root,"node_modules","@anthropic-ai","claude-code","bin","claude.exe");Directory.CreateDirectory(Path.GetDirectoryName(native)!);File.WriteAllText(native,"fixture");
            var shim=Path.Combine(root,"claude.cmd");var template="@ECHO off\nGOTO start\n:find_dp0\nSET dp0=%~dp0\nEXIT /b\n:start\nSETLOCAL\nCALL :find_dp0\n\"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe\"   %*";File.WriteAllText(shim,template);
            var args=new[]{"--print","--","中文 \" arg"};var command=Commands.Resolve("claude",new(){Executable=shim,Arguments=args});
            Check(command.Executable==native&&command.Adapter=="exe"&&command.Arguments.SequenceEqual(args),"native shim argv differs");
            File.AppendAllText(shim,"\necho unsafe");try{Commands.Resolve("claude",new(){Executable=shim});throw new Exception("extra shell semantics ignored");}catch(IOException){}
        });
    }
}
