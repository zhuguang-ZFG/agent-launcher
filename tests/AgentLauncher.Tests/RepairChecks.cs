using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static class RepairChecks
{
    public static int Run()
    {
        if(!(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT")??"").Contains("AgentLauncher-tests-"))throw new InvalidOperationException("Repair checks require an isolated root.");
        Directory.CreateDirectory(Store.Root);var cwd=Path.Combine(Store.Root,"repair-cwd");Directory.CreateDirectory(cwd);
        var type=Assembly.LoadFrom(Store.WorkerPath.Replace(".exe",".dll",StringComparison.OrdinalIgnoreCase)).GetType("AgentLauncher.Worker.Monitor",true)!;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object? Call(object monitor,string name,params object?[] args)
        {
            try{return (type.GetMethod(name,flags)??throw new MissingMethodException(name)).Invoke(monitor,args);}
            catch(TargetInvocationException e)when(e.InnerException!=null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
        }
        void Set(object monitor,string field,object value)=>type.GetField(field,flags)!.SetValue(monitor,value);
        T Get<T>(object monitor,string field)=>(T)type.GetField(field,flags)!.GetValue(monitor)!;
        var failures=0;var results=new List<object>();
        void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
        void Test(string name,Action action){try{action();Console.WriteLine("PASS "+name);results.Add(new{name,passed=true});}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e.Message);results.Add(new{name,passed=false,error=e.ToString()});}}
        LaunchItem Item(string name="repair")=>new(Guid.NewGuid(),Guid.NewGuid(),1,name,cwd,PhysicalDirectory.Workspace(cwd),true,new(Environment.ProcessPath!,["--hold","1"],"exe"),ResourcePolicy.AgentBudget,SourceSlotId:Guid.NewGuid());
        IDisposable Monitor(Ledger? book=null)
        {
            var monitor=(IDisposable)Activator.CreateInstance(type,true)!;
            Set(monitor,"sample",new MemorySnapshot(16*ResourcePolicy.GiB,12*ResourcePolicy.GiB,32*ResourcePolicy.GiB,24*ResourcePolicy.GiB,5,DateTimeOffset.UtcNow));
            Set(monitor,"root",new Job(Store.RootJob+"-fixture-"+Guid.NewGuid().ToString("N"),root:true));Set(monitor,"rootCpuRate",3500);Set(monitor,"ledger",book??new Ledger());
            Set(monitor,"ledgerPath",Store.FilePath("repair-"+Guid.NewGuid().ToString("N")+".json"));return monitor;
        }
        Test("R11 missing ledger keeps durable queue claims blocked",()=>
        {
            var item=Item();var claim=new TaskReservationClaim(Guid.NewGuid(),item.Id);var book=new Ledger{BootId=Store.BootId(),Entries=[new(){Plan=item,Digest=Store.Digest(item)}],TaskClaims=[claim]};
            using var monitor=Monitor(book);Call(monitor,"Save");Call(monitor,"Save");var path=Get<string>(monitor,"ledgerPath");File.Delete(path);
            var backup=File.ReadAllBytes(path+".bak");using var restarted=Monitor();Set(restarted,"ledgerPath",path);Call(restarted,"LoadLedger");
            Assert(Get<bool>(restarted,"ledgerBroken"),"lost book treated as a fresh installation");
            Assert(((string?)Call(restarted,"AdmissionBlockedReason"))?.Contains("备份")==true,"missing recovery reason");
            Assert(!File.Exists(path)&&backup.SequenceEqual(File.ReadAllBytes(path+".bak")),"recovery overwrote retained claims");
        });
        Test("R11 missing configuration blocks status reserve waiver with one reason",()=>
        {
            using var monitor=Monitor();var path=Store.FilePath("authority-"+Guid.NewGuid().ToString("N")+".json");Set(monitor,"configurationPath",path);
            Store.Save(path,new Configuration());Store.Save(path,new Configuration());File.Delete(path);
            var reason=(string?)Call(monitor,"AdmissionBlockedReason");Assert(reason?.Contains("备份")==true,"missing authority fell back to defaults");
            foreach(var op in new[]{"reserve","waiver"})
            {
                string? message=null;try{Call(monitor,"Handle",new Request(op,Guid.NewGuid(),[Item()]));}catch(IOException e){message=e.Message;}
                Assert(message==reason,op+" used a different block reason: "+message);
            }
            var status=(Response)Call(monitor,"Handle",new Request("status",Guid.NewGuid()))!;
            Assert(!status.Ok&&status.AdmissionBlockedReason==reason,"status did not fail closed consistently");
        });
        Test("R11 missing configuration also blocks previously reserved begin and resume",()=>
        {
            foreach(var op in new[]{"begin","resume"})
            {
                var item=Item();var entry=new ControlEntry{Plan=item,Digest=Store.Digest(item),State=op=="begin"?"Pending":"Creating"};
                using var monitor=Monitor(new Ledger{Entries=[entry]});entry.Epoch=Get<string>(monitor,"epoch");
                var path=Store.FilePath("activation-authority-"+Guid.NewGuid().ToString("N")+".json");Set(monitor,"configurationPath",path);
                Store.Save(path,new Configuration());Store.Save(path,new Configuration());File.Delete(path);
                var reason=(string?)Call(monitor,"AdmissionBlockedReason");string? refusal=null;
                try{Call(monitor,"Handle",new Request(op,Guid.NewGuid(),Attempt:item.Id,Epoch:entry.Epoch,Digest:entry.Digest));}catch(IOException error){refusal=error.Message;}
                Assert(reason?.Contains("备份")==true&&refusal==reason,op+" activated an old grant after authority disappeared: "+refusal);
            }
        });
        Test("R12 reservation capacity keeps growth room and leaves whole batch unchanged",()=>
        {
            var book=new Ledger();for(var i=0;i<10;i++){var item=Item(new string('x',480000));book.Entries.Add(new(){Plan=item,Digest=Store.Digest(item)});}
            using var monitor=Monitor(book);var before=JsonSerializer.Serialize(book,Store.Json);var candidate=Item(new string('y',480000));
            var entries=new[]{new ControlEntry{Plan=candidate,Digest=Store.Digest(candidate)}};
            var refused=false;try{Call(monitor,"EnsureReservationCapacity",entries,Array.Empty<PoolRecord>(),null);}catch(IOException){refused=true;}
            Assert(refused,"accepted a batch without lifecycle growth room");Assert(JsonSerializer.Serialize(book,Store.Json)==before,"capacity check mutated active records or claims");
            using var empty=Monitor();Call(empty,"EnsureReservationCapacity",new[]{new ControlEntry{Plan=Item()}},Array.Empty<PoolRecord>(),null);
        });
        Test("R13 Refresh pruning releases job ownership without losing durable attempt result",()=>
        {
            var item=Item();var entry=new ControlEntry{Plan=item,Digest=Store.Digest(item),State="Started"};var book=new Ledger{BootId=Store.BootId(),Entries=[entry]};
            for(var i=0;i<15;i++){var old=Item(new string('x',450000));book.Entries.Add(new(){Plan=old,Digest=Store.Digest(old),State="Settled"});}
            using var monitor=Monitor(book);using var root=new Job(Store.RootJob+"-repair-"+Guid.NewGuid().ToString("N"),root:true);Set(monitor,"root",new Job(Store.RootJob+"-repair-"+Guid.NewGuid().ToString("N"),root:true));
            var job=new Job(Store.JobName(item.Id),ResourcePolicy.AgentBudget);Get<Dictionary<Guid,Job>>(monitor,"jobs").Add(item.Id,job);
            Store.Save(Store.ReceiptPath(item.Id),new Receipt("Finished",0,0,0,"finished",DateTimeOffset.UtcNow));Call(monitor,"Refresh",false);
            Assert(!Get<Dictionary<Guid,Job>>(monitor,"jobs").ContainsKey(item.Id),"terminal job still owned");
            Assert(book.Entries.All(e=>e.Plan.Id!=item.Id),"fixture did not prune newly ended item");
            var reply=(Response)Call(monitor,"Handle",new Request("attempt",Guid.NewGuid(),Attempt:item.Id,Digest:entry.Digest))!;
            Assert(reply.Ok&&reply.Entries is [var ended]&&ended.Plan.Id==item.Id&&ended.State=="Settled","pruned result unavailable by attempt identity");
            using var restarted=Monitor(Store.Read<Ledger>(Get<string>(monitor,"ledgerPath"))!);
            var durable=(Response)Call(restarted,"Handle",new Request("attempt",Guid.NewGuid(),Attempt:item.Id,Digest:entry.Digest))!;
            Assert(durable.Ok&&durable.Entries is [var restored]&&restored.State=="Settled","terminal archive did not survive monitor reconstruction");
            var bad=(Response)Call(monitor,"Handle",new Request("attempt",Guid.NewGuid(),Attempt:item.Id,Digest:"wrong"))!;Assert(!bad.Ok,"wrong digest accepted");
            var unknown=(Response)Call(monitor,"Handle",new Request("attempt",Guid.NewGuid(),Attempt:Guid.NewGuid()))!;Assert(!unknown.Ok&&unknown.Entries is null,"missing attempt invented completion");
        });
        Test("R07 exact job allocation notice identifies offender and preserves sibling",()=>
        {
            var watchType=typeof(Job).Assembly.GetType("AgentLauncher.Windows.JobMemoryWatch")??throw new MissingMemberException("JobMemoryWatch");
            using var root=new Job(Store.RootJob+"-notice-"+Guid.NewGuid().ToString("N"),root:true);using var offender=new Job(Store.JobName(Guid.NewGuid()),96*ResourcePolicy.MiB);using var siblingJob=new Job(Store.JobName(Guid.NewGuid()),96*ResourcePolicy.MiB);
            using var watch=(IDisposable)Activator.CreateInstance(watchType,[offender])!;
            using var sibling=new ProtectedProcess(new(Environment.ProcessPath!,["--hold","10000"],"exe"),cwd,root,siblingJob);
            using var process=new ProtectedProcess(new(Environment.ProcessPath!,["--allocate-commit","256"],"exe"),cwd,root,offender);
            try
            {
                sibling.Resume();process.Resume();Assert(process.Process.WaitForExit(6000)&&process.Process.ExitCode==37,"capped allocation fixture failed");
                var pids=new List<int>();for(var i=0;i<20&&pids.Count==0;i++){pids.AddRange((int[])watchType.GetMethod("Drain")!.Invoke(watch,null)!);if(pids.Count==0)Thread.Sleep(50);}
                Assert(pids.Contains(process.Process.Id),"kernel allocation refusal was not attributed to offender");Assert(!sibling.Process.HasExited,"offender cap killed sibling");Assert(offender.QueryMemory()?.LimitBytes==96*ResourcePolicy.MiB,"cap changed");
            }
            finally{if(!sibling.Process.HasExited){sibling.Process.Kill();sibling.Process.WaitForExit(5000);}}
        });
        Test("R11 terminal archive rejects swapped identity and never reads backup receipt",()=>
        {
            using var monitor=Monitor();var item=Item();var other=Item();var entry=new ControlEntry{Plan=other,Digest=Store.Digest(other),State="Settled"};
            var path=Path.Combine(Store.Root,"attempts",item.Id.ToString("N"),"outcome.json");
            Store.Save(path,new ArchivedAttempt(1,item.Id,entry.Digest,entry));
            var refused=(Response)Call(monitor,"Handle",new Request("attempt",Guid.NewGuid(),Attempt:item.Id))!;Assert(!refused.Ok,"foreign result accepted");
            File.Delete(path);Store.Save(Store.ReceiptPath(item.Id),new Receipt("Finished",0,0,0,"foreign receipt",DateTimeOffset.UtcNow));
            Store.Save(Get<string>(monitor,"ledgerPath")+".bak",new Ledger{Entries=[new(){Plan=item,Digest=Store.Digest(item),State="Settled"}]});
            var missing=(Response)Call(monitor,"Handle",new Request("attempt",Guid.NewGuid(),Attempt:item.Id))!;Assert(!missing.Ok&&missing.Entries is null,"old backup or unbound receipt trusted");
        });
        Test("ACC-L01 task result checks attempt batch task and workspace identity",()=>
        {
            var matcher=typeof(TaskRun).Assembly.GetType("AgentLauncher.Core.AttemptIdentity")??throw new MissingMemberException("AttemptIdentity");
            var item=Item();var entry=new ControlEntry{Plan=item,Digest=Store.Digest(item),State="Settled"};
            var run=new TaskRun{AttemptId=item.Id,BatchId=item.BatchId,TaskId=item.SourceSlotId!.Value,WorkspaceKey=item.Workspace.Key};
            bool Match(ControlEntry candidate)=>(bool)matcher.GetMethod("Matches")!.Invoke(null,[run,candidate])!;
            Assert(Match(entry),"matching identity rejected");
            foreach(var wrong in new[]{item with{Id=Guid.NewGuid()},item with{BatchId=Guid.NewGuid()},item with{SourceSlotId=Guid.NewGuid()},item with{Workspace=item.Workspace with{Key="other"}}})
                Assert(!Match(new(){Plan=wrong,Digest=Store.Digest(wrong),State="Settled"}),"unrelated outcome accepted");
        });
        Test("R07 runner receipt retains exact native refusal without raising cap",()=>
        {
            var config=new Configuration();config.Draft.Resource.JobMemoryLimit=ResourcePolicy.GiB;config.Draft.Resource.PlanningBytes=ResourcePolicy.MinPlanningBytes;Store.Save(Store.FilePath("configuration.json"),config);
            var healthy=Ipc.EnsureMonitorAsync().GetAwaiter().GetResult();Assert(healthy.Ok,healthy.Message);
            var item=Item() with{Budget=ResourcePolicy.GiB,PlanningBytes=ResourcePolicy.MinPlanningBytes,Command=new(Environment.ProcessPath!,["--allocate-commit","2048"],"exe")};
            var reserved=Ipc.SendAsync(new("reserve",Guid.NewGuid(),[item])).GetAwaiter().GetResult();Assert(reserved.Ok,reserved.Message);
            var start=new ProcessStartInfo(Store.WorkerPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("run");start.ArgumentList.Add(item.Id.ToString());
            using var runner=Process.Start(start)!;runner.StandardInput.Close();Assert(runner.WaitForExit(10000),"runner timeout");Assert(runner.ExitCode==37,"unexpected runner result: "+runner.StandardError.ReadToEnd());
            using var receipt=JsonDocument.Parse(File.ReadAllText(Store.ReceiptPath(item.Id)));var memory=receipt.RootElement.GetProperty("JobMemory");
            Assert(memory.TryGetProperty("LimitReached",out var hit)&&hit.GetBoolean(),"receipt lost exact kernel refusal");
            Assert(memory.GetProperty("LimitBytes").GetUInt64()==ResourcePolicy.GiB,"runner raised cap");
            Assert(receipt.RootElement.GetProperty("Detail").GetString()!.Contains("内核"),"no actionable cause on receipt");
            Store.Save(Store.FilePath("configuration.json"),new Configuration());
        });
        Test("R07 undersized shared pool requires explicit admission warning",()=>
        {
            var settings=new ResourceSettings{Pool=ResourcePolicy.PoolShared,PoolMemoryLimit=2*ResourcePolicy.GiB,PlanningBytes=ResourcePolicy.GiB};
            var sample=new MemorySnapshot(16*ResourcePolicy.GiB,12*ResourcePolicy.GiB,32*ResourcePolicy.GiB,24*ResourcePolicy.GiB,5,DateTimeOffset.UtcNow);var policy=ResourcePolicy.SnapshotFor(settings,sample.Total,sample.CommitLimit);var pool=Guid.NewGuid();
            var items=Enumerable.Range(0,3).Select(_=>new BudgetItem(Guid.NewGuid(),"agent",ResourcePolicy.GiB,2*ResourcePolicy.GiB,pool,2*ResourcePolicy.GiB)).ToArray();
            var decision=ResourcePolicy.Evaluate(sample,policy,[],items);Assert(decision.Kind==AdmissionKind.RequireWaiver&&decision.Reason.Contains("共享池"),"pool undersizing silently admitted");
            Assert(ResourcePolicy.Evaluate(sample,policy,items,[],false).Kind==AdmissionKind.Allow,"begin re-consumed consent");
        });
        Test("R04 failed pane keeps an error receipt and deliberate dismissal key",()=>
        {
            var policy=typeof(TaskRun).Assembly.GetType("AgentLauncher.Core.PaneExitPolicy")??throw new MissingMemberException("PaneExitPolicy");
            var key=policy.GetMethod("DismissalKey")!;Assert((ConsoleKey)key.Invoke(null,[37,false])! == ConsoleKey.F12,"failure can disappear on buffered Enter");
            Assert((ConsoleKey)key.Invoke(null,[0,true])! == ConsoleKey.F12,"allocation refusal with exit zero can disappear");
            var id=Guid.NewGuid();var start=new ProcessStartInfo(Store.WorkerPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("pane");start.ArgumentList.Add(id.ToString());
            using var process=Process.Start(start)!;process.StandardInput.Close();Assert(process.WaitForExit(10000)&&process.ExitCode!=0,"missing-grant pane did not fail");
            var receipt=Store.Read<Receipt>(Store.ReceiptPath(id));Assert(receipt?.Phase=="FailedBeforeStart"&&receipt.Detail.Contains("不存在"),"early pane failure lost cause");
        });
        Test("R07 monitor retains shared-pool kernel event with offender PID",()=>
        {
            using var monitor=Monitor();var poolId=Guid.NewGuid();using var pool=new Job(Store.PoolName(poolId),96*ResourcePolicy.MiB);Call(monitor,"WatchPool",poolId,pool);
            using var process=new ProtectedProcess(new(Environment.ProcessPath!,["--allocate-commit","256"],"exe"),cwd,[pool]);process.Resume();Assert(process.Process.WaitForExit(6000)&&process.Process.ExitCode==37,"pool refusal fixture failed");
            var path=Store.FilePath(Path.Combine("pools",poolId.ToString("N")+".limit.json"));
            for(var i=0;i<20&&!File.Exists(path);i++){Call(monitor,"DrainPoolLimits");if(!File.Exists(path))Thread.Sleep(50);}
            using var notice=JsonDocument.Parse(File.ReadAllText(path));Assert(notice.RootElement.GetProperty("PoolId").GetGuid()==poolId&&notice.RootElement.GetProperty("Pid").GetInt32()==process.Process.Id,"pool event lost scope or offender identity");
        });
        Test("R12 real reserve rejects capacity before creating any pool or authority",()=>
        {
            var poolId=Guid.NewGuid();var settings=new ResourceSettings{Pool=ResourcePolicy.PoolShared,PoolMemoryLimit=8*ResourcePolicy.GiB,JobMemoryLimit=ResourcePolicy.GiB,PlanningBytes=ResourcePolicy.MinPlanningBytes};
            var config=new Configuration();config.Draft.Resource=settings;Store.Save(Store.FilePath("configuration.json"),config);
            var book=new Ledger{Pools=[new(){Id=poolId,BatchId=Guid.NewGuid(),MemoryLimit=8*ResourcePolicy.GiB,PolicyHash=ResourcePolicy.PolicyHash(ResourcePolicy.SnapshotFor(settings,256*ResourcePolicy.GiB,512*ResourcePolicy.GiB))}]};
            for(var i=0;i<10;i++){var plan=Item(new string('x',480000)) with{PoolId=poolId,Budget=ResourcePolicy.GiB,PlanningBytes=ResourcePolicy.MinPlanningBytes};book.Entries.Add(new(){Plan=plan,Digest=Store.Digest(plan)});}
            using var monitor=Monitor(book);Set(monitor,"sample",new MemorySnapshot(256*ResourcePolicy.GiB,192*ResourcePolicy.GiB,512*ResourcePolicy.GiB,384*ResourcePolicy.GiB,5,DateTimeOffset.UtcNow));
            var freshPool=poolId;var planNew=Item(new string('y',480000)) with{PoolId=freshPool,Budget=ResourcePolicy.GiB,PlanningBytes=ResourcePolicy.MinPlanningBytes};
            var before=JsonSerializer.Serialize(book,Store.Json);var denied=false;try{Call(monitor,"Handle",new Request("reserve",Guid.NewGuid(),[planNew]));}catch(IOException e){denied=e.Message.Contains("容量");}
            Assert(denied&&JsonSerializer.Serialize(book,Store.Json)==before,"overfull batch changed retained authority");Assert(Get<Dictionary<Guid,Job>>(monitor,"pools").Count==0,"capacity denial created a pool");
            Store.Save(Store.FilePath("configuration.json"),new Configuration());
        });
        Test("R12 pooled publish failure rolls back observer ownership as well as ledger",()=>
        {
            var config=new Configuration();config.Draft.Resource.Pool=ResourcePolicy.PoolShared;Store.Save(Store.FilePath("configuration.json"),config);
            using var monitor=Monitor();var path=Get<string>(monitor,"ledgerPath");Store.Save(path,new Ledger());
            var item=Item() with{PoolId=Guid.NewGuid()};var denied=false;
            using(var reader=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                try{Call(monitor,"Handle",new Request("reserve",Guid.NewGuid(),[item]));}catch(IOException e){denied=!e.Message.Contains("超时");}
            var book=Get<Ledger>(monitor,"ledger");Assert(denied&&book.Entries.Count==0&&book.Pools.Count==0,"failed pool publication left authority behind");
            Assert(Get<Dictionary<Guid,JobMemoryWatch>>(monitor,"poolWatches").Count==0,"failed pool publication leaked an observer");
            Store.Save(Store.FilePath("configuration.json"),new Configuration());
        });
        Test("F08 duplicate pane never overwrites the original attempt receipt",()=>
        {
            var id=Guid.NewGuid();var path=Store.ReceiptPath(id);Store.Save(path,new Receipt("Started",123,456,null,"original owner",DateTimeOffset.UtcNow));File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!,"claim"),"1");var before=File.ReadAllBytes(path);
            var start=new ProcessStartInfo(Store.WorkerPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("pane");start.ArgumentList.Add(id.ToString());
            using var process=Process.Start(start)!;process.StandardInput.Close();Assert(process.WaitForExit(10000)&&process.ExitCode!=0,"duplicate pane accepted");
            Assert(before.SequenceEqual(File.ReadAllBytes(path)),"duplicate pane overwrote its original owner's receipt");
        });
        Test("R12 Refresh disk failure remains fail-closed on the following tick",()=>
        {
            var item=Item();var entry=new ControlEntry{Plan=item,Digest=Store.Digest(item),State="Started"};var book=new Ledger{BootId=Store.BootId(),Entries=[entry]};
            using var monitor=Monitor(book);var path=Get<string>(monitor,"ledgerPath");Store.Save(path,book);var before=File.ReadAllBytes(path);
            Get<Dictionary<Guid,Job>>(monitor,"jobs").Add(item.Id,new Job(Store.JobName(item.Id),ResourcePolicy.AgentBudget));
            Store.Save(Store.ReceiptPath(item.Id),new Receipt("Finished",0,0,0,"finished",DateTimeOffset.UtcNow));
            using(var reader=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))Call(monitor,"Refresh",false);
            Assert(Get<bool>(monitor,"ledgerBroken")&&before.SequenceEqual(File.ReadAllBytes(path)),"failed Refresh published authority or forgot the disk error");
            Call(monitor,"Refresh",false);Assert(Get<bool>(monitor,"ledgerBroken")&&((string?)Call(monitor,"AdmissionBlockedReason"))?.Contains("未写入")==true,"next tick silently cleared the persistence block");
        });
        Test("CR-N01 actual Refresh write failure denies begin and resume across later ticks",()=>
        {
            var plan=Item();var ended=Item();var pending=Item();
            var creator=new ControlEntry{Plan=plan,Digest=Store.Digest(plan),State="Creating"};
            var book=new Ledger{BootId=Store.BootId(),Entries=[creator,new(){Plan=ended,Digest=Store.Digest(ended),State="Started"},new(){Plan=pending,Digest=Store.Digest(pending)}]};
            using var monitor=Monitor(book);creator.Epoch=Get<string>(monitor,"epoch");
            var jobs=Get<Dictionary<Guid,Job>>(monitor,"jobs");
            jobs[plan.Id]=new Job(Store.JobName(plan.Id),ResourcePolicy.AgentBudget);
            jobs[ended.Id]=new Job(Store.JobName(ended.Id),ResourcePolicy.AgentBudget);
            Store.Save(Store.ReceiptPath(ended.Id),new Receipt("Finished",0,0,0,"fixture finished",DateTimeOffset.UtcNow));
            var path=Get<string>(monitor,"ledgerPath");Store.Save(path,book);var before=File.ReadAllBytes(path);
            using var gate=new Mutex(false,Store.AttemptLock(plan.Id));gate.WaitOne();
            try
            {
                using(var reader=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))Task.Run(()=>Call(monitor,"Refresh",false)).GetAwaiter().GetResult();
                for(var tick=0;tick<2;tick++)
                {
                    Assert(Get<bool>(monitor,"ledgerBroken"),"fixture did not retain actual persistence failure");
                    foreach(var op in new[]{"resume","begin"})
                    {
                        var refused=false;
                        try{Call(monitor,"Handle",new Request(op,Guid.NewGuid(),Attempt:op=="resume"?plan.Id:pending.Id,Epoch:creator.Epoch,Digest:creator.Digest));}
                        catch(IOException e){refused=e.Message.Contains("未写入");}
                        Assert(refused,op+" ignored the broken-ledger latch");
                    }
                    Assert(creator.State=="Creating"&&book.Entries.Single(e=>e.Plan.Id==pending.Id).State=="Pending"&&before.SequenceEqual(File.ReadAllBytes(path)),"rejection changed grant or ledger bytes");
                    Task.Run(()=>Call(monitor,"Refresh",false)).GetAwaiter().GetResult();
                }
            }
            finally{gate.ReleaseMutex();}
        });
        Test("CR-N01 healthy frozen resume does not reconsume soft admission consent",()=>
        {
            var plan=Item();var entry=new ControlEntry{Plan=plan,Digest=Store.Digest(plan),State="Creating"};
            using var monitor=Monitor(new(){Entries=[entry]});entry.Epoch=Get<string>(monitor,"epoch");
            var tracker=Get<PressureTracker>(monitor,"pressureTracker");
            for(var i=0;i<3;i++)tracker.Observe(Get<MemorySnapshot>(monitor,"sample") with{CpuPercent=95},ResourcePolicy.GiB);
            Assert(tracker.Pressure,"pressure fixture did not enter recovery");Set(monitor,"rootCpuReason","new configuration awaits old jobs");
            var response=(Response)Call(monitor,"Handle",new Request("resume",Guid.NewGuid(),Attempt:plan.Id,Epoch:entry.Epoch,Digest:entry.Digest))!;
            Assert(response.Ok&&entry.State=="Creating","resume reapplied soft admission policy to an existing grant");
        });
        foreach(var evidence in new[]{"claim","run","task","ancestor","unreadable","missing-ancestor","cycle"})
        Test("CR-N03 generic retry refuses task provenance from "+evidence+" without changing authority",()=>
        {
            var taskPath=Store.FilePath("tasks.json");
            var original=Item();var book=new TaskBook();
            if(evidence=="missing-ancestor")original=original with{RetryOf=Guid.NewGuid()};
            if(evidence=="cycle")original=original with{RetryOf=original.Id};
            if(evidence=="task")original=original with{SourceSlotId=TaskBookOps.Create(book,"queued",cwd,"custom",new AgentProfile(),"",[],"shared",null,0).Id};
            if(evidence=="run")book.Attempts.Add(new(){TaskId=Guid.NewGuid(),TaskRevision=1,AttemptId=original.Id,BatchId=original.BatchId,State="Failed",WorkspaceKey=original.Workspace.Key,ProgramFingerprint=new string('A',64)});
            var ledger=new Ledger{BootId=Store.BootId(),Entries=[new(){Plan=original,Digest=Store.Digest(original),State="Revoked"}]};
            if(evidence is "claim" or "ancestor")ledger.TaskClaims.Add(new(Guid.NewGuid(),original.Id));
            if(evidence=="ancestor")
            {
                original=original with{Id=Guid.NewGuid(),BatchId=Guid.NewGuid(),RetryOf=original.Id};
                ledger.Entries.Add(new(){Plan=original,Digest=Store.Digest(original),State="Revoked"});
            }
            Store.Save(taskPath,book);if(evidence=="unreadable")File.WriteAllText(taskPath,"{broken");
            try
            {
                using var monitor=Monitor(ledger);var path=Get<string>(monitor,"ledgerPath");Store.Save(path,ledger);
                var bytes=File.ReadAllBytes(path);var taskBytes=File.ReadAllBytes(taskPath);var before=JsonSerializer.Serialize(ledger,Store.Json);
                var retry=original with{Id=Guid.NewGuid(),BatchId=Guid.NewGuid(),RetryOf=original.Id};var refused=false;
                try{Call(monitor,"Handle",new Request("reserve",Guid.NewGuid(),[retry]));}catch(Exception e)when(e is IOException or InvalidDataException or JsonException){refused=true;}
                Assert(refused,"task retry escaped queue bookkeeping");
                Assert(before==JsonSerializer.Serialize(ledger,Store.Json)&&bytes.SequenceEqual(File.ReadAllBytes(path))&&taskBytes.SequenceEqual(File.ReadAllBytes(taskPath)),"rejected retry changed retained authority");
                Assert(Get<Dictionary<Guid,Job>>(monitor,"jobs").Count==0,"rejected retry created a job");
            }
            finally{Store.Save(taskPath,new TaskBook());}
        });
        Test("CR-N04 Worker refuses frozen runtime-only Node identity before claiming queue",()=>
        {
            var script=Path.Combine(cwd,"legacy-agent.js");File.WriteAllText(script,"legacy identity");
            var command=new CommandSpec(Environment.ProcessPath!,[script],"node-shim");
            var book=new TaskBook();var task=TaskBookOps.Create(book,"legacy Node",cwd,"custom",new AgentProfile(),"",[],"shared",null,0);
            var queued=TaskBookOps.Enqueue(book,task.Id,command,AgentProbe.Fingerprint(command.Executable));
            var taskPath=Store.FilePath("tasks.json");Store.Save(taskPath,book);var taskBytes=File.ReadAllBytes(taskPath);
            try
            {
                using var monitor=Monitor();var plan=Item() with{SourceSlotId=task.Id,AgentId="custom",Command=command};var sample=Get<MemorySnapshot>(monitor,"sample");
                var policy=ResourcePolicy.SnapshotFor(queued.Resource,sample.Total,sample.CommitLimit);var refused=false;
                try{Call(monitor,"Handle",new Request("reserve-task",Guid.NewGuid(),[plan],Policy:policy,PolicyHash:ResourcePolicy.PolicyHash(policy),PlanDigest:ResourcePolicy.PlanDigest([plan]),TaskEntryId:queued.Id));}catch(IOException){refused=true;}
                Assert(refused,"legacy runtime-only identity still authorized Node task");
                Assert(Get<Ledger>(monitor,"ledger").TaskClaims.Count==0&&Get<Ledger>(monitor,"ledger").Entries.Count==0&&taskBytes.SequenceEqual(File.ReadAllBytes(taskPath)),"identity rejection consumed queue authority");
            }
            finally{Store.Save(taskPath,new TaskBook());}
        });
        foreach(var phase in new[]{"begin","resume"})
        Test("CR-N04 Node upgrade after reservation refuses "+phase+" without changing grants",()=>
        {
            var script=Path.Combine(cwd,"upgrade-"+phase+".js");File.WriteAllText(script,"before");
            var command=new CommandSpec(Environment.ProcessPath!,[script],"node-shim");var book=new TaskBook();
            var task=TaskBookOps.Create(book,"Node activation",cwd,"custom",new AgentProfile(),"",[],"shared",null,0);
            var queued=TaskBookOps.Enqueue(book,task.Id,command,AgentProbe.Fingerprint(command));
            var plan=Item() with{Command=command,SourceSlotId=task.Id};
            var entry=new ControlEntry{Plan=plan,Digest=Store.Digest(plan),State=phase=="begin"?"Pending":"Creating"};
            var ledger=new Ledger{BootId=Store.BootId(),Entries=[entry],TaskClaims=[new(queued.Id,plan.Id)]};
            if(phase=="resume")TaskBookOps.StartRun(book,queued,plan.Id,plan.BatchId,plan.Workspace.Key);
            var taskPath=Store.FilePath("tasks.json");Store.Save(taskPath,book);var taskBytes=File.ReadAllBytes(taskPath);
            try
            {
                using var monitor=Monitor(ledger);entry.Epoch=Get<string>(monitor,"epoch");
                var path=Get<string>(monitor,"ledgerPath");Store.Save(path,ledger);var bytes=File.ReadAllBytes(path);var before=JsonSerializer.Serialize(ledger,Store.Json);
                File.WriteAllText(script,"after");var refused=false;
                try{Call(monitor,"Handle",new Request(phase,Guid.NewGuid(),Attempt:plan.Id,Epoch:entry.Epoch,Digest:entry.Digest));}catch(IOException e){refused=e.Message.Contains("程序");}
                Assert(refused,"activation accepted an upgrade after reservation");
                Assert(before==JsonSerializer.Serialize(ledger,Store.Json)&&bytes.SequenceEqual(File.ReadAllBytes(path))&&taskBytes.SequenceEqual(File.ReadAllBytes(taskPath)),"activation rejection altered authority");
                Assert(Get<Dictionary<Guid,Job>>(monitor,"jobs").Count==0,"identity rejection created a job");
            }
            finally{Store.Save(taskPath,new TaskBook());}
        });
        Test("UX06 Worker rejects deferred reservation and frozen begin before creating authority",()=>
        {
            var taskPath=Store.FilePath("tasks.json");
            foreach(var phase in new[]{"reserve-task","begin"})
            {
                var book=new TaskBook();var task=TaskBookOps.Create(book,"deferred",cwd,"custom",new AgentProfile(),"",[],"shared",null,0);
                var plan=Item() with{SourceSlotId=task.Id,AgentId="custom"};
                var queued=TaskBookOps.Enqueue(book,task.Id,plan.Command,AgentProbe.Fingerprint(plan.Command));
                TaskBookOps.SetDeferred(book,queued.Id,true);Store.Save(taskPath,book);var taskBytes=File.ReadAllBytes(taskPath);
                var entry=new ControlEntry{Plan=plan,Digest=Store.Digest(plan),State="Pending"};
                var ledger=phase=="begin"?new Ledger{BootId=Store.BootId(),Entries=[entry],TaskClaims=[new(queued.Id,plan.Id)]}:new Ledger{BootId=Store.BootId()};
                using var monitor=Monitor(ledger);entry.Epoch=Get<string>(monitor,"epoch");
                var sample=Get<MemorySnapshot>(monitor,"sample");var policy=ResourcePolicy.SnapshotFor(queued.Resource,sample.Total,sample.CommitLimit);
                var before=JsonSerializer.Serialize(ledger,Store.Json);var refused=false;
                try{Call(monitor,"Handle",phase=="begin"?new Request("begin",Guid.NewGuid(),Attempt:plan.Id,Epoch:entry.Epoch,Digest:entry.Digest)
                    :new Request("reserve-task",Guid.NewGuid(),[plan],Policy:policy,PolicyHash:ResourcePolicy.PolicyHash(policy),PlanDigest:ResourcePolicy.PlanDigest([plan]),TaskEntryId:queued.Id));}
                catch(IOException e){refused=e.Message.Contains("暂缓");}
                Assert(refused&&before==JsonSerializer.Serialize(ledger,Store.Json)&&taskBytes.SequenceEqual(File.ReadAllBytes(taskPath)),phase+" ignored deferral or consumed authority");
                Assert(Get<Dictionary<Guid,Job>>(monitor,"jobs").Count==0,"deferred entry created a job");
            }
            Store.Save(taskPath,new TaskBook());
        });
        Store.Save(Store.FilePath("repair-results.json"),results);Console.WriteLine($"Repair checks: {results.Count-failures} passed, {failures} failed; {Store.Root}");return failures==0?0:1;
    }
}
