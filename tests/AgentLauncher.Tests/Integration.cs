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
        await Test("P04 begin rechecks complete resource policy without consuming grant",async()=>{
            var type=System.Reflection.Assembly.LoadFrom(Store.WorkerPath.Replace(".exe",".dll",StringComparison.OrdinalIgnoreCase)).GetType("AgentLauncher.Worker.Monitor",true)!;
            const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            System.Reflection.FieldInfo Field(string name)=>type.GetField(name,flags)??throw new MissingFieldException(type.FullName,name);
            var handle=type.GetMethod("Handle",flags)??throw new MissingMethodException(type.FullName,"Handle");
            var healthySample=new MemorySnapshot(16*ResourcePolicy.GiB,12*ResourcePolicy.GiB,32*ResourcePolicy.GiB,24*ResourcePolicy.GiB,5,DateTimeOffset.UtcNow);
            foreach(var sample in new[]{healthySample with{CommitAvailable=ResourcePolicy.GiB},healthySample with{Available=ResourcePolicy.DefaultPhysicalFloor(healthySample.Total)+ResourcePolicy.DefaultPlanningBytes-1},healthySample with{CpuPercent=double.NaN},healthySample with{At=DateTimeOffset.UtcNow.AddSeconds(-10)}})
            {
                using var monitor=(IDisposable)Activator.CreateInstance(type,true)!;var item=Item("recheck",["--hold","1"]);var entry=new ControlEntry{Plan=item,Digest=Store.Digest(item)};
                Field("sample").SetValue(monitor,sample);Field("ledger").SetValue(monitor,new Ledger{Entries=[entry]});
                Field("ledgerPath").SetValue(monitor,Path.Combine(Store.Root,"recheck-"+Guid.NewGuid().ToString("N")+".json"));
                var denied=false;try{handle.Invoke(monitor,[new Request("begin",Guid.NewGuid(),Attempt:item.Id)]);}catch(System.Reflection.TargetInvocationException ex)when(ex.InnerException is IOException){denied=true;}
                Assert(denied&&entry.State=="Pending","begin consumed a grant with invalid resource evidence");
            }
            await Task.CompletedTask;
        });
        await Test("R-handle-lost reconcile releases finished-tree proof and refuses unprovable trees",async()=>{
            var type=System.Reflection.Assembly.LoadFrom(Store.WorkerPath.Replace(".exe",".dll",StringComparison.OrdinalIgnoreCase)).GetType("AgentLauncher.Worker.Monitor",true)!;
            const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            System.Reflection.FieldInfo Field(string name)=>type.GetField(name,flags)??throw new MissingFieldException(type.FullName,name);
            var handle=type.GetMethod("Handle",flags)??throw new MissingMethodException(type.FullName,"Handle");
            var finItem=Item("lost-finished",["--hold","1"]);var deadTreeItem=Item("lost-dead-tree",["--hold","1"]);var liveItem=Item("lost-live",["--hold","1"]);
            var isolatedRoot=Store.RootJob+"-reconcile-"+Guid.NewGuid().ToString("N");
            using var rootJob=new Job(isolatedRoot,root:true);
            using(var treeJob=new Job(Store.JobName(deadTreeItem.Id),ResourcePolicy.AgentBudget))
            {
                using var parent=new ProtectedProcess(new(Environment.ProcessPath!,["--hold","30000"],"exe"),cwd,rootJob,treeJob);
                using var survivor=new ProtectedProcess(new(Environment.ProcessPath!,["--hold","60000"],"exe"),cwd,rootJob,treeJob);
                parent.Resume();survivor.Resume();
                for(var i=0;i<50&&treeJob.Members().Length<2;i++)await Task.Delay(100);
                Assert(treeJob.Members().Length==2,"tree job did not contain both children: "+string.Join(",",treeJob.Members()));
                parent.Process.Kill();await parent.Process.WaitForExitAsync();
                var parentPid=parent.Process.Id;var parentTime=parent.CreationTime;
                var survivorPid=survivor.Process.Id;var survivorTime=survivor.CreationTime;
                treeJob.Dispose();
                var openFailed=false;try{using var probe=new Job(Store.JobName(deadTreeItem.Id),openOnly:true);}catch(System.ComponentModel.Win32Exception e)when(e.NativeErrorCode==2){openFailed=true;}
                Assert(openFailed,"scenario broken: job object still openable after last handle closed");
                Assert(!survivor.Process.HasExited,"scenario broken: surviving tree member exited with the job object");
                try
                {
                    var sharedStatus=await Ipc.SendAsync(new("status",Guid.NewGuid()));
                    Assert(sharedStatus.Ok,"reconcile fixture contaminated the shared monitor: "+sharedStatus.Message);
                    var finEntry=new ControlEntry{Plan=finItem,Digest=Store.Digest(finItem),State="Creating",Detail="正在创建进程"};
                    var deadTreeEntry=new ControlEntry{Plan=deadTreeItem,Digest=Store.Digest(deadTreeItem),State="Creating",Detail="正在创建进程"};
                    var liveEntry=new ControlEntry{Plan=liveItem,Digest=Store.Digest(liveItem),State="Creating",Detail="正在创建进程"};
                    Store.Save(Store.ReceiptPath(finItem.Id),new Receipt("Finished",parentPid,parentTime,1,"全部受管进程已退出，退出码 1",DateTimeOffset.UtcNow));
                    Store.Save(Store.ReceiptPath(deadTreeItem.Id),new Receipt("Exited",parentPid,parentTime,-1073740791,"进程已退出，退出码 -1073740791",DateTimeOffset.UtcNow));
                    Store.Save(Store.ReceiptPath(liveItem.Id),new Receipt("Started",survivorPid,survivorTime,null,"已启动进程（不代表任务完成）",DateTimeOffset.UtcNow));
                    using var monitor=(IDisposable)Activator.CreateInstance(type,true)!;
                    Field("sample").SetValue(monitor,new MemorySnapshot(16*ResourcePolicy.GiB,12*ResourcePolicy.GiB,32*ResourcePolicy.GiB,24*ResourcePolicy.GiB,5,DateTimeOffset.UtcNow));
                    Field("ledger").SetValue(monitor,new Ledger{Entries=[finEntry,deadTreeEntry,liveEntry]});
                    Field("ledgerPath").SetValue(monitor,Path.Combine(Store.Root,"reconcile-"+Guid.NewGuid().ToString("N")+".json"));
                    Field("root").SetValue(monitor,new Job(isolatedRoot,root:true));
                    var response=(Response)handle.Invoke(monitor,[new Request("reconcile",Guid.NewGuid())])!;
                    Assert(finEntry.State=="Settled"&&finEntry.Detail.Contains("核对释放"),"finished-tree receipt was not released: "+finEntry.State+" "+finEntry.Detail);
                    Assert(deadTreeEntry.State=="Creating"&&deadTreeEntry.Detail.Contains("拒绝"),"dead parent with a live surviving tree member was released: "+deadTreeEntry.State+" "+deadTreeEntry.Detail);
                    Assert(liveEntry.State=="Creating"&&liveEntry.Detail.Contains("仍在运行"),"entry with a live recorded process did not refuse: "+liveEntry.State+" "+liveEntry.Detail);
                    Assert(response.Ok==false&&response.Entries?.SingleOrDefault(e=>e.Plan.Id==finItem.Id)!=null,"reconcile outcome did not keep refusals while reporting the release");
                    Assert(!survivor.Process.HasExited,"reconcile killed the surviving tree member");
                }
                finally{try{survivor.Process.Kill();await survivor.Process.WaitForExitAsync();}catch(System.InvalidOperationException){}}
            }
        });
        await Test("R13 synchronous ResumeThread rejection reclaims never-run child",async()=>{
            var output=Path.Combine(Store.Root,"resume-rejection-must-not-exist.json");var item=Item("resume-rejected",["--echo",output]);var reserved=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[item]));Assert(reserved.Ok,reserved.Message);
            try
            {
                var info=new ProcessStartInfo(Store.WorkerPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add("run");info.ArgumentList.Add(item.Id.ToString());info.Environment["AGENT_LAUNCHER_FAIL_AT"]="ResumeRejected";
                using var runner=Process.Start(info)!;runner.StandardInput.Close();await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                var receipt=Store.Read<Receipt>(Store.ReceiptPath(item.Id));Assert(runner.ExitCode!=0&&!File.Exists(output)&&receipt?.Phase=="FailedBeforeStart","known resume rejection did not settle as never-run");
                var status=await Ipc.SendAsync(new("status",Guid.NewGuid()));Assert(status.Entries!.Single(e=>e.Plan.Id==item.Id).State=="FailedBeforeStart","known non-running child retained occupancy");
            }
            finally
            {
                if(Store.Read<Receipt>(Store.ReceiptPath(item.Id))?.Phase=="ResumeIntent")
                {
                    using var ownedJob=new Job(Store.JobName(item.Id),openOnly:true);
                    foreach(var pid in ownedJob.Members()){using var child=Process.GetProcessById(pid);Native.Check(Native.IsProcessInJob(child.Handle,ownedJob.Handle,out var member));Assert(member,"fixture process identity changed");Native.Check(Native.TerminateProcess(child.Handle,1));child.WaitForExit(5000);}
                    Store.Save(Store.ReceiptPath(item.Id),new Receipt("FailedBeforeStart",0,0,null,"Isolated fixture cleaned never-resumed child",DateTimeOffset.UtcNow));await Ipc.SendAsync(new("status",Guid.NewGuid()));
                }
            }
        });
        await Test("R05 single-item retry is immutable and cannot replay an older record",async()=>{
            var original=Item("retry-source",["--hold","1"]) with{Shared=true};var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[original]));Assert(reserve.Ok,reserve.Message);
            var retry=original with{Id=Guid.NewGuid(),BatchId=Guid.NewGuid(),RetryOf=original.Id};
            var unsafeRetry=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[retry]));Assert(!unsafeRetry.Ok,"pending source allowed a duplicate retry");
            await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:original.Id));
            var changed=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[retry with{Command=retry.Command with{Arguments=["--hold","2"]}}]));Assert(!changed.Ok,"modified command accepted as original retry");
            var allowed=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[retry]));Assert(allowed.Ok,allowed.Message);await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:retry.Id));
            var replay=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[retry with{Id=Guid.NewGuid(),BatchId=Guid.NewGuid()}]));Assert(!replay.Ok,"old source record retried twice");
        });
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
            var item=Item("cleanup",["--hold","100"]);var cleanupNotifications=0;
            Environment.SetEnvironmentVariable("AGENT_LAUNCHER_IPC_DROP_AFTER_SEND","1");
            try{await Ipc.ReserveWithCleanupAsync([item],beforeCleanup:()=>cleanupNotifications++);Assert(false,"timeout not surfaced");}
            catch(IOException e){Assert(e.Message.Contains("撤销"),e.Message);}
            finally{Environment.SetEnvironmentVariable("AGENT_LAUNCHER_IPC_DROP_AFTER_SEND",null);}
            var status=await Ipc.SendAsync(new("status",Guid.NewGuid()));
            Assert(cleanupNotifications==1,"UI cleanup notification was not delivered exactly once");
            Assert(status.Entries!.Single(e=>e.Plan.Id==item.Id).State=="Revoked","committed grant not revoked after client timeout");
        });
        // F1 regression: ControlledCommand must route its reserve through the cleanup wrapper; a lost reserve response must not leave a Pending git grant holding the global slot.
        await Test("ControlledCommand lost reserve response auto-revokes",async()=>{
            var git=Commands.FindExe("git.exe")!;
            Environment.SetEnvironmentVariable("AGENT_LAUNCHER_IPC_DROP_AFTER_SEND","1");
            try{await ControlledCommand.RunAsync(new(git,["--version"],"exe"),cwd);Assert(false,"lost reserve response not surfaced");}
            catch(IOException e){Assert(e.Message.Contains("撤销"),e.Message);}
            finally{Environment.SetEnvironmentVariable("AGENT_LAUNCHER_IPC_DROP_AFTER_SEND",null);}
            var status=await Ipc.SendAsync(new("status",Guid.NewGuid()));
            Assert(!status.Entries!.Any(e=>!Store.IsTerminal(e)&&e.Plan.Kind=="git"),"unclaimed git grant leaked after lost reserve response");
        });
        // F2 regression: a Runner that exits while its grant is still Pending (begin rejected/never sent) must surface "never ran" distinctly and release the global git slot. Uses the new AGENT_LAUNCHER_FAIL_AT=BeforeBegin hook (test-infra addition, flagged for review).
        await Test("ControlledCommand begin rejection releases git slot",async()=>{
            var git=Commands.FindExe("git.exe")!;
            Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAIL_AT","BeforeBegin");
            IOException? failure=null;
            try{await ControlledCommand.RunAsync(new(git,["--version"],"exe"),cwd);}catch(IOException e){failure=e;}
            finally{Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAIL_AT",null);}
            Assert(failure!=null&&failure.Message.Contains("未运行"),"begin rejection not surfaced distinctly: "+(failure?.Message??"no error"));
            var status=await Ipc.SendAsync(new("status",Guid.NewGuid()));
            Assert(!status.Entries!.Any(e=>!Store.IsTerminal(e)&&e.Plan.Kind=="git"),"begin-rejected git grant leaked");
        });
        // F3 regression: replay must re-resolve commands each time so a rewritten npm-style shim (new entry script) is reflected; the old repair only triggered when the executable was missing.
        await Test("Preparation replay refreshes changed shim argv",async()=>{
            const string npmShim="""
@ECHO off
GOTO start
:find_dp0
SET dp0=%~dp0
EXIT /b
:start
SETLOCAL
CALL :find_dp0

IF EXIST "%dp0%\node.exe" (
  SET "_prog=%dp0%\node.exe"
) ELSE (
  SET "_prog=node"
  SET PATHEXT=%PATHEXT:;.JS;=;%
)

endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%"  "%dp0%\{ENTRY}" %*
""";
            var shim=Path.Combine(Store.Root,"shim-refresh");Directory.CreateDirectory(shim);
            File.WriteAllText(Path.Combine(shim,"node.exe"),"stub");
            var beforeRecords=Directory.CreateDirectory(Store.FilePath("preparations")).GetFiles("*.json").Select(f=>f.FullName).ToHashSet();
            await File.WriteAllTextAsync(Path.Combine(shim,"tool.cmd"),npmShim.Replace("{ENTRY}","old.js").Replace("\r\n","\n"));await File.WriteAllTextAsync(Path.Combine(shim,"old.js"),"// 1");
            var d=new Draft{Project=cwd,DirectoryMode="shared"};d.Slots.ForEach(s=>s.Enabled=false);d.Slots[0].Enabled=true;d.Slots[0].Profiles["pi"]=new(){Executable=Path.Combine(shim,"tool.cmd")};
            try
            {
                var first=await new WorkspacePreparer().PrepareAsync(d,_=>{},_=>Task.FromResult(false),CancellationToken.None);
                Assert(first[0].Command.Arguments[0].EndsWith("old.js"),"initial shim resolution wrong: "+first[0].Command.Arguments[0]);
                await File.WriteAllTextAsync(Path.Combine(shim,"tool.cmd"),npmShim.Replace("{ENTRY}","new.js").Replace("\r\n","\n"));await File.WriteAllTextAsync(Path.Combine(shim,"new.js"),"// 2");File.Delete(Path.Combine(shim,"old.js"));
                var second=await new WorkspacePreparer().PrepareAsync(d,_=>{},_=>Task.FromResult(false),CancellationToken.None);
                Assert(second[0].Command.Arguments[0].EndsWith("new.js"),"changed shim argv not reflected on replay: "+second[0].Command.Arguments[0]);
            }
            finally{if(Directory.Exists(Store.FilePath("preparations")))foreach(var extra in Directory.GetFiles(Store.FilePath("preparations"),"*.json").Where(path=>!beforeRecords.Contains(path)))File.Delete(extra);}
        });
        // F4 regression: status history is byte-bounded (768 KiB terminal window) so the 1-MiB IPC frame stays writable with large arguments.
        await Test("Status frame stays readable with large argument history",async()=>{
            var big=new string('a',28000);
            for(var i=0;i<40;i++){var item=Item("bulk-"+i,["--hold","1",big]);var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[item]));Assert(reserve.Ok,reserve.Message);var revoke=await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:item.Id));Assert(revoke.Ok,revoke.Message);}
            var report=await Ipc.SendAsync(new("status",Guid.NewGuid()));
            Assert(report.Ok,"status unreadable with large terminal history: "+report.Message);
            Assert(report.Entries!.Count(e=>Store.IsTerminal(e))<40,"terminal history not byte-bounded");
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
        await Test("F09 custom worktree location isolates and reuses prepared directories",async()=>{
            var destination=Path.Combine(Store.Root,"custom-worktrees");Directory.CreateDirectory(destination);var draft=validationDraft.Copy();draft.Project=Path.Combine(Store.Root,"tiny-repo");draft.DirectoryMode="isolated";draft.Slots[0].Enabled=true;
            foreach(var slot in draft.Slots.Where(s=>s.Enabled))slot.Profiles[slot.AgentId]=new(){Executable=Environment.ProcessPath!,Arguments=["--hold","1"]};
            var plans=await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            Assert(plans.All(p=>p.Cwd.StartsWith(PhysicalDirectory.Resolve(destination).Path+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)),"custom location ignored");
            var replay=await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            Assert(replay.Select(p=>p.Cwd).SequenceEqual(plans.Select(p=>p.Cwd))&&!replay.Select(p=>p.Id).Intersect(plans.Select(p=>p.Id)).Any(),"custom location replay duplicated worktrees or authorization");
        });
        await Test("F09 default destination alias reuses existing preparation",async()=>{
            var draft=validationDraft.Copy();draft.Project=Path.Combine(Store.Root,"tiny-repo");draft.DirectoryMode="isolated";
            var implicitPlans=await new WorkspacePreparer().PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            var explicitPlans=await new WorkspacePreparer(Store.FilePath("worktrees")).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            Assert(implicitPlans.Select(p=>p.Cwd).SequenceEqual(explicitPlans.Select(p=>p.Cwd)),"explicit default destination duplicated worktrees");
        });
        await Test("F09 worktree destination inside source is rejected before creation",async()=>{
            var repo=Path.Combine(Store.Root,"tiny-repo");var nested=Path.Combine(repo,"nested-destination");Directory.CreateDirectory(nested);
            var draft=validationDraft.Copy();draft.Project=repo;draft.DirectoryMode="isolated";
            foreach(var destination in new[]{repo,nested}){var before=Directory.GetFileSystemEntries(destination).Order().ToArray();var rejected=false;try{await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);}catch(IOException){rejected=true;}Assert(rejected&&Directory.GetFileSystemEntries(destination).Order().SequenceEqual(before),"nested worktree destination accepted or mutated");}
        });
        await Test("F09 unused missing destination does not block direct or shared launch",async()=>{
            var missing=Path.Combine(Store.Root,"unused-missing-destination");var draft=validationDraft.Copy();
            foreach(var mode in new[]{"shared","isolated"}){draft.DirectoryMode=mode;if(mode=="isolated")draft.Slots.ForEach(s=>s.Enabled=s==draft.Slots[0]);var plans=await new WorkspacePreparer(missing).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);Assert(plans.All(p=>p.Cwd==PhysicalDirectory.Resolve(cwd).Path),"unused destination changed direct launch");}
        });
        await Test("F05 custom title semicolons remain literal Terminal data",async()=>{
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"test-root.txt"),Store.Root);
            var output=Path.Combine(Store.Root,"literal-title.json");var item=Item("review;literal-title-tail\\;中文",["--echo",output,"title-is-data"]) with{Shared=true};
            var reserved=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[item]));Assert(reserved.Ok,reserved.Message);
            Commands.SubmitTerminal(Commands.Terminal(""),[item],"vertical");
            for(var i=0;i<100&&Store.Read<Receipt>(Store.ReceiptPath(item.Id))?.Phase!="Finished";i++)await Task.Delay(100);
            Assert(Store.Read<Receipt>(Store.ReceiptPath(item.Id)) is{Phase:"Finished",ExitCode:0}&&File.Exists(output),"literal title changed Terminal command parsing");await Ipc.SendAsync(new("status",Guid.NewGuid()));
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
        await Ipc.EnsureMonitorAsync();await WorkspaceReviewChecks.RunAsync(Test,cwd);
        await RuntimeReviewChecks.RunAsync(Test,cwd);
        Store.Save(Path.Combine(Store.Root,"integration-results.json"),evidence);
        var failures=evidence.Count(e=>e.GetType().GetProperty("passed")?.GetValue(e) is false);Console.WriteLine($"Integration failures: {failures}; evidence: {Store.Root}");return failures==0?0:1;
    }
}
