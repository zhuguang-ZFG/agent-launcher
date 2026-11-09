using System.Diagnostics;
using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static class Integration
{
    public static async Task<int> RunAsync()
    {
        if(!(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT")??"").Contains("AgentLauncher-tests-"))throw new InvalidOperationException("Integration requires an isolated test root.");
        Directory.CreateDirectory(Store.Root);var evidence=new List<object>();var cwd=Path.Combine(Store.Root,"项目 & spaces");Directory.CreateDirectory(cwd);
        Process Start(string mode,Guid id)
        {
            var start=new ProcessStartInfo(Store.WorkerPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add(mode);start.ArgumentList.Add(id.ToString());
            var p=Process.Start(start)!;p.StandardInput.Close();return p;
        }
        void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
        async Task Test(string name,Func<Task> action){try{await action();Console.WriteLine("PASS "+name);evidence.Add(new{name,passed=true});}catch(Exception e){Console.WriteLine("FAIL "+name+": "+e.Message);evidence.Add(new{name,passed=false,error=e.ToString()});}}
        LaunchItem Item(string name,string[] args,string kind="agent")=>new(Guid.NewGuid(),Guid.NewGuid(),1,name,cwd,PhysicalDirectory.Workspace(cwd),false,new(Environment.ProcessPath!,args,"exe"),kind=="git"?ResourcePolicy.GitBudget:ResourcePolicy.AgentBudget,kind);
        var initial=await Ipc.EnsureMonitorAsync();Assert(initial.Ok,initial.Message);
        await Test("R14 revoked grant rejects late Runner",async()=>{
            var output=Path.Combine(Store.Root,"must-not-exist.json");var item=Item("late",["--echo",output]);var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[item]));Assert(reserve.Ok,reserve.Message);
            var revoke=await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:item.Id));Assert(revoke.Ok,revoke.Message);
            using var runner=Start("run",item.Id);await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));Assert(runner.ExitCode!=0&&!File.Exists(output),"late runner created process");
        });
        await Test("F14 cross-batch independent workspace conflict",async()=>{
            var one=Item("one",["--hold","100"]);var two=Item("two",["--hold","100"]);var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[one]));Assert(reserve.Ok,reserve.Message);
            try{var duplicate=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[two]));Assert(!duplicate.Ok&&duplicate.Message.Contains("占用"),"directory conflict not reported");}finally{await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:one.Id));}
        });
        await Test("Runner receipt and exact arguments",async()=>{
            var output=Path.Combine(Store.Root,"echo.json");var item=Item("echo",["--echo",output,"中文 spaces","a;split-pane","&literal"]);var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[item]));Assert(reserve.Ok,reserve.Message);
            using var runner=Start("run",item.Id);var stderr=runner.StandardError.ReadToEndAsync();await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));Assert(runner.ExitCode==0,await stderr);
            var receipt=Store.Read<Receipt>(Store.ReceiptPath(item.Id));Assert(receipt?.Phase=="Finished"&&File.Exists(output),"missing completion receipt");
            var status=await Ipc.SendAsync(new("status",Guid.NewGuid()));Assert(status.Entries!.Single(e=>e.Plan.Id==item.Id).State=="Settled","budget not released");
        });
        await Test("Git prepare process uses protected capture",async()=>{
            var git=Commands.FindExe("git.exe")!;var result=await ControlledCommand.RunAsync(new(git,["--version"],"exe"),cwd);Assert(result.Code==0&&result.Output.StartsWith("git version"),result.Error+result.Output);
        });
        await Test("Real Pi version via recognized adapter",async()=>{
            var command=Commands.Resolve("pi",new(){Arguments=["--version"]});var result=await ControlledCommand.RunAsync(command,cwd);Assert(result.Code==0&&result.Output.Trim().Length>0,result.Error);evidence.Add(new{name="pi-version",output=result.Output.Trim()});
        });
        await Test("R13 monitor restart preserves running process and reconciles",async()=>{
            var item=Item("survive",["--hold","4500"]);var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[item]));Assert(reserve.Ok,reserve.Message);
            using var runner=Start("run",item.Id);
            Receipt? receipt=null;for(var i=0;i<20;i++){await Task.Delay(100);receipt=Store.Read<Receipt>(Store.ReceiptPath(item.Id));if(receipt?.Phase=="Started")break;}
            Assert(receipt?.Phase=="Started","child not started");
            // Only kill the isolated monitor identified by this test root's recorded PID.
            var pid=Store.Read<int>(Store.FilePath("monitor-pid.json"));using(var monitor=Process.GetProcessById(pid)){monitor.Kill();await monitor.WaitForExitAsync();}
            using(var child=Process.GetProcessById(receipt!.Pid))Assert(!child.HasExited,"child died with monitor");
            var restored=await Ipc.EnsureMonitorAsync();Assert(restored.Ok,restored.Message);
            await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));var status=await Ipc.SendAsync(new("status",Guid.NewGuid()));Assert(status.Entries!.Single(e=>e.Plan.Id==item.Id).State=="Settled","did not reconcile completion");
        });
        foreach(var phase in new[]{"SpawnIntent","CreatedSuspended","Started"})
        await Test("R13 crash at "+phase,async()=>{
            var output=Path.Combine(Store.Root,"crash-"+phase+".json");var item=Item("crash",["--echo",output]);var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[item]));Assert(reserve.Ok,reserve.Message);
            Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAIL_AT",phase);Process runner;try{runner=Start("run",item.Id);}finally{Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAIL_AT",null);}
            using(runner){await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));Assert(runner.ExitCode==97,"fault hook did not fire");}
            for(var i=0;i<25;i++){await Task.Delay(100);var report=await Ipc.SendAsync(new("status",Guid.NewGuid()));if(Store.IsTerminal(report.Entries!.Single(e=>e.Plan.Id==item.Id)))break;}
            var status=await Ipc.SendAsync(new("status",Guid.NewGuid()));Assert(Store.IsTerminal(status.Entries!.Single(e=>e.Plan.Id==item.Id)),"unsettled crashed attempt");Assert(File.Exists(output)==(phase=="Started"),"unexpected execution after crash boundary");
        });
        await Test("F09 isolated worktrees from fixed commit",async()=>{
            var repo=Path.Combine(Store.Root,"tiny-repo");Directory.CreateDirectory(repo);var git=Commands.FindExe("git.exe")!;
            async Task Git(params string[] args){var result=await ControlledCommand.RunAsync(new(git,args,"exe"),repo);Assert(result.Code==0,result.Error);}
            await Git("init");await File.WriteAllTextAsync(Path.Combine(repo,"hello.txt"),"baseline");await Git("add","hello.txt");await Git("-c","user.name=Launcher Test","-c","user.email=test@example.invalid","commit","-m","temporary fixture");
            var d=new Draft{Project=repo};d.Slots[1].Enabled=true;foreach(var slot in d.Slots.Take(2))slot.Profiles[slot.AgentId]=new(){Executable=Environment.ProcessPath!,Arguments=["--hold","100"]};
            var plans=await new WorkspacePreparer().PrepareAsync(d,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            var repeated=await new WorkspacePreparer().PrepareAsync(d,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            Assert(repeated.Select(p=>p.Cwd).SequenceEqual(plans.Select(p=>p.Cwd)),"recovery created duplicate worktrees");Assert(!repeated.Select(p=>p.Id).Intersect(plans.Select(p=>p.Id)).Any(),"reused launch authorization IDs");
            Assert(plans.Count==2&&plans[0].Workspace.Key!=plans[1].Workspace.Key,"worktrees not independent");await File.WriteAllTextAsync(Path.Combine(plans[0].Cwd,"hello.txt"),"only first");Assert(await File.ReadAllTextAsync(Path.Combine(plans[1].Cwd,"hello.txt"))=="baseline","worktrees share modifications");
        });
        await Test("Terminal two panes exact child cwd and arguments",async()=>{
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"test-root.txt"),Store.Root);
            var batch=Guid.NewGuid();var items=Enumerable.Range(1,2).Select(n=>Item("Terminal fixture "+n,["--echo",Path.Combine(Store.Root,$"terminal-{n}.json"),"中文 & ; spaces"]) with{BatchId=batch,Position=n,Shared=true}).ToArray();
            var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),items));Assert(reserve.Ok,reserve.Message);
            Commands.SubmitTerminal(Commands.Terminal(""),items,"vertical");
            for(var i=0;i<100;i++){if(items.All(p=>Store.Read<Receipt>(Store.ReceiptPath(p.Id))?.Phase=="Finished"))break;await Task.Delay(100);}
            Assert(items.All(p=>Store.Read<Receipt>(Store.ReceiptPath(p.Id))?.Phase=="Finished"),"Terminal panes did not complete");
            Assert(File.Exists(Path.Combine(Store.Root,"terminal-1.json"))&&File.Exists(Path.Combine(Store.Root,"terminal-2.json")),"terminal command did not execute");await Ipc.SendAsync(new("status",Guid.NewGuid()));
        });
        var monitorId=Store.Read<int>(Store.FilePath("monitor-pid.json"));using(var monitor=Process.GetProcessById(monitorId)){monitor.Refresh();evidence.Add(new{name="idle-monitor-memory",privateBytes=monitor.PrivateMemorySize64,workingSet=monitor.WorkingSet64});}
        Store.Save(Path.Combine(Store.Root,"integration-results.json"),evidence);
        var failures=evidence.Count(e=>e.GetType().GetProperty("passed")?.GetValue(e) is false);Console.WriteLine($"Integration failures: {failures}; evidence: {Store.Root}");return failures==0?0:1;
    }
}
