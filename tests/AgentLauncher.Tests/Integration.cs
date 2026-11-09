using System.Diagnostics;
using System.Text.Json;
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
            var found=Commands.FindExe("pi");evidence.Add(new{name="pi-found",path=found});
            CommandSpec command;try{command=Commands.Resolve("pi",new(){Arguments=["--version"]});}catch(Exception e){throw new Exception($"pi resolved path: {found??"<null>"}; {e.Message}");}
            var result=await ControlledCommand.RunAsync(command,cwd);Assert(result.Code==0&&result.Output.Trim().Length>0,result.Error);evidence.Add(new{name="pi-version",output=result.Output.Trim()});
        });
        await Test("Client timeout after committed reserve auto-revokes",async()=>{
            var item=Item("cleanup",["--hold","100"]);
            Environment.SetEnvironmentVariable("AGENT_LAUNCHER_IPC_DROP_AFTER_SEND","1");
            try{await Ipc.ReserveWithCleanupAsync([item]);Assert(false,"timeout not surfaced");}
            catch(IOException e){Assert(e.Message.Contains("撤销"),e.Message);}
            finally{Environment.SetEnvironmentVariable("AGENT_LAUNCHER_IPC_DROP_AFTER_SEND",null);}
            var status=await Ipc.SendAsync(new("status",Guid.NewGuid()));
            Assert(status.Entries!.Single(e=>e.Plan.Id==item.Id).State=="Revoked","committed grant not revoked after client timeout");
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
        await Test("Status reliable across idle ticks",async()=>{
            for(var i=0;i<12;i++){await Task.Delay(500);var report=await Ipc.SendAsync(new("status",Guid.NewGuid()));Assert(report.Ok,"status failed at iteration "+i+": "+report.Message);}
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
            var prepPath=Directory.GetFiles(Path.Combine(Store.Root,"preparations"),"*.json").Single();
            Assert(Store.Read<PreparationRecord>(prepPath)!.Original[0].Cwd==PhysicalDirectory.Resolve(repo).Path,"preparation record Original overwritten with worktree path");
            var drift=await ControlledCommand.RunAsync(new(git,["-c","user.name=Launcher Test","-c","user.email=test@example.invalid","commit","--allow-empty","-m","drift"],"exe"),plans[0].Cwd);
            Assert(drift.Code==0,drift.Error);
            var rejected=false;try{await new WorkspacePreparer().PrepareAsync(d,_=>{},_=>Task.FromResult(false),CancellationToken.None);}catch(IOException e){rejected=e.Message.Contains("基线已变化");}
            Assert(rejected,"changed prepared worktree HEAD was reused");
            var reset=await ControlledCommand.RunAsync(new(git,["reset","--hard","HEAD~1"],"exe"),plans[0].Cwd);Assert(reset.Code==0,reset.Error);
            var record=Store.Read<PreparationRecord>(prepPath)!;
            record.Original[0]=record.Original[0] with{Command=new(Path.Combine(Store.Root,"missing-node.exe"),record.Original[0].Command.Arguments,"exe")};Store.Save(prepPath,record);
            var healed=await new WorkspacePreparer().PrepareAsync(d,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            Assert(healed.Select(p=>p.Cwd).SequenceEqual(plans.Select(p=>p.Cwd)),"stale command recompute lost worktree mapping");
            var healedRecord=Store.Read<PreparationRecord>(prepPath)!;Assert(healedRecord.SchemaVersion==1&&File.Exists(healedRecord.Original[0].Command.Executable),"stale command not re-resolved in record");
            var good=Store.Read<PreparationRecord>(prepPath)!;
            record=Store.Read<PreparationRecord>(prepPath)!;record.Original.Add(null!);Store.Save(prepPath,record);
            try{await new WorkspacePreparer().PrepareAsync(d,_=>{},_=>Task.FromResult(false),CancellationToken.None);Assert(false,"corrupt preparation record replayed");}catch(IOException e){Assert(e.Message.Contains("损坏"),e.Message);}
            Store.Save(prepPath,new{good.Digest,good.Original,good.Completed,good.Baselines});
            var legacy=await new WorkspacePreparer().PrepareAsync(d,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            Assert(legacy.Select(p=>p.Cwd).SequenceEqual(plans.Select(p=>p.Cwd)),"legacy record created duplicate worktrees");
            var adopted=Store.Read<PreparationRecord>(prepPath)!;Assert(adopted.SchemaVersion==1&&adopted.Original.Select(p=>p.Id).SequenceEqual(good.Original.Select(p=>p.Id))&&adopted.Baselines.Count==good.Baselines.Count,"legacy record identity lost");
            Store.Save(prepPath,new{SchemaVersion=2,good.Digest,good.Original,good.Completed,good.Baselines});
            var unsupported=File.ReadAllBytes(prepPath);
            try{await new WorkspacePreparer().PrepareAsync(d,_=>{},_=>Task.FromResult(false),CancellationToken.None);Assert(false,"unsupported preparation record accepted");}catch(IOException e){Assert(e.Message.Contains("版本不支持"),e.Message);}
            Assert(File.ReadAllBytes(prepPath).SequenceEqual(unsupported),"unsupported preparation record was rewritten");
        });
        var validationDraft=new Draft{Project=cwd,DirectoryMode="shared"};
        for(var i=0;i<validationDraft.Slots.Count;i++){var slot=validationDraft.Slots[i];slot.Enabled=i is 1 or 3;slot.Profiles[slot.AgentId]=new(){Executable=Environment.ProcessPath!,Arguments=["--hold","100"]};}
        var previousRecords=Directory.GetFiles(Store.FilePath("preparations"),"*.json").ToHashSet();
        var validationPlans=await new WorkspacePreparer().PrepareAsync(validationDraft,_=>{},_=>Task.FromResult(false),CancellationToken.None);
        var validationPath=Directory.GetFiles(Store.FilePath("preparations"),"*.json").Single(p=>!previousRecords.Contains(p));
        var validRecordBytes=File.ReadAllBytes(validationPath);
        var corruptions=new (string Name,Action<PreparationRecord> Apply)[]
        {
            ("null executable",r=>r.Original[0]=r.Original[0] with{Command=r.Original[0].Command with{Executable=null!}}),
            ("null arguments",r=>r.Original[0]=r.Original[0] with{Command=r.Original[0].Command with{Arguments=null!}}),
            ("null argument",r=>r.Original[0]=r.Original[0] with{Command=r.Original[0].Command with{Arguments=[null!]}}),
            ("NUL argument",r=>r.Original[0]=r.Original[0] with{Command=r.Original[0].Command with{Arguments=["bad\0arg"]}}),
            ("null adapter",r=>r.Original[0]=r.Original[0] with{Command=r.Original[0].Command with{Adapter=null!}}),
            ("null workspace path",r=>r.Original[0]=r.Original[0] with{Workspace=r.Original[0].Workspace with{Path=null!}}),
            ("null workspace key",r=>r.Original[0]=r.Original[0] with{Workspace=r.Original[0].Workspace with{Key=null!}}),
            ("null name",r=>r.Original[0]=r.Original[0] with{Name=null!}),
            ("relative source path",r=>r.Original[0]=r.Original[0] with{Cwd="relative"}),
            ("zero position before repair",r=>r.Original[0]=r.Original[0] with{Position=0,Command=r.Original[0].Command with{Executable=Store.FilePath("missing-command.exe")}}),
            ("out of range position before repair",r=>r.Original[0]=r.Original[0] with{Position=5,Command=r.Original[0].Command with{Executable=Store.FilePath("missing-command.exe")}}),
            ("disabled draft position",r=>r.Original[0]=r.Original[0] with{Position=1}),
            ("duplicate original ID",r=>r.Original[1]=r.Original[1] with{Id=r.Original[0].Id}),
            ("duplicate position",r=>r.Original[1]=r.Original[1] with{Position=r.Original[0].Position}),
            ("missing active slot",r=>r.Original.RemoveAt(1)),
            ("mixed batch IDs",r=>r.Original[1]=r.Original[1] with{BatchId=Guid.NewGuid()}),
            ("orphan completed key before repair",r=>r.Completed.Add(Guid.NewGuid(),r.Original[0] with{Command=r.Original[0].Command with{Executable=Store.FilePath("missing-command.exe")}})),
            ("completed ID mismatch",r=>r.Completed.Add(r.Original[0].Id,r.Original[0] with{Id=Guid.NewGuid()})),
            ("completed position mismatch",r=>r.Completed.Add(r.Original[0].Id,r.Original[0] with{Position=r.Original[1].Position})),
            ("completed batch mismatch",r=>r.Completed.Add(r.Original[0].Id,r.Original[0] with{BatchId=Guid.NewGuid()})),
            ("null completed executable",r=>r.Completed.Add(r.Original[0].Id,r.Original[0] with{Command=r.Original[0].Command with{Executable=null!}})),
            ("null baseline",r=>r.Baselines.Add(r.Original[0].Workspace.Key,null!)),
            ("unknown baseline key",r=>r.Baselines.Add("unknown-workspace",new string('a',40)))
        };
        foreach(var version in new[]{0,1})
        foreach(var corruption in corruptions)
        await Test($"R11 preparation v{version} {corruption.Name} refuses without writes",async()=>{
            var candidate=JsonSerializer.Deserialize<PreparationRecord>(validRecordBytes,Store.Json)!;corruption.Apply(candidate);
            if(version==0)Store.Save(validationPath,new{candidate.Digest,candidate.Original,candidate.Completed,candidate.Baselines});else Store.Save(validationPath,candidate);
            var originalBytes=File.ReadAllBytes(validationPath);var backupBytes=File.ReadAllBytes(validationPath+".bak");Exception? error=null;
            try{await new WorkspacePreparer().PrepareAsync(validationDraft,_=>{},_=>Task.FromResult(false),CancellationToken.None);}catch(Exception e){error=e;}
            Assert(File.ReadAllBytes(validationPath).SequenceEqual(originalBytes),"invalid preparation record was rewritten");
            Assert(File.ReadAllBytes(validationPath+".bak").SequenceEqual(backupBytes),"backup was overwritten on corruption refusal");
            Assert(error is IOException,"expected corruption IOException, got "+(error?.GetType().Name??"successful replay"));
        });
        Store.Save(validationPath,JsonSerializer.Deserialize<PreparationRecord>(validRecordBytes,Store.Json)!);
        await Test("F09 noncontiguous active positions preserve preparation identity",async()=>{
            var replay=await new WorkspacePreparer().PrepareAsync(validationDraft,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            Assert(replay.Select(p=>p.Position).SequenceEqual(new[]{2,4})&&replay.Select(p=>p.Cwd).SequenceEqual(validationPlans.Select(p=>p.Cwd)),"valid active-slot mapping changed");
            Assert(!replay.Select(p=>p.Id).Intersect(validationPlans.Select(p=>p.Id)).Any(),"reused launch authorization IDs");
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
