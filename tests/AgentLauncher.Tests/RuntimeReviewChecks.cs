using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static class RuntimeReviewChecks
{
    private static MemorySnapshot FreshSample()=>new(16*ResourcePolicy.GiB,12*ResourcePolicy.GiB,32*ResourcePolicy.GiB,24*ResourcePolicy.GiB,5,DateTimeOffset.UtcNow);

    // Regressions are pending review: they require the INTEGRATION_TESTS published Worker (fake sampler + env-root test hooks).
    public static async Task RunAsync(Func<string,Func<Task>,Task> test,string cwd)
    {
        if(!Store.Root.Contains("AgentLauncher-tests-",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Runtime review checks require the integration test build and an isolated AgentLauncher-tests-* root.");
        if(!File.Exists(Store.WorkerPath))throw new FileNotFoundException("Runtime review checks require the published Worker beside the test executable.",Store.WorkerPath);
        Directory.CreateDirectory(cwd);
        await test("CR04 revoke save failure restores state and detail before later begin",()=>RevokeRollbackAsync(cwd));
        await test("CR06 oversized plan is rejected before persistence",()=>OversizedPlanAsync(cwd));
        await test("CR06 ledger stays readable while active reservations survive pruning",()=>LedgerBoundAsync(cwd));
        await test("CR06 unrepresentable active record is withheld instead of failing status",()=>WithheldStatusAsync(cwd));
        await test("CR06 paged status keeps oversized active and history records exact",()=>StatusTransportAsync(cwd));
        await test("CR06 truncated and epoch-mixed continuation frames are rejected",()=>ContinuationRejectionAsync(cwd));
        await test("CR07 pressure exposes an admission reason without changing reconciliation health",()=>PressureReasonAsync(cwd));
        await test("CR10 idle monitor retires within five seconds without polling and keeps pending attempts",()=>IdleRetirementAsync(cwd));
        await test("CR10 a merely busy pipe does not start a second monitor",()=>BusyPipeAsync(cwd));
    }

    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}

    private static LaunchItem Item(string name,string[] arguments,string cwd)
    {
        Directory.CreateDirectory(cwd);
        return new(Guid.NewGuid(),Guid.NewGuid(),1,name,cwd,PhysicalDirectory.Workspace(cwd),false,new(Environment.ProcessPath!,arguments,"exe"),ResourcePolicy.AgentBudget);
    }

    private static async Task RevokeRollbackAsync(string cwd)
    {
        var root=OwnedRoot(cwd);var item=Item("cr04",["--hold","100"],Path.Combine(root,"project"));
        const string pendingDetail="fixture pending authorization";var entry=new ControlEntry{Plan=item,Digest=Store.Digest(item),Detail=pendingDetail};var ledger=new Ledger{Entries=[entry]};var ledgerPath=Path.Combine(root,"control.json");
        using var probe=new MonitorProbe(ledger,FreshSample(),ledgerPath);
        Exception? saveFailure=null;Response? revoke=null;Exception? holderFailure=null;
        using(var gate=new Mutex(false,FileMutexFor(ledgerPath)))
        using(var acquired=new ManualResetEventSlim(false))
        using(var release=new ManualResetEventSlim(false))
        {
            // Named mutexes are reentrant per thread; hold it on another thread so the Worker's 2s acquisition actually fails.
            var holder=new Thread(()=>
            {
                try{if(!gate.WaitOne(0))throw new InvalidOperationException("fixture ledger mutex unavailable");acquired.Set();release.Wait();gate.ReleaseMutex();}
                catch(Exception e){holderFailure=e;acquired.Set();}
            }){IsBackground=true};holder.Start();acquired.Wait();
            try
            {
                try{revoke=(Response)probe.Invoke("Handle",new Request("revoke",Guid.NewGuid(),Attempt:item.Id))!;}
                catch(TargetInvocationException ex)when(ex.InnerException is IOException failure){saveFailure=failure;}
            }
            finally{release.Set();holder.Join();}
        }
        if(holderFailure!=null)throw holderFailure;
        Assert(revoke==null,"revoke returned a response even though the ledger save failed");
        Assert(saveFailure!=null,"failed revoke did not surface the save failure");
        Assert(entry.State=="Pending","failed revoke left state "+entry.State);
        Assert(entry.Detail==pendingDetail,"failed revoke changed the preexisting detail: "+entry.Detail);
        probe.Set("sample",FreshSample());var begin=(Response)probe.Invoke("Handle",new Request("begin",Guid.NewGuid(),Attempt:item.Id))!;
        Assert(begin.Ok,"later begin rejected after rollback: "+begin.Message);
        Assert(entry.State=="Creating","later begin did not consume the restored pending grant");
        await Task.CompletedTask;
    }

    private static async Task OversizedPlanAsync(string cwd)
    {
        var root=OwnedRoot(cwd);var ledgerPath=Path.Combine(root,"control.json");
        using var probe=new MonitorProbe(new Ledger(),FreshSample(),ledgerPath);
        var seed=Item("cr06-oversized",[""],Path.Combine(root,"project"));var request=new Request("reserve",Guid.NewGuid(),[seed]);
        var argumentLength=Ipc.FrameLimit-JsonSerializer.SerializeToUtf8Bytes(request,Store.Json).Length;
        var oversized=seed with{Command=seed.Command with{Arguments=[new string('b',argumentLength)]}};request=request with{Items=[oversized]};
        Assert(JsonSerializer.SerializeToUtf8Bytes(request,Store.Json).Length==Ipc.FrameLimit,"fixture request is not at the accepted wire boundary");var rejected=false;
        try{_=probe.Invoke("Handle",request);}
        catch(TargetInvocationException ex)when(ex.InnerException is IOException){rejected=true;}
        Assert(rejected,"an unrepresentable plan was not rejected before persistence");
        Assert(probe.LedgerState.Entries.Count==0,"rejected plan mutated the in-memory ledger");
        Assert(!File.Exists(ledgerPath),"rejected plan was persisted to disk");
        await Task.CompletedTask;
    }

    private static Task LedgerBoundAsync(string cwd)
    {
        // Terminal history must never grow the published ledger past Store.Read's 8 MiB bound; otherwise the next monitor start
        // reports a broken ledger and refuses every launch until the file is repaired by hand.
        var root=OwnedRoot(cwd);var ledgerPath=Path.Combine(root,"control.json");
        var ledger=new Ledger();var historyCwd=Path.Combine(root,"projects","history");var large=new string('c',60000);
        for(var i=0;i<120;i++)ledger.Entries.Add(new ControlEntry{Plan=Item("cr06-ledger-"+i,[large],historyCwd),State="Settled",Detail="done"});
        var newestTerminal=ledger.Entries[^1].Plan.Id;
        var active=Item("cr06-ledger-active",["--hold","100"],Path.Combine(root,"projects","active"));
        ledger.Entries.Add(new ControlEntry{Plan=active,Digest=Store.Digest(active),Detail="fixture pending authorization"});
        var before=ledger.Entries.Count;
        var rawSize=JsonSerializer.SerializeToUtf8Bytes(ledger,Store.Json).LongLength;
        using var probe=new MonitorProbe(ledger,FreshSample(),ledgerPath);
        probe.Invoke("Save");
        var size=new FileInfo(ledgerPath).Length;
        Assert(rawSize>LedgerPruneTrigger,"fixture never crossed the pruning trigger: "+rawSize);
        Assert(size<LedgerReadCap,"published ledger approached the unreadable bound: "+size);
        var stored=Store.Read<Ledger>(ledgerPath)??throw new Exception("pruned ledger is not readable");
        Assert(stored.Entries.Count<before,"pruning kept every terminal record");
        Assert(stored.Entries.Any(e=>e.Plan.Id==active.Id&&e.State=="Pending"&&e.Plan.Command.Arguments.SequenceEqual(new[]{"--hold","100"})),"pruning dropped or changed the active reservation");
        Assert(stored.Entries.Any(e=>e.Plan.Id==newestTerminal),"pruning dropped the newest terminal record before older ones");
        return Task.CompletedTask;
    }

    private static async Task WithheldStatusAsync(string cwd)
    {
        var root=OwnedRoot(cwd);var ledger=new Ledger();
        var oversized=Item(new string('汉',Ipc.FrameLimit/6),[new string('d',Ipc.FrameLimit)],Path.Combine(root,"projects","big"));
        ledger.Entries.Add(new ControlEntry{Plan=oversized,Digest=Store.Digest(oversized),Detail="fixture pending authorization"});
        var listed=Item("cr06-listed",["--hold","1"],Path.Combine(root,"projects","listed"));
        ledger.Entries.Add(new ControlEntry{Plan=listed,Digest=Store.Digest(listed),Detail="fixture pending authorization"});
        using var probe=new MonitorProbe(ledger,FreshSample(),Path.Combine(root,"control.json"));
        var status=probe.Status();
        Assert(!status.Ok&&!string.IsNullOrEmpty(status.AdmissionBlockedReason),"an unrepresentable active record did not close the gate");
        Assert(status.Message.Contains("超出单帧边界")&&status.Message.Contains(oversized.Id.ToString("N")),"withheld record ID was not reported: "+status.Message);
        Assert(status.Entries!.Any(e=>e.Plan.Id==listed.Id&&e.Plan.Command.Arguments.SequenceEqual(listed.Command.Arguments)),"representable active record was hidden or modified");
        Assert(!status.Entries!.Any(e=>e.Plan.Id==oversized.Id),"withheld record was still returned");
        using var wire=new MemoryStream();await Ipc.WriteResponseAsync(wire,status,CancellationToken.None);wire.Position=0;
        var received=await Ipc.ReadResponseAsync(wire,CancellationToken.None);
        Assert(!received.Ok&&received.Entries!.Single().Plan.Id==listed.Id,"bounded failure status did not survive wire transport");
        foreach(var request in new[]{new Request("reserve",Guid.NewGuid(),[Item("cr06-denied",[],Path.Combine(root,"projects","denied"))]),new Request("begin",Guid.NewGuid(),Attempt:listed.Id)})
        {
            var denied=false;
            try{probe.Invoke("Handle",request);}catch(TargetInvocationException e)when(e.InnerException is IOException failure&&failure.Message.Contains("超出单帧边界")){denied=true;}
            Assert(denied,"incomplete status allowed "+request.Operation);
        }
        var revoke=(Response)probe.Invoke("Handle",new Request("revoke",Guid.NewGuid(),Attempt:listed.Id))!;
        Assert(revoke.Ok&&revoke.Entries!.Single().State=="Revoked","oversized peer prevented revoking a representable pending entry");
        Assert(ledger.Entries.Single(e=>e.Plan.Id==oversized.Id).Plan.Command.Arguments.SequenceEqual(oversized.Command.Arguments),"withholding modified the original ledger");
    }

    private static async Task BusyPipeAsync(string cwd)
    {
        // A single-instance pipe that is merely busy must not be read as "no monitor"; otherwise every contended reserve spawns a Worker
        // that exits immediately. The spawn log is an INTEGRATION_TESTS-only hook, so only a real Process.Start can add a line.
        var log=Path.Combine(OwnedRoot(cwd),"monitor-spawns.log");var previous=Environment.GetEnvironmentVariable("AGENT_LAUNCHER_MONITOR_SPAWN_LOG");
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_MONITOR_SPAWN_LOG",log);
        try
        {
            await Ipc.EnsureMonitorAsync();
            File.Delete(log);
            using var holder=new NamedPipeClientStream(".",Store.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await holder.ConnectAsync(timeout.Token);
            var poll=Ipc.EnsureMonitorAsync();
            var item=Item("cr10-busy",["--hold","1"],Path.Combine(cwd,"projects","busy"));
            var reserve=Ipc.ReserveWithCleanupAsync([item]);
            await Task.Delay(1200);
            Assert(!File.Exists(log),"a second monitor was started during pipe contention");
            holder.Dispose();
            var response=await reserve;
            Assert((await poll).Ok,"status through a busy pipe failed");
            Assert(response.Ok,"reserve through a busy pipe failed: "+response.Message);
            Assert(!File.Exists(log),"a second monitor was started while the pipe was merely busy");
            await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:item.Id));
        }
        finally{Environment.SetEnvironmentVariable("AGENT_LAUNCHER_MONITOR_SPAWN_LOG",previous);}
    }

    private static async Task StatusTransportAsync(string cwd)
    {
        using var monitor=await OwnedMonitor.StartAsync(cwd);
        var historyArgs=new[]{"--hold","1",new string('a',28000)};var history=new List<Guid>();var historyCwd=Path.Combine(monitor.Root,"projects","history");
        for(var i=0;i<40;i++)
        {
            var item=Item("cr06-history-"+i,historyArgs,historyCwd);
            var reserved=await monitor.SendAsync(new("reserve",Guid.NewGuid(),[item]));
            Assert(reserved.Ok,"history reserve failed at "+i+": "+reserved.Message);
            var revoked=await monitor.SendAsync(new("revoke",Guid.NewGuid(),Attempt:item.Id));
            Assert(revoked.Ok,"history revoke failed at "+i+": "+revoked.Message);
            history.Add(item.Id);
        }
        var activeArgs=new[]{"--hold","100",new string('汉',28000)};
        var first=Item("cr06-active-a",activeArgs,Path.Combine(monitor.Root,"projects","active-a"));
        var second=Item("cr06-active-b",activeArgs,Path.Combine(monitor.Root,"projects","active-b"));
        try
        {
            var reserved=await monitor.SendAsync(new("reserve",Guid.NewGuid(),[first,second]));
            Assert(reserved.Ok,"large active reserve failed: "+reserved.Message);
            var status=await monitor.SendAsync(new("status",Guid.NewGuid()));
            Assert(status.Ok,"paged status failed: "+status.Message);
            Assert(JsonSerializer.SerializeToUtf8Bytes(status,Store.Json).Length>1024*1024,"fixture stayed within one IPC frame; paged transport was not exercised");
            foreach(var expected in new[]{first,second})
            {
                var actual=status.Entries?.SingleOrDefault(e=>e.Plan.Id==expected.Id)??throw new Exception("paged status dropped active entry "+expected.Id);
                Assert(actual.State=="Pending","paged status changed an active state: "+actual.State);
                Assert(actual.Plan.BatchId==expected.BatchId&&actual.Plan.Command.Arguments.SequenceEqual(expected.Command.Arguments),"paged status changed active arguments or batch identity");
            }
            var terminal=status.Entries!.Where(Store.IsTerminal).ToArray();
            Assert(terminal.Length>0,"paged status dropped all bounded terminal history");
            foreach(var actual in terminal)Assert(history.Contains(actual.Plan.Id)&&actual.Plan.Command.Arguments.SequenceEqual(historyArgs),"paged status corrupted a terminal record");
            Assert(status.AdmissionBlockedReason==null,"healthy owned monitor reported an admission blocker: "+(status.AdmissionBlockedReason??"<null>"));
        }
        finally
        {
            foreach(var item in new[]{first,second}){try{await monitor.SendAsync(new("revoke",Guid.NewGuid(),Attempt:item.Id),TimeSpan.FromSeconds(5));}catch(IOException){}catch(TimeoutException){}}
        }
    }

    private static async Task ContinuationRejectionAsync(string cwd)
    {
        var root=OwnedRoot(cwd);var item=Item("cr06-frame",["--hold","1"],Path.Combine(root,"project"));var entry=new ControlEntry{Plan=item,Digest=Store.Digest(item)};
        await AssertContinuationRejectedAsync(entry,truncated:true);
        await AssertContinuationRejectedAsync(entry,truncated:false);
    }

    private static async Task AssertContinuationRejectedAsync(ControlEntry entry,bool truncated)
    {
        var name="AgentLauncher-review-"+Guid.NewGuid().ToString("N");using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server=Task.Run(async()=>
        {
            await using var stream=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await stream.WaitForConnectionAsync(cts.Token);
            await Ipc.WriteAsync(stream,new Response(true,"page",[entry],null,"epoch-a",null,true),cts.Token);
            if(!truncated)await Ipc.WriteAsync(stream,new Response(true,"page",[entry],null,"epoch-b",null,false),cts.Token);
        });
        await using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(cts.Token);
        var rejected=false;
        try{_=await Ipc.ReadResponseAsync(client,cts.Token);}catch(IOException){rejected=true;}
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(rejected,truncated?"truncated continuation returned a partial response":"epoch-mixed continuation was accepted");
    }

    private static Task PressureReasonAsync(string cwd)
    {
        var root=OwnedRoot(cwd);using var probe=new MonitorProbe(new Ledger(),FreshSample(),Path.Combine(root,"control.json"));
        probe.Set("pressure",true);
        var pressured=probe.Status();
        Assert(pressured.Ok,"resource pressure changed reconciliation health");
        Assert(!string.IsNullOrEmpty(pressured.AdmissionBlockedReason)&&pressured.AdmissionBlockedReason!.Contains("压力"),"pressure admission blocker not exposed: "+(pressured.AdmissionBlockedReason??"<null>"));
        probe.Set("pressure",false);
        var normal=probe.Status();
        Assert(normal.Ok&&normal.AdmissionBlockedReason==null,"cleared pressure still reports an admission blocker: "+(normal.AdmissionBlockedReason??"<null>"));
        return Task.CompletedTask;
    }

    private static async Task IdleRetirementAsync(string cwd)
    {
        using var monitor=await OwnedMonitor.StartAsync(cwd);
        for(var i=0;i<7;i++)
        {
            await Task.Delay(700);
            var polled=await monitor.SendAsync(new("status",Guid.NewGuid()));
            Assert(polled.Ok,"poll during the idle window failed: "+polled.Message);
        }
        var pending=Item("cr10-pending",["--hold","100"],Path.Combine(monitor.Root,"projects","pending"));
        var reserved=await monitor.SendAsync(new("reserve",Guid.NewGuid(),[pending]));
        Assert(reserved.Ok,"pending reserve failed: "+reserved.Message);
        await Task.Delay(5200);
        var status=await monitor.SendAsync(new("status",Guid.NewGuid()));
        Assert(status.Ok,"an active pending attempt did not keep the monitor alive: "+status.Message);
        Assert(status.Entries!.Single(e=>e.Plan.Id==pending.Id).State=="Pending","pending attempt changed before revocation");
        var revokeWatch=Stopwatch.StartNew();
        var revoke=await monitor.SendAsync(new("revoke",Guid.NewGuid(),Attempt:pending.Id));
        Assert(revoke.Ok,"final revoke failed: "+revoke.Message);
        var exited=monitor.WaitForExit(TimeSpan.FromSeconds(5));revokeWatch.Stop();
        Assert(exited,"owned monitor did not exit within five seconds after the final revoke");
        Assert(revokeWatch.Elapsed<=TimeSpan.FromSeconds(5),"owned monitor exit took "+revokeWatch.Elapsed.TotalSeconds.ToString("F2")+"s");
    }

    private const long LedgerPruneTrigger=6L*1024*1024;
    private const long LedgerReadCap=8L*1024*1024;
    private static string OwnedRoot(string cwd){var root=Path.GetFullPath(Path.Combine(cwd,"AgentLauncher-tests-runtime-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);return root;}

    private static string FileMutexFor(string path)=>"Local\\AgentLauncher-file-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));

    private sealed class MonitorProbe : IDisposable
    {
        private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
        private readonly Type type;private readonly object instance;
        public Ledger LedgerState { get; }
        public MonitorProbe(Ledger ledger,MemorySnapshot sample,string ledgerPath)
        {
            LedgerState=ledger;
            var assembly=Assembly.LoadFrom(Store.WorkerPath.Replace(".exe",".dll",StringComparison.OrdinalIgnoreCase));
            type=assembly.GetType("AgentLauncher.Worker.Monitor",true)!;
            instance=Activator.CreateInstance(type,true)!;
            Set("ledger",ledger);Set("ledgerPath",ledgerPath);Set("sample",sample);
        }
        public void Set(string name,object? value)=>Field(name).SetValue(instance,value);
        public object? Invoke(string name,params object[] arguments)=>Method(name).Invoke(instance,arguments);
        public Response Status()=>(Response)Invoke("Status")!;
        private FieldInfo Field(string name)=>type.GetField(name,Flags)??throw new MissingFieldException(type.FullName??type.Name,name);
        private MethodInfo Method(string name)=>type.GetMethod(name,Flags)??throw new MissingMethodException(type.FullName??type.Name,name);
        public void Dispose()=>((IDisposable)instance).Dispose();
    }

    private sealed class OwnedMonitor : IDisposable
    {
        public string Root { get; }
        private readonly string pipe;private readonly Process process;private readonly DateTime started;
        private OwnedMonitor(string root,string pipe,Process process){Root=root;this.pipe=pipe;this.process=process;started=process.StartTime;}
        public static async Task<OwnedMonitor> StartAsync(string cwd)
        {
            var root=OwnedRoot(cwd);Directory.CreateDirectory(root);
            var start=new ProcessStartInfo(Store.WorkerPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add("monitor");start.Environment["AGENT_LAUNCHER_TEST_ROOT"]=root;start.Environment["AGENT_LAUNCHER_FAKE_SAMPLE"]="1";
            var process=Process.Start(start)??throw new IOException("无法启动隔离监控器。");process.StandardInput.Close();
            var monitor=new OwnedMonitor(root,PipeFor(root),process);
            try{await monitor.WaitReadyAsync();return monitor;}
            catch{monitor.Dispose();throw;}
        }
        private async Task WaitReadyAsync()
        {
            var deadline=DateTime.UtcNow.AddSeconds(15);
            while(DateTime.UtcNow<deadline)
            {
                try{var status=await SendAsync(new("status",Guid.NewGuid()),TimeSpan.FromMilliseconds(800));if(status.Ok)return;}
                catch(Exception e)when(e is IOException or TimeoutException or OperationCanceledException){}
                if(process.HasExited)throw new IOException("隔离监控器提前退出："+process.StandardError.ReadToEnd().Trim());
                await Task.Delay(100);
            }
            throw new TimeoutException("隔离监控器未在期限内就绪。");
        }
        public async Task<Response> SendAsync(Request request,TimeSpan? timeout=null)
        {
            using var cts=new CancellationTokenSource(timeout??TimeSpan.FromSeconds(8));
            await using var client=new NamedPipeClientStream(".",pipe,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(cts.Token);await Ipc.WriteAsync(client,request,cts.Token);
            return await Ipc.ReadResponseAsync(client,cts.Token);
        }
        public bool WaitForExit(TimeSpan timeout)=>process.WaitForExit((int)timeout.TotalMilliseconds);
        public void Dispose()
        {
            try{if(!process.HasExited&&!process.WaitForExit(5000)&&Store.Read<int>(Path.Combine(Root,"monitor-pid.json"))==process.Id&&process.StartTime==started){process.Kill();process.WaitForExit(3000);}}finally{process.Dispose();}
        }
        private static string PipeFor(string root)=>"AgentLauncher-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value+root)))[..20];
    }
}
