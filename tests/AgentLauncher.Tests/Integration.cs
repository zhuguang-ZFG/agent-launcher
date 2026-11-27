using System.Diagnostics;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static class Integration
{
    public static async Task<int> RunAsync(bool terminalOnly=false,bool directBatch=false,bool startupOnly=false)
    {
        if(!(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT")??"").Contains("AgentLauncher-tests-"))throw new InvalidOperationException("Integration requires an isolated test root.");
        Directory.CreateDirectory(Store.Root);var evidence=new List<object>();var cwd=Path.Combine(Store.Root,"项目 & spaces");Directory.CreateDirectory(cwd);
        Process Start(string mode,Guid id)
        {
            var start=new ProcessStartInfo(Store.WorkerPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add(mode);start.ArgumentList.Add(id.ToString());
            var p=Process.Start(start)!;p.StandardInput.Close();return p;
        }
        void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
        T Sync<T>(Task<T> task)=>task.GetAwaiter().GetResult();
        string WaitMarker(string path,string expected,int timeoutMs)
        {
            var watch=Stopwatch.StartNew();
            while(watch.ElapsedMilliseconds<timeoutMs)
            {
                try{using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(stream);var text=reader.ReadToEnd();if(text==expected)return text;}
                catch(IOException){}
                System.Threading.Thread.Sleep(20);
            }
            throw new Exception("marker timeout: "+path);
        }
        async Task Test(string name,Func<Task> action){try{await action();Console.WriteLine("PASS "+name);evidence.Add(new{name,passed=true});}catch(Exception e){Console.WriteLine("FAIL "+name+": "+e.Message);evidence.Add(new{name,passed=false,error=e.ToString()});}}
        LaunchItem Item(string name,string[] args,string kind="agent")=>new(Guid.NewGuid(),Guid.NewGuid(),1,name,cwd,PhysicalDirectory.Workspace(cwd),false,new(Environment.ProcessPath!,args,"exe"),kind=="git"?ResourcePolicy.GitBudget:ResourcePolicy.AgentBudget,kind,AgentId:kind=="git"?null:"codex");
        async Task TerminalBatch()
        {
            var terminal=directBatch?"":Commands.Terminal("");
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"test-root.txt"),Store.Root);
            var settings=new ResourceSettings{Mode="custom",JobMemoryLimit=ResourcePolicy.AgentBudget,PlanningBytes=ResourcePolicy.MinPlanningBytes,Pool=ResourcePolicy.PoolShared,PoolMemoryLimit=4*ResourcePolicy.GiB};
            var original=Store.Read<Configuration>(Store.FilePath("configuration.json"));
            var config=new Configuration();config.Draft.Project=cwd;config.Draft.Resource=settings;Store.Save(Store.FilePath("configuration.json"),config);
            try
            {
                var batch=Guid.NewGuid();var pool=Guid.NewGuid();var release=Path.Combine(Store.Root,"terminal-6.release");
                var items=Enumerable.Range(1,ResourcePolicy.MaxConcurrentAgents).Select(n=>Item("Terminal 6-pane fixture "+n,["--pane-ready",Path.Combine(Store.Root,$"terminal-6-{n}.json"),release]) with{BatchId=batch,Position=n,Shared=true,PlanningBytes=settings.PlanningBytes,ResourceMode=settings.Mode,PoolId=pool,AgentId="codex"}).ToArray();
                using var monitor=Process.GetProcessById(Store.Read<int>(Store.FilePath("monitor-pid.json")));_ = monitor.Handle;
                var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),items));Assert(reserve.Ok,reserve.Message);
                int[] missing=[];var runners=new List<Process>();
                try
                {
                    if(directBatch)foreach(var item in items)runners.Add(Start("run",item.Id));else Commands.SubmitTerminal(terminal,items,"vertical");
                    var deadline=Stopwatch.StartNew();
                    while(deadline.Elapsed<TimeSpan.FromSeconds(25)&&items.Any(item=>!File.Exists(Path.Combine(Store.Root,$"terminal-6-{item.Position}.json"))))await Task.Delay(100);
                }
                finally
                {
                    missing=items.Where(item=>!File.Exists(Path.Combine(Store.Root,$"terminal-6-{item.Position}.json"))).Select(item=>item.Position).ToArray();
                    var snapshot=new{missing,monitorExited=monitor.HasExited,monitorExitCode=monitor.HasExited?(int?)monitor.ExitCode:null,items=items.Select(item=>new{item.Id,item.Position,claim=File.Exists(Path.Combine(Store.Root,"attempts",item.Id.ToString("N"),"claim")),receipt=Store.Read<Receipt>(Store.ReceiptPath(item.Id)),entered=Store.Read<JsonElement?>(Store.FilePath(Path.Combine("attempts",item.Id.ToString("N"),"runner-entered.json"))),error=Store.Read<JsonElement?>(Store.FilePath(Path.Combine("attempts",item.Id.ToString("N"),"runner-error.json")))}).ToArray()};
                    Store.Save(Store.FilePath("terminal-16-diagnostics.json"),snapshot);
                    Console.WriteLine("Terminal six pane diagnostics: "+JsonSerializer.Serialize(snapshot,Store.Json));
                    File.WriteAllText(release,"release");
                    var finished=Stopwatch.StartNew();
                    while(finished.Elapsed<TimeSpan.FromSeconds(15)&&items.Any(item=>File.Exists(Path.Combine(Store.Root,"attempts",item.Id.ToString("N"),"claim"))&&Store.Read<Receipt>(Store.ReceiptPath(item.Id))?.Phase is not("Finished" or "FailedBeforeStart")))await Task.Delay(100);
                    foreach(var item in items.Where(item=>!File.Exists(Path.Combine(Store.Root,"attempts",item.Id.ToString("N"),"claim"))))
                        try{await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:item.Id));}catch(Exception e)when(e is IOException or OperationCanceledException){Console.WriteLine("Synthetic unclaimed pane cleanup refused: "+e.Message);}
                    await Ipc.SendAsync(new("status",Guid.NewGuid()));
                    foreach(var runner in runners)runner.Dispose();
                }
                Assert(missing.Length==0,"Terminal did not start every 6-pane fixture; missing positions: "+string.Join(",",missing));
                Assert(items.All(item=>File.Exists(Path.Combine(Store.Root,"attempts",item.Id.ToString("N"),"claim"))),"one or more pane claims are missing");
                Assert(items.All(item=>Store.Read<Receipt>(Store.ReceiptPath(item.Id)) is{Phase:"Finished",ExitCode:0}),"not every pane completed after the shared release");
            }
            finally { Store.Save(Store.FilePath("configuration.json"),original??new Configuration()); }
        }
        async Task TerminalPair()
        {
            var terminal=Commands.Terminal("");
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"test-root.txt"),Store.Root);
            var batch=Guid.NewGuid();var release=Path.Combine(Store.Root,"terminal-two.release");var items=Enumerable.Range(1,2).Select(n=>Item("Terminal fixture "+n,["--pane-ready",Path.Combine(Store.Root,$"terminal-{n}.json"),release,"中文 & ; spaces"]) with{BatchId=batch,Position=n,Shared=true}).ToArray();
            var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),items));Assert(reserve.Ok,reserve.Message);
            try
            {
                Commands.SubmitTerminal(terminal,items,"vertical");
                for(var i=0;i<100&&!items.All(item=>File.Exists(Path.Combine(Store.Root,$"terminal-{item.Position}.json")));i++)await Task.Delay(100);
                foreach(var item in items)
                {
                    var actual=JsonDocument.Parse(File.ReadAllText(Path.Combine(Store.Root,$"terminal-{item.Position}.json"))).RootElement;
                    Assert(actual.GetProperty("Cwd").GetString()==item.Cwd&&actual.GetProperty("Args")[0].GetString()=="中文 & ; spaces","Terminal changed the child cwd or literal arguments");
                }
                foreach(var item in items.Reverse())
                {
                    var reference=Store.Read<TerminalPaneReference>(Store.FilePath($"attempts/{item.Id:N}/terminal.json"));
                    Assert(reference is not null&&reference.AttemptId==item.Id&&reference.BatchId==batch,"missing stable pane reference");
                    using var focus=Process.Start(Commands.TerminalFocusStartInfo(terminal,reference!))!;
                    await focus.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    var title=TerminalAppearance.PaneTitle(item);var found=false;
                    for(var i=0;i<30&&!found;i++){found=TerminalRecallChecks.HasWindow(title);if(!found)await Task.Delay(100);}
                    Assert(found&&focus.ExitCode==0,"Terminal did not focus the requested pane: "+item.Position);
                }
            }
            finally
            {
                File.WriteAllText(release,"release");
                for(var i=0;i<100&&items.Any(p=>Store.Read<Receipt>(Store.ReceiptPath(p.Id))?.Phase!="Finished");i++)await Task.Delay(100);
                await Ipc.SendAsync(new("status",Guid.NewGuid()));
            }
            for(var i=0;i<100;i++){if(items.All(p=>Store.Read<Receipt>(Store.ReceiptPath(p.Id))?.Phase=="Finished"))break;await Task.Delay(100);}
            Assert(items.All(p=>Store.Read<Receipt>(Store.ReceiptPath(p.Id))?.Phase=="Finished"),"Terminal panes did not complete");
            Assert(File.Exists(Path.Combine(Store.Root,"terminal-1.json"))&&File.Exists(Path.Combine(Store.Root,"terminal-2.json")),"terminal command did not execute");await Ipc.SendAsync(new("status",Guid.NewGuid()));
        }
        ProtectedProcess LaunchPooled(Job root,Guid poolId,Guid? itemId,string[] args)
        {
            using var pool=new Job(Store.PoolName(poolId),openOnly:true);
            ProtectedProcess process;
            if(itemId is {} id)
            {
                using var item=new Job(Store.JobName(id),openOnly:true);
                process=new ProtectedProcess(new(Environment.ProcessPath!,args,"exe"),cwd,[root,pool,item]);
            }
            else process=new ProtectedProcess(new(Environment.ProcessPath!,args,"exe"),cwd,[root,pool]);
            process.Resume();return process;
        }
        var initial=await Ipc.EnsureMonitorAsync();Assert(initial.Ok,initial.Message);
        if(startupOnly)
        {
            await Test("F05 Terminal sends one creation at a time and cancellation stops later panes",async()=>{
                var capture=Path.Combine(Store.Root,"terminal-submission-capture");Directory.CreateDirectory(capture);
                var batch=Guid.NewGuid();var items=Enumerable.Range(1,3).Select(n=>Item("submit "+n,["--hold","1"]) with{BatchId=batch,Position=n,Shared=true}).ToArray();
                var previous=Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TERMINAL_FIXTURE");Environment.SetEnvironmentVariable("AGENT_LAUNCHER_TERMINAL_FIXTURE",capture);
                using var cancellation=new CancellationTokenSource();Task? submission=null;
                try
                {
                    submission=Task.Run(()=>Commands.SubmitTerminal(Environment.ProcessPath!,items,"vertical",cancellation.Token));
                    var first=Path.Combine(capture,items[0].Id.ToString("N")+".json");var deadline=Stopwatch.StartNew();
                    while(!File.Exists(first)&&!submission.IsCompleted&&deadline.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(20);
                    Assert(File.Exists(first),"first pane was not submitted");await Task.Delay(250);
                    Assert(Directory.GetFiles(capture,"*.json").Length==1&&!submission.IsCompleted,"later split was sent without the first claim");
                    cancellation.Cancel();try{await submission;Assert(false,"cancellation was ignored");}catch(OperationCanceledException){}
                    File.WriteAllText(Path.Combine(capture,items[0].Id.ToString("N")+".release"),"release");
                    await Task.Delay(100);Assert(Directory.GetFiles(capture,"*.json").Length==1,"cancellation sent another pane");
                    var command=JsonSerializer.Deserialize<string[]>(File.ReadAllText(first))!;
                    Assert(command.Count(arg=>arg is "new-tab" or "split-pane")==1&&command.Contains(items[0].Id.ToString("D")),"submission contained multiple pane creations");
                }
                finally
                {
                    cancellation.Cancel();foreach(var item in items)File.WriteAllText(Path.Combine(capture,item.Id.ToString("N")+".release"),"release");
                    if(submission!=null)try{await submission;}catch(OperationCanceledException){}
                    Environment.SetEnvironmentVariable("AGENT_LAUNCHER_TERMINAL_FIXTURE",previous);
                }
            });
            await Test("CR10 disconnected pipe clients preserve the original monitor",async()=>{
                using var monitor=Process.GetProcessById(Store.Read<int>(Store.FilePath("monitor-pid.json")));_ = monitor.Handle;
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                for(var i=0;i<64;i++)
                {
                    using(var client=new System.IO.Pipes.NamedPipeClientStream(".",Store.Pipe,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous|System.IO.Pipes.PipeOptions.CurrentUserOnly))await client.ConnectAsync(deadline.Token);
                    if(i%8==0){var status=await Ipc.SendAsync(new("status",Guid.NewGuid()),deadline.Token);Assert(status.Ok&&status.Epoch==initial.Epoch&&!monitor.HasExited,"short-lived peer exited or replaced the monitor");}
                }
                var final=await Ipc.SendAsync(new("status",Guid.NewGuid()),deadline.Token);Assert(final.Ok&&final.Epoch==initial.Epoch&&!monitor.HasExited,"disconnect burst exited or replaced the monitor");
            });
            foreach(var mode in new[]{"release","crash"})await Test("P04 startup queue waits without spawning and continues after "+mode,async()=>{
                var ready=Store.FilePath("gate-"+mode+".ready");var release=Store.FilePath("gate-"+mode+".release");var output=Store.FilePath("gate-"+mode+".json");
                var gateInfo=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
                foreach(var arg in new[]{"--startup-gate",ready,release,mode})gateInfo.ArgumentList.Add(arg);
                using var holder=Process.Start(gateInfo)!;Process? runner=null;
                try
                {
                    var deadline=Stopwatch.StartNew();while(!File.Exists(ready)&&!holder.HasExited&&deadline.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(20);
                    Assert(File.Exists(ready),"startup gate fixture did not acquire ownership");
                    var item=Item("queued-"+mode,["--echo",output]);var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[item]));Assert(reserve.Ok,reserve.Message);
                    runner=Start("run",item.Id);deadline.Restart();
                    var claim=Path.Combine(Store.Root,"attempts",item.Id.ToString("N"),"claim");
                    while(!File.Exists(claim)&&!runner.HasExited&&deadline.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(20);
                    Assert(File.Exists(claim),"runner did not claim its queued attempt");
                    // Exceed the IPC budget while held: queueing must happen before begin, not inside a pipe request.
                    await Task.Delay(mode=="release"?9000:250);
                    var status=await Ipc.SendAsync(new("status",Guid.NewGuid()));
                    Assert(!runner.HasExited&&!File.Exists(output)&&Store.Read<Receipt>(Store.ReceiptPath(item.Id)) is null,"queued attempt spawned or failed before startup ownership");
                    Assert(status.Ok&&status.Entries!.Single(e=>e.Plan.Id==item.Id).State=="Pending","waiting for startup ownership consumed the grant or blocked status");
                    File.WriteAllText(release,"release");await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(holder.ExitCode==(mode=="crash"?97:0),"startup gate fixture did not exit as requested");
                    await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                    Assert(runner.ExitCode==0&&File.Exists(output)&&Store.Read<Receipt>(Store.ReceiptPath(item.Id)) is{Phase:"Finished",ExitCode:0},"queued attempt did not complete after "+mode);
                    status=await Ipc.SendAsync(new("status",Guid.NewGuid()));Assert(status.Entries!.Single(e=>e.Plan.Id==item.Id).State=="Settled","queued attempt did not settle");
                }
                finally
                {
                    File.WriteAllText(release,"release");await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    if(runner is not null){try{await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));}finally{runner.Dispose();}}
                }
            });
            Store.Save(Store.FilePath("integration-results.json"),evidence);
            return evidence.Any(e=>e.GetType().GetProperty("passed")?.GetValue(e) is false)?1:0;
        }
        if(terminalOnly)
        {
            if(!directBatch)await Test("Terminal two panes exact child cwd and arguments",TerminalPair);
            await Test("Terminal six panes (MaxConcurrentAgents cap) all become ready before any fixture exits",TerminalBatch);
            Store.Save(Store.FilePath("integration-results.json"),evidence);
            return evidence.Any(e=>e.GetType().GetProperty("passed")?.GetValue(e) is false)?1:0;
        }
        await Test("Pane memory warning latches hit history and tracks commit-based recovery and rebound",async()=>{
            var ready=Path.Combine(Store.Root,"alloc-pressure.ready");var go=Path.Combine(Store.Root,"alloc-pressure.go");
            var quiet=Path.Combine(Store.Root,"alloc-pressure.quiet");var released=Path.Combine(Store.Root,"alloc-pressure.released");var rehold=Path.Combine(Store.Root,"alloc-pressure.rehold");
            var stats=Path.Combine(Store.Root,"alloc-pressure.stats");
            // The fixture child must be a native-style allocator: measured evidence shows .NET children get no
            // kernel limit notice (0/6 refusals notified) while python does (1 notice per crossing episode).
            var script=Path.Combine(Store.Root,"alloc-pressure.py");
            File.WriteAllText(script,"""
import os,sys,time
ready,go,quiet,released,rehold,stats=sys.argv[1:7]
base=bytearray(1400*1024*1024)
for i in range(0,len(base),4096): base[i]=1
open(ready,'w').write('ready')
while not os.path.exists(go): time.sleep(0.05)
attempts=0;refused=0;end=time.time()+5
while time.time()<end:
    attempts+=1
    try:
        spike=bytearray(700*1024*1024)
        for i in range(0,len(spike),4096): spike[i]=1
    except MemoryError:
        refused+=1
    spike=None
    time.sleep(0.5)
open(stats,'w').write('attempts=%d;refused=%d'%(attempts,refused))
open(quiet,'w').write('quiet')
time.sleep(3)
base=None
open(released,'w').write('released')
time.sleep(3)
base=bytearray(1400*1024*1024)
for i in range(0,len(base),4096): base[i]=1
open(rehold,'w').write('rehold')
time.sleep(2)
""");
            // Freeze a custom 1536 MiB per-item cap into configuration so the begin recheck accepts the entry budget.
            var original=Store.Read<Configuration>(Store.FilePath("configuration.json"));
            var settings=new ResourceSettings{Mode="custom",JobMemoryLimit=1536*ResourcePolicy.MiB,PlanningBytes=ResourcePolicy.MinPlanningBytes,Pool=ResourcePolicy.PoolIndependent};
            var config=new Configuration();config.Draft.Project=cwd;config.Draft.Resource=settings;Store.Save(Store.FilePath("configuration.json"),config);
            var python=(Environment.GetEnvironmentVariable("PATH")??"").Split(';').Select(dir=>Path.Combine(dir,"python.exe")).FirstOrDefault(File.Exists)??throw new Exception("python.exe not on PATH");
            var item=new LaunchItem(Guid.NewGuid(),Guid.NewGuid(),1,"alloc pressure fixture",cwd,PhysicalDirectory.Workspace(cwd),false,new CommandSpec(python,[script,ready,go,quiet,released,rehold,stats],"exe"),1536*ResourcePolicy.MiB) with{PlanningBytes=settings.PlanningBytes,ResourceMode=settings.Mode,AgentId="codex"};
            try
            {
            var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[item]));Assert(reserve.Ok,reserve.Message);
            using var runner=Start("run",item.Id);
            try
            {
            var wait=Stopwatch.StartNew();
            while((!File.Exists(ready)||Store.Read<Receipt>(Store.ReceiptPath(item.Id))?.Phase!="Started")&&!runner.HasExited&&wait.Elapsed<TimeSpan.FromSeconds(15))await Task.Delay(25);
            Assert(File.Exists(ready)&&Store.Read<Receipt>(Store.ReceiptPath(item.Id))?.Phase=="Started","pressure fixture pane did not start with baseline held");
            using var job=new Job(Store.JobName(item.Id),openOnly:true);
            Assert(job.QueryMemory() is{LimitBytes:1536*ResourcePolicy.MiB},"monitor-created pane job lacks the entry budget as its kernel cap");
            File.WriteAllText(go,"go");
            // Retry phase: kernel posts a limit notice only once per crossing episode (probe evidence), so the
            // assertions cover the latch itself—not a per-refusal stream the kernel does not provide.
            var deadline=Stopwatch.StartNew();var sawLimitHit=false;string? lastDetail=null;
            while(!File.Exists(quiet)&&deadline.Elapsed<TimeSpan.FromSeconds(20))
            {
                if(Store.Read<Receipt>(Store.ReceiptPath(item.Id)) is{} sample)
                {
                    lastDetail=sample.Detail;
                    if(sample.Detail.Contains("触及")&&sample.JobMemory is{LimitReached:true})sawLimitHit=true;
                }
                await Task.Delay(100);
            }
            Assert(File.Exists(quiet)&&File.Exists(stats)&&File.ReadAllText(stats) is{} counters&&counters.Contains("refused=")&&!counters.Contains("refused=0"),"spike allocations were never refused; counters="+(File.Exists(stats)?File.ReadAllText(stats):"<none>"));
            Assert(sawLimitHit,"limit hit was never latched; last detail="+(lastDetail??"<none>"));
            // Hold phase: the 1400 MiB baseline stays above the 85% threshold, so no recovery note may appear
            // until the fixture actually releases; this pins recovery to the commit level, not to time passing.
            var releasedWait=Stopwatch.StartNew();var sawRecoveryBeforeRelease=false;
            while(!File.Exists(released)&&releasedWait.Elapsed<TimeSpan.FromSeconds(15))
            {
                if(Store.Read<Receipt>(Store.ReceiptPath(item.Id)) is{} sample){lastDetail=sample.Detail;if(sample.Detail.Contains("已回落"))sawRecoveryBeforeRelease=true;}
                await Task.Delay(100);
            }
            Assert(File.Exists(released),"fixture never reached the release phase");
            Assert(!sawRecoveryBeforeRelease,"recovery claimed while the above-threshold baseline was still held; last detail="+(lastDetail??"<none>"));
            // After release the commit drops far below the threshold: the note appears while the latch survives.
            deadline.Restart();
            while(deadline.Elapsed<TimeSpan.FromSeconds(10)&&(Store.Read<Receipt>(Store.ReceiptPath(item.Id)) is not{} probe||!probe.Detail.Contains("已回落")))await Task.Delay(100);
            var quietReceipt=Store.Read<Receipt>(Store.ReceiptPath(item.Id));
            Assert(quietReceipt is{JobMemory:{LimitReached:true}}&&quietReceipt.Detail.Contains("已回落"),"recovery note missing after release or latched hit history erased; last detail="+(quietReceipt?.Detail??"<none>"));
            // Rehold phase: committing back above the threshold must withdraw the note (history stays latched).
            var reboundWait=Stopwatch.StartNew();
            while(!File.Exists(rehold)&&reboundWait.Elapsed<TimeSpan.FromSeconds(15))await Task.Delay(100);
            Assert(File.Exists(rehold),"fixture never reached the rehold phase");
            deadline.Restart();
            while(deadline.Elapsed<TimeSpan.FromSeconds(10)&&(Store.Read<Receipt>(Store.ReceiptPath(item.Id)) is{} rebound&&rebound.Detail.Contains("已回落")))await Task.Delay(100);
            var reboundReceipt=Store.Read<Receipt>(Store.ReceiptPath(item.Id));
            Assert(reboundReceipt is{JobMemory:{LimitReached:true}}&&!reboundReceipt.Detail.Contains("已回落"),"recovery note survived the rebound; last detail="+(reboundReceipt?.Detail??"<none>"));
            await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var finished=Store.Read<Receipt>(Store.ReceiptPath(item.Id));
            Assert(runner.ExitCode==0&&finished is{Phase:"Finished",ExitCode:0}&&finished.JobMemory is{LimitReached:true},"final receipt lost the latched hit history; last detail="+(finished?.Detail??"<none>"));
            }
            finally
            {
                // The fixture is self-timed (~11 s after go): let it exit naturally so the ledger entry turns terminal;
                // revoke only clears Pending entries (Program.cs:703), so kill is the last resort.
                try{File.WriteAllText(go,"go");}catch{}
                try{await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));}catch{}
                try{if(!runner.HasExited){runner.Kill(true);await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}}catch{}
                try{await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:item.Id));}catch{}
                var settle=Stopwatch.StartNew();
                while(settle.Elapsed<TimeSpan.FromSeconds(5))
                {
                    var frame=await Ipc.SendAsync(new("status",Guid.NewGuid()));
                    var entry=frame.Entries?.FirstOrDefault(e=>e.Plan.Id==item.Id);
                    if(entry is null||(entry.State!="Started"&&entry.State!="Pending"))break;
                    await Task.Delay(200);
                }
            }
            }
            finally{try{Store.Save(Store.FilePath("configuration.json"),original??new Configuration());}catch{}}
        });
        await Test("Shared pool memory recovery note tracks commit fall and rebound across panes",async()=>{
            var readyA=Path.Combine(Store.Root,"pool-pressure.ready.a");var readyB=Path.Combine(Store.Root,"pool-pressure.ready.b");
            var go=Path.Combine(Store.Root,"pool-pressure.go");var quiet=Path.Combine(Store.Root,"pool-pressure.quiet");
            var released=Path.Combine(Store.Root,"pool-pressure.released");var rehold=Path.Combine(Store.Root,"pool-pressure.rehold");
            var stats=Path.Combine(Store.Root,"pool-pressure.stats");var finish=Path.Combine(Store.Root,"pool-pressure.finish");
            foreach(var marker in new[]{readyA,readyB,go,quiet,released,rehold,stats,finish})if(File.Exists(marker))File.Delete(marker);
            // VirtualAlloc commits immediately and refuses with NULL instead of throwing; the old bytearray+page-touch
            // fixture stalled for 40 s under paging pressure and MemoryError is unsafe under commit refusal.
            var script=Path.Combine(Store.Root,"pool-pressure.py");
            File.WriteAllText(script,"""
import os,sys,time,ctypes
which,ready,go,quiet,released,rehold,stats,finish=sys.argv[1:9]
k=ctypes.windll.kernel32
k.VirtualAlloc.restype=ctypes.c_void_p
k.VirtualAlloc.argtypes=[ctypes.c_void_p,ctypes.c_size_t,ctypes.c_uint,ctypes.c_uint]
k.VirtualFree.argtypes=[ctypes.c_void_p,ctypes.c_size_t,ctypes.c_uint]
def commit(mib):
    return k.VirtualAlloc(None,mib*1024*1024,0x3000,0x04) or None
def release(p):
    if p: k.VirtualFree(p,0,0x8000)
if which=='a':
    base=commit(500)
    if not base: sys.exit(3)
    open(ready,'w').write('ready')
    while not os.path.exists(go): time.sleep(0.05)
    attempts=0;refused=0;end=time.time()+4
    while time.time()<end:
        attempts+=1
        spike=commit(150)
        if spike is None: refused+=1
        else: release(spike)
        time.sleep(0.3)
    open(stats,'w').write('attempts=%d;refused=%d'%(attempts,refused))
    open(quiet,'w').write('quiet')
    time.sleep(3)
    release(base);base=None
    open(released,'w').write('released')
    time.sleep(15)
    base=commit(500)
    if not base: sys.exit(4)
    open(rehold,'w').write('rehold')
    time.sleep(3)
else:
    held=commit(500)
    if not held: sys.exit(3)
    open(ready,'w').write('ready')
    while not os.path.exists(finish): time.sleep(0.1)
    release(held)
""");
            // MinJobMemoryLimit is 1 GiB: a literal 700 MiB item cap cannot pass ValidateSettings/ValidateLaunchItem.
            // 1 GiB is behaviorally inert here (a peaks at 500+150=650 MiB); the pool cap and 85% window are unchanged.
            var original=Store.Read<Configuration>(Store.FilePath("configuration.json"));
            var settings=new ResourceSettings{Mode="custom",Pool=ResourcePolicy.PoolShared,JobMemoryLimit=ResourcePolicy.GiB,PoolMemoryLimit=1100*ResourcePolicy.MiB,PlanningBytes=ResourcePolicy.MinPlanningBytes};
            var config=new Configuration();config.Draft.Project=cwd;config.Draft.Resource=settings;Store.Save(Store.FilePath("configuration.json"),config);
            var python=(Environment.GetEnvironmentVariable("PATH")??"").Split(';').Select(dir=>Path.Combine(dir,"python.exe")).FirstOrDefault(File.Exists)??throw new Exception("python.exe not on PATH");
            var poolId=Guid.NewGuid();var batch=Guid.NewGuid();
            LaunchItem Make(string which,int position)=>new(Guid.NewGuid(),batch,position,"pool pressure "+which,cwd,PhysicalDirectory.Workspace(cwd),true,new CommandSpec(python,[script,which,which=="a"?readyA:readyB,go,quiet,released,rehold,stats,finish],"exe"),ResourcePolicy.GiB,"agent",PlanningBytes:settings.PlanningBytes,ResourceMode:settings.Mode,PoolId:poolId,AgentId:"codex");
            var a=Make("a",1);var b=Make("b",2);
            Receipt? ReceiptOf(LaunchItem item)=>Store.Read<Receipt>(Store.ReceiptPath(item.Id));
            string? aDetail=null,bDetail=null;
            void Sample(){if(ReceiptOf(a) is{} ra)aDetail=ra.Detail;if(ReceiptOf(b) is{} rb)bDetail=rb.Detail;}
            string Diag()=>" a="+(aDetail??"<none>")+" b="+(bDetail??"<none>")+" stats="+(File.Exists(stats)?File.ReadAllText(stats):"<none>");
            var runners=new List<Process>();
            try
            {
            var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[a,b]));Assert(reserve.Ok,reserve.Message);
            var ra=Start("run",a.Id);runners.Add(ra);var rb=Start("run",b.Id);runners.Add(rb);
            try
            {
            var wait=Stopwatch.StartNew();
            while((!File.Exists(readyA)||!File.Exists(readyB)||ReceiptOf(a)?.Phase!="Started"||ReceiptOf(b)?.Phase!="Started")&&!ra.HasExited&&!rb.HasExited&&wait.Elapsed<TimeSpan.FromSeconds(30))await Task.Delay(100);
            Sample();
            Assert(File.Exists(readyA)&&File.Exists(readyB)&&ReceiptOf(a)?.Phase=="Started"&&ReceiptOf(b)?.Phase=="Started","pool pressure panes did not start with baseline held;"+Diag());
            using(var poolJob=new Job(Store.PoolName(poolId),openOnly:true))Assert(poolJob.QueryMemory() is{LimitBytes:1100*ResourcePolicy.MiB},"monitor-created pool job lacks the 1100 MiB kernel cap");
            File.WriteAllText(go,"go");
            var deadline=Stopwatch.StartNew();var aNotice=false;var bNotice=false;
            while(!File.Exists(quiet)&&deadline.Elapsed<TimeSpan.FromSeconds(25))
            {
                Sample();
                // The offending pane (a) sees its own PID attributed; only the peer pane (b) sees the unconfirmed notice.
                if(aDetail?.Contains("内核已报告该共享池的内存分配触及 Job 上限")==true)aNotice=true;
                if(bDetail?.Contains("同一共享池已收到内核分配拒绝通知")==true)bNotice=true;
                await Task.Delay(100);
            }
            Sample();
            Assert(File.Exists(quiet)&&File.Exists(stats)&&File.ReadAllText(stats) is{} counters&&counters.Contains("refused=")&&!counters.Contains("refused=0"),"spike allocations were never refused;"+Diag());
            Assert(aNotice&&bNotice,"pool limit notice missing on a pane (a="+aNotice+" b="+bNotice+");"+Diag());
            // Hold phase: 500+500 MiB baseline sits above 85% of 1100 MiB (935 MiB), so no recovery note may appear.
            var holdWait=Stopwatch.StartNew();string? earlyRecovery=null;
            while(!File.Exists(released)&&holdWait.Elapsed<TimeSpan.FromSeconds(20))
            {
                Sample();
                if(aDetail?.Contains("共享池当前已回落")==true)earlyRecovery="a";
                else if(bDetail?.Contains("共享池当前已回落")==true)earlyRecovery="b";
                await Task.Delay(100);
            }
            Assert(File.Exists(released),"fixture never reached the release phase;"+Diag());
            Assert(earlyRecovery is null,"recovery note appeared while the above-threshold baseline was still held ("+earlyRecovery+");"+Diag());
            // After a releases its 500 MiB the pool drops to ~500 MiB. Only the offending pane latches poolOffender
            // (Program.cs:273), so only its poolRecovered can converge (Program.cs:278); the peer consumes the notice but
            // never holds a pool offender. Record b as diagnostic instead of demanding a note the Worker cannot produce.
            DateTimeOffset? aRecoveredAt=null,bRecoveredAt=null;
            deadline.Restart();
            while(deadline.Elapsed<TimeSpan.FromSeconds(12)&&aRecoveredAt is null)
            {
                Sample();
                if(aDetail?.Contains("共享池当前已回落")==true&&aRecoveredAt is null)aRecoveredAt=DateTimeOffset.UtcNow;
                if(bDetail?.Contains("共享池当前已回落")==true&&bRecoveredAt is null)bRecoveredAt=DateTimeOffset.UtcNow;
                await Task.Delay(100);
            }
            var aQuiet=ReceiptOf(a);
            Assert(aRecoveredAt is not null,"offender pane recovery note missing after release (aAt="+(aRecoveredAt?.ToString("O")??"<none>")+" bAt="+(bRecoveredAt?.ToString("O")??"<none>")+");"+Diag());
            Assert(aQuiet?.PoolMemory is{LimitReached:true},"pool hit history was not latched on the offending pane;"+Diag());
            // Rebound: a recommits 500 MiB. The offender note must withdraw while its receipt is still Started, and both
            // latched notice texts must remain (a Finished receipt would withdraw notes vacuously).
            var reboundWait=Stopwatch.StartNew();
            while(!File.Exists(rehold)&&reboundWait.Elapsed<TimeSpan.FromSeconds(25))await Task.Delay(100);
            Assert(File.Exists(rehold),"fixture never reached the rehold phase;"+Diag());
            deadline.Restart();string? aReboundDetail=null,bReboundDetail=null;
            while(deadline.Elapsed<TimeSpan.FromSeconds(12)&&(aReboundDetail is null||bReboundDetail is null))
            {
                if(ReceiptOf(a) is{Phase:"Started"} ra2&&!ra2.Detail.Contains("共享池当前已回落"))aReboundDetail=ra2.Detail;
                if(ReceiptOf(b) is{Phase:"Started"} rb2&&rb2.Detail.Contains("同一共享池已收到内核分配拒绝通知"))bReboundDetail=rb2.Detail;
                await Task.Delay(100);
            }
            Assert(aReboundDetail is not null,"offender recovery note survived the rebound or a finished too early;"+Diag());
            Assert(bReboundDetail is not null,"peer pane lost its latched pool notice after the rebound;"+Diag());
            Assert(aReboundDetail!.Contains("内核已报告该共享池的内存分配触及 Job 上限")&&bReboundDetail!.Contains("同一共享池已收到内核分配拒绝通知"),"latched pool notice text vanished after rebound; a="+aReboundDetail+" b="+bReboundDetail);
            File.WriteAllText(finish,"finish");
            var endWait=Stopwatch.StartNew();
            while((!ra.HasExited||!rb.HasExited)&&endWait.Elapsed<TimeSpan.FromSeconds(15))await Task.Delay(100);
            Assert(ra.HasExited&&rb.HasExited&&ra.ExitCode==0&&rb.ExitCode==0,"pool pressure runners did not exit cleanly (aExit="+(ra.HasExited?ra.ExitCode:(int?)null)+" bExit="+(rb.HasExited?rb.ExitCode:(int?)null)+");"+Diag());
            Assert(ReceiptOf(a) is{Phase:"Finished"}&&ReceiptOf(b) is{Phase:"Finished"},"pool pressure receipts did not finish;"+Diag());
            }
            finally
            {
                try{File.WriteAllText(go,"go");}catch{}
                try{File.WriteAllText(finish,"finish");}catch{}
                foreach(var runner in runners){try{await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));}catch{}try{if(!runner.HasExited){runner.Kill(true);await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}}catch{}}
                try{await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:a.Id));}catch{}
                try{await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:b.Id));}catch{}
                var settle=Stopwatch.StartNew();
                while(settle.Elapsed<TimeSpan.FromSeconds(5))
                {
                    var frame=await Ipc.SendAsync(new("status",Guid.NewGuid()));
                    var pending=frame.Entries?.Any(e=>(e.Plan.Id==a.Id||e.Plan.Id==b.Id)&&(e.State=="Started"||e.State=="Pending"))==true;
                    if(!pending)break;
                    await Task.Delay(200);
                }
            }
            }
            finally{try{Store.Save(Store.FilePath("configuration.json"),original??new Configuration());}catch{}}
        });
        await Test("P04 begin rechecks complete resource policy without consuming grant",async()=>{
            var type=System.Reflection.Assembly.LoadFrom(Store.WorkerPath.Replace(".exe",".dll",StringComparison.OrdinalIgnoreCase)).GetType("AgentLauncher.Worker.Monitor",true)!;
            const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            System.Reflection.FieldInfo Field(string name)=>type.GetField(name,flags)??throw new MissingFieldException(type.FullName,name);
            var handle=type.GetMethod("Handle",flags)??throw new MissingMethodException(type.FullName,"Handle");
            var healthySample=new MemorySnapshot(16*ResourcePolicy.GiB,12*ResourcePolicy.GiB,32*ResourcePolicy.GiB,24*ResourcePolicy.GiB,5,DateTimeOffset.UtcNow);
            // Conservative mode keeps the begin recheck complete: resource shortfalls stay hard refusals instead of practical-mode advice.
            var owned=Path.Combine(Store.Root,"recheck-owned-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(owned);
            var config=new Configuration();config.Draft.Project=cwd;config.Draft.DirectoryMode="shared";config.Draft.Resource=new(){Mode="conservative"};
            Store.Save(Path.Combine(owned,"configuration.json"),config);
            foreach(var sample in new[]{healthySample with{CommitAvailable=ResourcePolicy.GiB},healthySample with{Available=ResourcePolicy.DefaultPhysicalFloor(healthySample.Total)+ResourcePolicy.DefaultPlanningBytes-1},healthySample with{CpuPercent=double.NaN},healthySample with{At=DateTimeOffset.UtcNow.AddSeconds(-10)}})
            {
                using var monitor=(IDisposable)Activator.CreateInstance(type,true)!;var item=Item("recheck",["--hold","1"]);
                // The begin recheck derives policy from the entry's frozen Mode, not from configuration.json.
                var entry=new ControlEntry{Plan=item,Digest=Store.Digest(item),Mode="conservative"};
                Field("sample").SetValue(monitor,sample);Field("ledger").SetValue(monitor,new Ledger{Entries=[entry]});
                // Keep the invalid evidence at the sampling boundary now that begin refreshes live usage.
                Field("sampleOverride").SetValue(monitor,(Func<MemorySnapshot>)(()=>sample));
                Field("ledgerPath").SetValue(monitor,Path.Combine(owned,"recheck-"+Guid.NewGuid().ToString("N")+".json"));
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
            var finItem=Item("lost-finished",["--hold","1"]);var deadTreeItem=Item("lost-dead-tree",["--hold","1"]);var liveItem=Item("lost-live",["--hold","1"]);var rebootedItem=Item("lost-rebooted",["--hold","1"]);
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
                    var rebootedEntry=new ControlEntry{Plan=rebootedItem,Digest=Store.Digest(rebootedItem),State="Creating",Detail="正在创建进程"};
                    Store.Save(Store.ReceiptPath(finItem.Id),new Receipt("Finished",parentPid,parentTime,1,"全部受管进程已退出，退出码 1",DateTimeOffset.UtcNow));
                    Store.Save(Store.ReceiptPath(deadTreeItem.Id),new Receipt("Exited",parentPid,parentTime,-1073740791,"进程已退出，退出码 -1073740791",DateTimeOffset.UtcNow));
                    Store.Save(Store.ReceiptPath(liveItem.Id),new Receipt("Started",survivorPid,survivorTime,null,"已启动进程（不代表任务完成）",DateTimeOffset.UtcNow));
                    Store.Save(Store.ReceiptPath(rebootedItem.Id),new Receipt("Exited",parentPid,DateTime.UtcNow.Subtract(TimeSpan.FromMilliseconds(AgentLauncher.Windows.Native.GetTickCount64())).AddSeconds(-1).ToFileTimeUtc(),-1073740791,"进程已退出，退出码 -1073740791",DateTimeOffset.UtcNow));
                    using var monitor=(IDisposable)Activator.CreateInstance(type,true)!;
                    Field("sample").SetValue(monitor,new MemorySnapshot(16*ResourcePolicy.GiB,12*ResourcePolicy.GiB,32*ResourcePolicy.GiB,24*ResourcePolicy.GiB,5,DateTimeOffset.UtcNow));
                    Field("ledger").SetValue(monitor,new Ledger{Entries=[finEntry,deadTreeEntry,liveEntry,rebootedEntry]});
                    Field("ledgerPath").SetValue(monitor,Path.Combine(Store.Root,"reconcile-"+Guid.NewGuid().ToString("N")+".json"));
                    Field("root").SetValue(monitor,new Job(isolatedRoot,root:true));
                    var response=(Response)handle.Invoke(monitor,[new Request("reconcile",Guid.NewGuid())])!;
                    Assert(finEntry.State=="Settled"&&finEntry.Detail.Contains("核对释放"),"finished-tree receipt was not released: "+finEntry.State+" "+finEntry.Detail);
                    Assert(deadTreeEntry.State=="Creating"&&deadTreeEntry.Detail.Contains("拒绝"),"dead parent with a live surviving tree member was released: "+deadTreeEntry.State+" "+deadTreeEntry.Detail);
                    Assert(rebootedEntry.State=="Settled"&&rebootedEntry.Detail.Contains("早于本次系统启动"),"pre-boot receipt was not released by the reboot proof: "+rebootedEntry.State+" "+rebootedEntry.Detail);
                    Assert(liveEntry.State=="Creating"&&liveEntry.Detail.Contains("仍在运行"),"entry with a live recorded process did not refuse: "+liveEntry.State+" "+liveEntry.Detail);
                    Assert(response.Ok==false&&response.Entries?.SingleOrDefault(e=>e.Plan.Id==finItem.Id)!=null,"reconcile outcome did not keep refusals while reporting the release");
                    Assert(!survivor.Process.HasExited,"reconcile killed the surviving tree member");
                }
                finally{try{survivor.Process.Kill();await survivor.Process.WaitForExitAsync();}catch(System.InvalidOperationException){}}
            }
        });
        await Test("R-pool-handle-lost reconcile keeps unprovable pool charged and discloses retention",async()=>{
            var type=System.Reflection.Assembly.LoadFrom(Store.WorkerPath.Replace(".exe",".dll",StringComparison.OrdinalIgnoreCase)).GetType("AgentLauncher.Worker.Monitor",true)!;
            const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            System.Reflection.FieldInfo Field(string name)=>type.GetField(name,flags)??throw new MissingFieldException(type.FullName,name);
            var handle=type.GetMethod("Handle",flags)??throw new MissingMethodException(type.FullName,"Handle");
            var poolId=Guid.NewGuid();var poolLimit=2*ResourcePolicy.AgentBudget;
            var finItem=Item("pool-lost-finished",["--hold","1"]) with{PoolId=poolId,Shared=true};
            var isolatedRoot=Store.RootJob+"-reconcile-pool-"+Guid.NewGuid().ToString("N");
            using var rootJob=new Job(isolatedRoot,root:true);
            using(var poolJob=new Job(Store.PoolName(poolId),poolLimit))
            {
                // ACC-S3B03 class: a direct pool member with no item job and no ledger entry survives the handle loss.
                using var direct=new ProtectedProcess(new(Environment.ProcessPath!,["--hold","60000"],"exe"),cwd,rootJob,poolJob);
                direct.Resume();
                for(var i=0;i<50&&poolJob.Members().Length<1;i++)await Task.Delay(100);
                Assert(poolJob.Members().Length==1,"pool fixture did not contain the direct member");
                var directPid=direct.Process.Id;var directTime=direct.CreationTime;
                poolJob.Dispose();
                var openFailed=false;try{using var probe=new Job(Store.PoolName(poolId),openOnly:true);}catch(System.ComponentModel.Win32Exception e)when(e.NativeErrorCode==2){openFailed=true;}
                Assert(openFailed,"scenario broken: pool object still openable after last handle closed");
                Assert(!direct.Process.HasExited,"scenario broken: surviving direct member exited with the pool object");
                try
                {
                    var sharedStatus=await Ipc.SendAsync(new("status",Guid.NewGuid()));
                    Assert(sharedStatus.Ok,"reconcile fixture contaminated the shared monitor: "+sharedStatus.Message);
                    var finEntry=new ControlEntry{Plan=finItem,Digest=Store.Digest(finItem),State="Creating",Detail="正在创建进程"};
                    Store.Save(Store.ReceiptPath(finItem.Id),new Receipt("Finished",directPid,directTime,0,"全部受管进程已退出，退出码 0",DateTimeOffset.UtcNow));
                    using var monitor=(IDisposable)Activator.CreateInstance(type,true)!;
                    Field("sample").SetValue(monitor,new MemorySnapshot(16*ResourcePolicy.GiB,12*ResourcePolicy.GiB,32*ResourcePolicy.GiB,24*ResourcePolicy.GiB,5,DateTimeOffset.UtcNow));
                    var ledger=new Ledger{Entries=[finEntry],Pools=[new PoolRecord{Id=poolId,MemoryLimit=poolLimit,PolicyHash="fixture",BatchId=finItem.BatchId}]};
                    Field("ledger").SetValue(monitor,ledger);
                    Field("ledgerPath").SetValue(monitor,Path.Combine(Store.Root,"reconcile-pool-"+Guid.NewGuid().ToString("N")+".json"));
                    Field("root").SetValue(monitor,new Job(isolatedRoot,root:true));
                    var response=(Response)handle.Invoke(monitor,[new Request("reconcile",Guid.NewGuid())])!;
                    Assert(finEntry.State=="Settled"&&finEntry.Detail.Contains("核对释放"),"finished member entry was not released: "+finEntry.State+" "+finEntry.Detail);
                    Assert(ledger.Pools.Count==1,"pool record dropped while its direct member survives (ACC-S3B03 invariant)");
                    Assert(response.AdmissionBlockedReason?.Contains("资源池")==true,"lost pool stopped charging admission: "+response.AdmissionBlockedReason);
                    Assert(response.AdmissionBlockedReason?.Contains("自动释放")==true,"lost-pool guidance does not state the truthful reboot retirement: "+response.AdmissionBlockedReason);
                    Assert(response.Message.Contains("资源池")&&response.Message.Contains("重启"),"reconcile did not disclose retained pools: "+response.Message);
                    Assert(!direct.Process.HasExited,"reconcile killed the surviving direct member");
                    // Death is no more provable than survival without the handle: retention must outlive the member.
                    direct.Process.Kill();await direct.Process.WaitForExitAsync();
                    var second=(Response)handle.Invoke(monitor,[new Request("reconcile",Guid.NewGuid())])!;
                    Assert(ledger.Pools.Count==1,"pool record dropped after its direct member died unprovably (retain until boot rollover)");
                    Assert(second.AdmissionBlockedReason?.Contains("资源池")==true,"pool hold cleared without proof of member exit: "+second.AdmissionBlockedReason);
                    Assert(second.Message.Contains("资源池")&&second.Message.Contains("重启"),"post-death reconcile lost the retention disclosure: "+second.Message);
                    Assert(finEntry.State=="Settled","settled member entry was rewritten: "+finEntry.State+" "+finEntry.Detail);
                }
                finally{try{direct.Process.Kill();await direct.Process.WaitForExitAsync();}catch(System.InvalidOperationException){}}
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
            Assert(receipt?.JobMemory is {PeakBytes:>0} memory&&memory.LimitBytes==item.Budget,"completion receipt lost kernel memory evidence");
            var status=await Ipc.SendAsync(new("status",Guid.NewGuid()));Assert(status.Entries!.Single(e=>e.Plan.Id==item.Id).State=="Settled","budget not released");
        });
        await Test("Git prepare process uses protected capture",async()=>{
            var git=Commands.FindExe("git.exe")!;var result=await ControlledCommand.RunAsync(new(git,["--version"],"exe"),cwd);Assert(result.Code==0&&result.Output.StartsWith("git version"),result.Error+result.Output);
        });
        await Test("F09 git launch disables fsmonitor and interactive prompts",async()=>{
            var output=Path.Combine(Store.Root,"git-env.txt");var result=await ControlledCommand.RunAsync(new(Environment.ProcessPath!,["--dump-env",output],"exe"),cwd);Assert(result.Code==0,result.Error);
            var text=await File.ReadAllTextAsync(output);Assert(text.Contains("GIT_TERMINAL_PROMPT=0\n")&&text.Contains("GCM_INTERACTIVE=never\n")&&text.Contains("\ncore.fsmonitor=false"),text);
        });
        await Test("F09 git preparation reaps a leftover child instead of waiting",async()=>{
            var pidFile=Path.Combine(Store.Root,"git-child-pid.txt");var started=Stopwatch.StartNew();
            var work=ControlledCommand.RunAsync(new(Environment.ProcessPath!,["--spawn-child","200","60000",pidFile],"exe"),cwd);
            var finished=await Task.WhenAny(work,Task.Delay(15000));Assert(finished==work,"git preparation still waited on the surviving child");
            var result=await work;Assert(result.Code==0&&started.Elapsed<TimeSpan.FromSeconds(12),result.Error+" elapsed="+started.Elapsed);
            var pid=int.Parse(await File.ReadAllTextAsync(pidFile),System.Globalization.CultureInfo.InvariantCulture);var alive=true;
            for(var i=0;i<30&&alive;i++){try{using var child=Process.GetProcessById(pid);alive=!child.HasExited;}catch(ArgumentException){alive=false;}if(alive)await Task.Delay(100);}
            Assert(!alive,"surviving git child was not reaped");
        });
        await Test("Real Omp Codex and Claude capability probes gate presets by version",async()=>{
            foreach(var agent in new[]{"omp","codex","claude"})
            {
                var found=Commands.FindExe(Agents.Get(agent).Command);evidence.Add(new{name=agent+"-found",path=found});
                if(found==null){evidence.Add(new{name=agent+"-probe",result="not installed"});continue;}
                var capability=await AgentProbe.ProbeCapabilityAsync(agent,new(),cwd,TimeSpan.FromSeconds(15));
                evidence.Add(new{name=agent+"-capability",version=capability.Version,note=capability.Note,fingerprint=capability.Fingerprint[..12]});
                Assert(capability.Fingerprint.Length==64,$"{agent} fingerprint missing");
                if(capability.Version==null)continue;
                var verified=agent switch {"omp"=>capability.Version is AgentPresets.OmpVerifiedVersion or AgentPresets.OmpCurrentVerifiedVersion,"codex"=>capability.Version==AgentPresets.CodexVerifiedVersion,"claude"=>capability.Version==AgentPresets.ClaudeVerifiedVersion,_=>false};
                Assert(capability.Presets.Count>0==verified&&capability.Presets.All(p=>p.AgentId==agent),$"{agent} presets offered outside its verified version");
                if(verified)Assert(capability.Note.Contains("版本与预设已按此安装核对"),$"{agent} verified version not reported: {capability.Note}");
            }
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
            var d=new Draft{Project=cwd,DirectoryMode="shared"};d.Slots.ForEach(s=>s.Enabled=false);d.Slots[0].Enabled=true;d.Slots[0].Profiles["omp"]=new(){Executable=Path.Combine(shim,"tool.cmd")};
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
        await Test("UX12 custody survives both runner and monitor loss without releasing a live process",async()=>
        {
            var item=Item("custody-recovery",["--hold","4500"]);var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[item]));Assert(reserve.Ok,reserve.Message);
            using var runner=Start("run",item.Id);Receipt? receipt=null;
            for(var i=0;i<50;i++){receipt=Store.Read<Receipt>(Store.ReceiptPath(item.Id));if(receipt?.Phase=="Started")break;await Task.Delay(100);}
            Assert(receipt?.Phase=="Started","fixture never started");
            Assert(File.Exists(Store.FilePath($"attempts/{item.Id:N}/baseline.json")),"runner omitted launch baseline evidence");
            runner.Kill();await runner.WaitForExitAsync();
            var pid=Store.Read<int>(Store.FilePath("monitor-pid.json"));using(var monitor=Process.GetProcessById(pid)){monitor.Kill();await monitor.WaitForExitAsync();}
            using(var child=Process.GetProcessById(receipt!.Pid))Assert(!child.HasExited,"crash ended the Agent");
            using(var retained=new Job(Store.JobName(item.Id),openOnly:true))Assert(retained.Members().Contains(receipt!.Pid),"custody lost live item handle");
            var restored=await Ipc.EnsureMonitorAsync();Assert(restored.Ok,restored.Message);
            var live=await Ipc.SendAsync(new("status",Guid.NewGuid()));Assert(!Store.IsTerminal(live.Entries!.Single(e=>e.Plan.Id==item.Id)),"live process released");
            ControlEntry? settled=null;
            for(var i=0;i<80;i++){var current=await Ipc.SendAsync(new("status",Guid.NewGuid()));settled=current.Entries?.SingleOrDefault(e=>e.Plan.Id==item.Id);if(settled?.State=="Settled")break;await Task.Delay(100);}
            Assert(settled?.State=="Settled"&&settled.ExitCode is null,"drained recovered job did not settle honestly");
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
        await Test("F09/R23 remote sync endpoints follow their isolated worktrees",async()=>{
            var repo=Path.Combine(Store.Root,"tiny-repo");
            var host=new RemoteHost(Guid.NewGuid(),"test","first.invalid",22,"root","agent",["omp"],null,WorkspaceSyncMode.Mutagen,MemoryLimitMib:2048);
            var config=new Configuration{RemoteHosts=[host]};
            var draft=new Draft{Project=repo,Slots=[new(){Enabled=true,RemoteHostId=host.Id,RemoteDirectory="/work/one"},new(){Enabled=true,RemoteHostId=host.Id,RemoteDirectory="/work/two"}]};
            var preparer=new WorkspacePreparer();
            var plans=await preparer.PrepareAsync(draft,_=>{},_=>Task.FromResult(false),default,configuration:config);
            Assert(plans.Count==2&&plans[0].Cwd!=plans[1].Cwd&&plans.All(p=>p.RemoteExecution?.WorkspaceSync?.LocalPath==p.Cwd&&p.Cwd!=PhysicalDirectory.Resolve(repo).Path),"remote sync escaped the prepared checkout");
            Assert(plans.All(p=>p.RemoteExecution!.MemoryLimitBytes==2L*1024*1024*1024),"主机 MiB 上限未换算为字节写入计划");
            config.RemoteHosts[0]=host with{Address="second.invalid"};
            var replay=await preparer.PrepareAsync(draft,_=>{},_=>Task.FromResult(false),default,configuration:config);
            Assert(replay.Select(p=>p.Cwd).SequenceEqual(plans.Select(p=>p.Cwd))&&replay.All(p=>p.RemoteExecution is {Host:"second.invalid"}&&p.RemoteExecution.WorkspaceSync?.LocalPath==p.Cwd),"replay used stale host or endpoint");
            draft.Slots[1].RemoteDirectory="/work/one";
            try{await preparer.PrepareAsync(draft,_=>{},_=>Task.FromResult(false),default,configuration:config);throw new Exception("isolated slots accepted a shared remote endpoint");}
            catch(IOException e){Assert(e.Message.Contains("远程目录"),e.Message);}
            // 三台同权（§88）：同步模式 None 等同于互相断开，启动准备必须拒收。
            var noneHost=host with{WorkspaceSyncMode=WorkspaceSyncMode.None};
            var noneDraft=new Draft{Project=repo,Slots=[new(){Enabled=true,RemoteHostId=noneHost.Id,RemoteDirectory="/work/none"}]};
            try{await preparer.PrepareAsync(noneDraft,_=>{},_=>Task.FromResult(false),default,configuration:new Configuration{RemoteHosts=[noneHost]});throw new Exception("sync mode None was accepted for launch");}
            catch(IOException e){Assert(e.Message.Contains("同权"),e.Message);}
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
            var terminal=Commands.Terminal("");
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"test-root.txt"),Store.Root);
            var output=Path.Combine(Store.Root,"literal-title.json");var item=Item("review;literal-title-tail\\;中文",["--echo",output,"title-is-data"]) with{Shared=true};
            var reserved=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[item]));Assert(reserved.Ok,reserved.Message);
            Commands.SubmitTerminal(terminal,[item],"vertical");
            for(var i=0;i<100&&Store.Read<Receipt>(Store.ReceiptPath(item.Id))?.Phase!="Finished";i++)await Task.Delay(100);
            Assert(Store.Read<Receipt>(Store.ReceiptPath(item.Id)) is{Phase:"Finished",ExitCode:0}&&File.Exists(output),"literal title changed Terminal command parsing");await Ipc.SendAsync(new("status",Guid.NewGuid()));
        });
        await Test("Terminal two panes exact child cwd and arguments",TerminalPair);
        await Test("Terminal six panes (MaxConcurrentAgents cap) all become ready before any fixture exits",TerminalBatch);
        await Test("ACC-S3B01 shared pool bills once, keeps kernel caps and launches on a root-pool-item chain",async()=>{
            var settings=new ResourceSettings{Pool=ResourcePolicy.PoolShared,JobMemoryLimit=ResourcePolicy.GiB,PoolMemoryLimit=4*ResourcePolicy.GiB,PlanningBytes=ResourcePolicy.DefaultPlanningBytes};
            var config=new Configuration();config.Draft.Project=cwd;config.Draft.Resource=settings;Store.Save(Store.FilePath("configuration.json"),config);
            var poolId=Guid.NewGuid();var batch=Guid.NewGuid();
            LaunchItem Shared(string name,int position)=>new(Guid.NewGuid(),batch,position,name,cwd,PhysicalDirectory.Workspace(cwd),true,new(Environment.ProcessPath!,["--hold","1"],"exe"),ResourcePolicy.GiB,"agent",PlanningBytes:ResourcePolicy.DefaultPlanningBytes,ResourceMode:"practical",PoolId:poolId,AgentId:"codex");
            var items=new[]{Shared("pool-a",1),Shared("pool-b",2)};
            var reserved=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),items));Assert(reserved.Ok,reserved.Message);
            var ledger=LedgerMigration.Accept(Store.Read<Ledger>(Store.FilePath("control.json"))!);Assert(ledger.Pools.Count==1&&ledger.Pools[0].MemoryLimit==4*ResourcePolicy.GiB,"shared pool record missing");
            using var root=new Job(Store.RootJob,openOnly:true);
            using var attempt=new Mutex(false,Store.AttemptLock(items[0].Id));attempt.WaitOne();
            try
            {
                var began=Sync(Ipc.SendAsync(new("begin",Guid.NewGuid(),Attempt:items[0].Id)));Assert(began.Ok,began.Message);
                JobCustody.Retain(items[0]); // This fixture creates the protected child directly, so perform the runner's custody handshake.
                using var process=LaunchPooled(root,poolId,items[0].Id,["--hold","2000"]);
                using(var pool=new Job(Store.PoolName(poolId),openOnly:true))using(var item=new Job(Store.JobName(items[0].Id),openOnly:true))
                { Assert(pool.QueryMemory() is{LimitBytes:4*ResourcePolicy.GiB},"pool kernel cap missing");Assert(item.QueryMemory() is{LimitBytes:ResourcePolicy.GiB},"item kernel cap missing"); }
                Assert(process.Process.WaitForExit(10000),"pool chain fixture timed out");
                Store.Save(Store.ReceiptPath(items[0].Id),new Receipt("Finished",process.Process.Id,0,0,"全部受管进程已退出。",DateTimeOffset.UtcNow));
            }
            finally { attempt.ReleaseMutex(); }
            var settled=false;for(var i=0;i<60&&!settled;i++){var status=await Ipc.SendAsync(new("status",Guid.NewGuid()));settled=status.Entries?.Any(e=>e.Plan.Id==items[0].Id&&e.State=="Settled")==true;if(!settled)await Task.Delay(100);}
            Assert(settled,"shared pool entry never settled");
            var smallPool=Guid.NewGuid();var smallItem=Store.JobName(Guid.NewGuid());var ready=Store.FilePath(Path.Combine("pools",smallPool.ToString("N")+"-factory.ready"));var release=Store.FilePath(Path.Combine("pools",smallPool.ToString("N")+"-factory.release"));
            Directory.CreateDirectory(Path.GetDirectoryName(ready)!);File.Delete(ready);File.Delete(release);
            using(var factory=new ProtectedProcess(new(Store.WorkerPath,["pool-host",Store.PoolName(smallPool),smallItem,ready,release,"1",(192*ResourcePolicy.MiB).ToString(),(96*ResourcePolicy.MiB).ToString()],"exe"),cwd,[root]))
            {
                factory.Resume();Assert(WaitMarker(ready,"ready",10000)=="ready","small pool factory failed");
                using var smallPoolHandle=new Job(Store.PoolName(smallPool),openOnly:true);using var smallItemHandle=new Job(smallItem,openOnly:true);
                Assert(smallPoolHandle.QueryMemory() is{LimitBytes:192*ResourcePolicy.MiB}&&smallItemHandle.QueryMemory() is{LimitBytes:96*ResourcePolicy.MiB},"factory caps missing");
                using var refused=new ProtectedProcess(new(Environment.ProcessPath!,["--allocate-mib","256"],"exe"),cwd,[root,smallPoolHandle,smallItemHandle]);
                refused.Resume();Assert(refused.Process.WaitForExit(20000)&&refused.Process.ExitCode==0,"item cap did not refuse a 256 MiB allocation");
                File.WriteAllText(release,"1");Assert(factory.Process.WaitForExit(5000),"small pool factory did not exit");
            }
            await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:items[1].Id));
            Store.Save(Store.FilePath("configuration.json"),new Configuration());
        });
        await Test("ACC-S3B02 a pool with a direct member keeps its handle and budget after item entries finish",async()=>{
            var settings=new ResourceSettings{Pool=ResourcePolicy.PoolShared,JobMemoryLimit=ResourcePolicy.GiB,PoolMemoryLimit=4*ResourcePolicy.GiB,PlanningBytes=ResourcePolicy.DefaultPlanningBytes};
            var config=new Configuration();config.Draft.Project=cwd;config.Draft.Resource=settings;Store.Save(Store.FilePath("configuration.json"),config);
            var poolId=Guid.NewGuid();var batch=Guid.NewGuid();
            LaunchItem Shared(string name,int position)=>new(Guid.NewGuid(),batch,position,name,cwd,PhysicalDirectory.Workspace(cwd),true,new(Environment.ProcessPath!,["--hold","1"],"exe"),ResourcePolicy.GiB,"agent",PlanningBytes:ResourcePolicy.DefaultPlanningBytes,ResourceMode:"practical",PoolId:poolId,AgentId:"codex");
            var items=new[]{Shared("member-a",1),Shared("member-b",2)};
            var reserved=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),items));Assert(reserved.Ok,reserved.Message);
            using var root=new Job(Store.RootJob,openOnly:true);
            using var attempt=new Mutex(false,Store.AttemptLock(items[0].Id));attempt.WaitOne();
            int itemPid;
            try
            {
                var began=Sync(Ipc.SendAsync(new("begin",Guid.NewGuid(),Attempt:items[0].Id)));Assert(began.Ok,began.Message);
                using var process=LaunchPooled(root,poolId,items[0].Id,["--hold","2000"]);
                itemPid=process.Process.Id;
                var revoked=Sync(Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:items[1].Id)));Assert(revoked.Ok,revoked.Message);
                using var direct=LaunchPooled(root,poolId,null,["--hold","6000"]);
                Assert(process.Process.WaitForExit(10000),"item fixture timed out");
                Store.Save(Store.ReceiptPath(items[0].Id),new Receipt("Finished",itemPid,0,0,"全部受管进程已退出。",DateTimeOffset.UtcNow));
            }
            finally { attempt.ReleaseMutex(); }
            var settled=false;string? holdReason=null;
            for(var i=0;i<60&&!settled;i++)
            {
                var status=await Ipc.SendAsync(new("status",Guid.NewGuid()));settled=status.Entries?.Any(e=>e.Plan.Id==items[0].Id&&e.State=="Settled")==true;holdReason=status.AdmissionBlockedReason;
                if(status.AdmissionBlockedReason?.Contains("资源池")==true)break;
                await Task.Delay(100);
            }
            Assert(settled,"pooled entry never settled");
            Assert(holdReason?.Contains("资源池")==true,"direct pool member did not hold the pool: "+holdReason);
            var released=false;
            for(var i=0;i<150&&!released;i++)
            {
                try{using var probe=new Job(Store.PoolName(poolId),openOnly:true);}catch(System.ComponentModel.Win32Exception e)when(e.NativeErrorCode==2){released=true;}
                if(!released)await Task.Delay(100);
            }
            Assert(released,"pool was not released after its last member exited");
            var final=await Ipc.SendAsync(new("status",Guid.NewGuid()));Assert(final.AdmissionBlockedReason?.Contains("资源池")!=true,"pool hold persisted after release: "+final.AdmissionBlockedReason);
            Store.Save(Store.FilePath("configuration.json"),new Configuration());
            var pid=Store.Read<int>(Store.FilePath("monitor-pid.json"));using(var stale=Process.GetProcessById(pid)){stale.Kill();stale.WaitForExit(5000);}
            var restarted=await Ipc.EnsureMonitorAsync();Assert(restarted.Ok,restarted.Message);
            var after=await Ipc.SendAsync(new("status",Guid.NewGuid()));Assert(after.AdmissionBlockedReason?.Contains("账本")!=true,"retired pool broke the ledger across restart: "+after.AdmissionBlockedReason);
        });
        await Test("ACC-S3B03 custody retains a surviving pool across crash and releases after proven drain",async()=>{
            var settings=new ResourceSettings{Pool=ResourcePolicy.PoolShared,JobMemoryLimit=ResourcePolicy.GiB,PoolMemoryLimit=4*ResourcePolicy.GiB,PlanningBytes=ResourcePolicy.DefaultPlanningBytes};
            var config=new Configuration();config.Draft.Project=cwd;config.Draft.Resource=settings;Store.Save(Store.FilePath("configuration.json"),config);
            var poolId=Guid.NewGuid();var batch=Guid.NewGuid();
            LaunchItem Shared(string name,int position)=>new(Guid.NewGuid(),batch,position,name,cwd,PhysicalDirectory.Workspace(cwd),true,new(Environment.ProcessPath!,["--hold","1"],"exe"),ResourcePolicy.GiB,"agent",PlanningBytes:ResourcePolicy.DefaultPlanningBytes,ResourceMode:"practical",PoolId:poolId,AgentId:"codex");
            var items=new[]{Shared("crash-a",1),Shared("crash-b",2)};
            var reserved=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),items));Assert(reserved.Ok,reserved.Message);
            using var root=new Job(Store.RootJob,openOnly:true);
            using var direct=LaunchPooled(root,poolId,null,["--hold","9000"]);
            using var attempt=new Mutex(false,Store.AttemptLock(items[0].Id));attempt.WaitOne();
            try
            {
                var began=Sync(Ipc.SendAsync(new("begin",Guid.NewGuid(),Attempt:items[0].Id)));Assert(began.Ok,began.Message);
                JobCustody.Retain(items[0]); // Direct child fixture mirrors the runner's pre-spawn handshake.
                using var process=LaunchPooled(root,poolId,items[0].Id,["--hold","2000"]);
                Assert(process.Process.WaitForExit(10000),"item fixture timed out");
                Store.Save(Store.ReceiptPath(items[0].Id),new Receipt("Finished",process.Process.Id,0,0,"全部受管进程已退出。",DateTimeOffset.UtcNow));
            }
            finally { attempt.ReleaseMutex(); }
            var settled=false;for(var i=0;i<60&&!settled;i++){var status=await Ipc.SendAsync(new("status",Guid.NewGuid()));settled=status.Entries?.Any(e=>e.Plan.Id==items[0].Id&&e.State=="Settled")==true;if(!settled)await Task.Delay(100);}
            Assert(settled,"pooled entry never settled before crash");
            var pid=Store.Read<int>(Store.FilePath("monitor-pid.json"));using(var monitor=Process.GetProcessById(pid)){monitor.Kill();monitor.WaitForExit(5000);}
            await Ipc.EnsureMonitorAsync();
            var after=await Ipc.SendAsync(new("status",Guid.NewGuid()));
            using(var retained=new Job(Store.PoolName(poolId),openOnly:true))Assert(retained.Members().Contains(direct.Process.Id),"custody lost the surviving direct member");
            Assert(after.Pools?.Any(p=>p.Id==poolId&&p.MemoryLimit==4*ResourcePolicy.GiB)==true,"surviving pool budget was not retained after monitor crash");
            var ledger=Store.Read<Ledger>(Store.FilePath("control.json"))!;Assert(ledger.Pools.Any(p=>p.Id==poolId),"lost pool record dropped while a direct member may survive");
            await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:items[1].Id));
            var held=await Ipc.SendAsync(new("status",Guid.NewGuid()));
            Assert(held.AdmissionBlockedReason?.Contains("资源池")==true,"orphan direct pool member was not blocked after pending entry revoked");
            Assert(direct.Process.WaitForExit(15000),"direct member timed out");
            var cleared=false;
            for(var i=0;i<100&&!cleared;i++)
            {
                var probe=await Ipc.SendAsync(new("status",Guid.NewGuid()));cleared=probe.AdmissionBlockedReason?.Contains("资源池")!=true;
                var state=Store.Read<Ledger>(Store.FilePath("control.json"));
                if(cleared&&state!=null&&state.Pools.All(p=>p.Id!=poolId))break;
                await Task.Delay(100);
            }
            var current=Store.Read<Ledger>(Store.FilePath("control.json"))!;
            Assert(current.Pools.All(p=>p.Id!=poolId)&&cleared,"custody did not permit proven retirement after direct member drained");
            Store.Save(Store.FilePath("configuration.json"),new Configuration());
        });
        await Test("ACC-S3B04 the real preparation path assigns one shared pool and launches through reserve",async()=>{
            var settings=new ResourceSettings{Pool=ResourcePolicy.PoolShared,JobMemoryLimit=ResourcePolicy.GiB,PoolMemoryLimit=4*ResourcePolicy.GiB,PlanningBytes=ResourcePolicy.DefaultPlanningBytes};
            var config=new Configuration();config.Draft.Project=cwd;config.Draft.Resource=settings;Store.Save(Store.FilePath("configuration.json"),config);
            var slotA=new Slot{Id=Guid.NewGuid(),Enabled=true,AgentId="codex",CustomName="pool-real-a",Directory=cwd,Profiles=new(){{"codex",new AgentProfile{Executable=Environment.ProcessPath!,Arguments=["--hold","2000"]}}}};
            var slotB=new Slot{Id=Guid.NewGuid(),Enabled=true,AgentId="codex",CustomName="pool-real-b",Directory=cwd,Profiles=new(){{"codex",new AgentProfile{Executable=Environment.ProcessPath!,Arguments=["--hold","2000"]}}}};
            var draft=new Draft{SchemaVersion=2,Revision=config.Draft.Revision,Project=cwd,DirectoryMode="shared",Layout="vertical",Resource=settings.Copy(),Slots=[slotA,slotB]};
            var preparer=new WorkspacePreparer(config.WorktreeDirectory);
            var plans=(await Task.Run(()=>preparer.PrepareAsync(draft,_=>{},_=>Task.FromResult(true),CancellationToken.None))).ToArray();
            var batchPool=plans.Length==2?plans[0].PoolId:null;
            Assert(batchPool is {} firstPool&&plans[1].PoolId==firstPool,"prepared batch items do not share one pool id");
            var second=(await Task.Run(()=>preparer.PrepareAsync(draft,_=>{},_=>Task.FromResult(true),CancellationToken.None))).ToArray();
            var nextPool=second.Length==2?second[0].PoolId:null;
            Assert(nextPool is {} secondPool&&second[1].PoolId==secondPool&&secondPool!=batchPool,"re-preparing reused the previous batch pool");
            var preparedPool=batchPool;
            var memory=(await Ipc.SendAsync(new("status",Guid.NewGuid()))).Memory!;
            var policy=ResourcePolicy.SnapshotFor(settings,memory.Total,memory.CommitLimit);
            var reserved=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),plans,Policy:policy,PolicyHash:ResourcePolicy.PolicyHash(policy),PlanDigest:ResourcePolicy.PlanDigest(plans),DraftRevision:config.Draft.Revision));
            Assert(reserved.Ok,reserved.Message);
            var ledger=Store.Read<Ledger>(Store.FilePath("control.json"))!;
            Assert(ledger.Pools.Any(p=>p.Id==preparedPool&&p.MemoryLimit==4*ResourcePolicy.GiB),"prepared pool record missing");
            using var root=new Job(Store.RootJob,openOnly:true);
            using var attempt=new Mutex(false,Store.AttemptLock(plans[0].Id));attempt.WaitOne();
            try
            {
                var began=Sync(Ipc.SendAsync(new("begin",Guid.NewGuid(),Attempt:plans[0].Id)));Assert(began.Ok,began.Message);
                using var process=LaunchPooled(root,preparedPool!.Value,plans[0].Id,["--hold","2000"]);
                Assert(process.Process.WaitForExit(10000),"prepared pool item timed out");
                Store.Save(Store.ReceiptPath(plans[0].Id),new Receipt("Finished",process.Process.Id,0,0,"全部受管进程已退出。",DateTimeOffset.UtcNow));
            }
            finally { attempt.ReleaseMutex(); }
            var settled=false;for(var i=0;i<60&&!settled;i++){var status=await Ipc.SendAsync(new("status",Guid.NewGuid()));settled=status.Entries?.Any(e=>e.Plan.Id==plans[0].Id&&e.State=="Settled")==true;if(!settled)await Task.Delay(100);}
            Assert(settled,"prepared shared item never settled");
            await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:plans[1].Id));
            Store.Save(Store.FilePath("configuration.json"),new Configuration());
        });
        var monitorId=Store.Read<int>(Store.FilePath("monitor-pid.json"));using(var monitor=Process.GetProcessById(monitorId)){monitor.Refresh();evidence.Add(new{name="idle-monitor-memory",privateBytes=monitor.PrivateMemorySize64,workingSet=monitor.WorkingSet64});}
        await Ipc.EnsureMonitorAsync();await WorkspaceReviewChecks.RunAsync(Test,cwd);
        await RuntimeReviewChecks.RunAsync(Test,cwd);
        await ReviewIntegrationChecks.RunAsync(Test,cwd);
        Store.Save(Path.Combine(Store.Root,"integration-results.json"),evidence);
        var failures=evidence.Count(e=>e.GetType().GetProperty("passed")?.GetValue(e) is false);Console.WriteLine($"Integration failures: {failures}; evidence: {Store.Root}");return failures==0?0:1;
    }
}
