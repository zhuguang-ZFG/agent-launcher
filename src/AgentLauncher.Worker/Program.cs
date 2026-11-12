using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Worker;
internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if(args is ["monitor"])return new Monitor().Run();
            if(args.Length>=6&&args[0]=="pool-host")return PoolHost(args);
            if(args.Length==2 && args[0] is "pane" or "run" && Guid.TryParse(args[1],out var id))return RunPane(id,args[0]=="pane");
            Console.Error.WriteLine("用法: monitor | pane <attempt-guid> | pool-host <池> <项> <就绪> <释放> <新建池>");return 2;
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {
            if(args is ["monitor"])
                try{Store.Save(Store.FilePath("monitor-error.json"),new{At=DateTimeOffset.UtcNow,Type=e.GetType().FullName,e.HResult,Error=e.ToString()});}catch(Exception writeError)when(writeError is not OutOfMemoryException){}
            if(args.Length==2&&args[0] is "pane" or "run"&&Guid.TryParse(args[1],out var failedAttempt))
                try{Store.Save(Store.FilePath(Path.Combine("attempts",failedAttempt.ToString("N"),"runner-error.json")),new{At=DateTimeOffset.UtcNow,Pid=Environment.ProcessId,Type=e.GetType().FullName,e.HResult,Error=e.ToString()});}catch(Exception writeError)when(writeError is not OutOfMemoryException){}
            Console.Error.WriteLine("启动器: "+e.Message);HoldPane(args is ["pane",_],1,false);return 1;
        }
    }
    private static void HoldPane(bool interactive,int exit,bool limited)
    {
#if !INTEGRATION_TESTS
        if(!interactive)return;
        var key=PaneExitPolicy.DismissalKey(exit,limited);
        Console.WriteLine($"\n进程已退出（{exit}）；工作目录已保留。按 {key} 关闭此窗格。");
        // A redirected/closed stdin is not an interactive terminal; never hang a caller that cannot acknowledge.
        if(Console.IsInputRedirected)return;
        try{while(Console.ReadKey(intercept:true).Key!=key){}}
        catch(Exception e)when(e is InvalidOperationException or IOException){Console.Error.WriteLine("无法读取关闭确认；请在启动器运行记录中核对错误。");}
#endif
    }
    // A job created by a process that is already inside a job becomes its child. The pool therefore needs a short-lived
    // helper that the monitor starts inside the root job; the helper creates the pool/items, the monitor opens them by
    // name while the helper still holds handles, and only then is the helper released.
    private static int PoolHost(string[] args)
    {
        Job? pool=null;
        try
        {
            var poolLimit=ulong.Parse(args[6],System.Globalization.CultureInfo.InvariantCulture);
            var itemLimit=args.Length>7?ulong.Parse(args[7],System.Globalization.CultureInfo.InvariantCulture):0;
            pool=args[5]=="1"?new Job(args[1]):new Job(args[1],openOnly:true);
            if(args[5]=="1")
            {
                if(!pool.CreatedNew)throw new IOException("资源池已存在，未重复创建。");
                Native.Check(Native.AssignProcessToJobObject(pool.Handle,Process.GetCurrentProcess().Handle));
                pool.SetMemoryLimit(poolLimit);
            }
            if(pool.QueryMemory() is not { LimitBytes: var probed } || probed!=poolLimit)throw new IOException("资源池上限未生效。");
            Job? item=null;
            if(args[2].Length>0)
            {
                item=new Job(args[2]);
                if(itemLimit==0||itemLimit>poolLimit)throw new IOException("单项上限无效。");
                item.SetMemoryLimit(itemLimit);
                if(item.QueryMemory() is not { LimitBytes: var itemProbed } || itemProbed!=itemLimit)throw new IOException("单项上限未生效。");
            }
            File.WriteAllText(args[3],"ready");
            var watch=Stopwatch.StartNew();
            while(!File.Exists(args[4]))
            {
                if(watch.ElapsedMilliseconds>15000)return 3;
                Thread.Sleep(20);
            }
            GC.KeepAlive(pool);
            if(item!=null){GC.KeepAlive(item);item.Dispose();}
            return 0;
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {
            try{File.WriteAllText(args[3],"error:"+e.Message);}catch{}
            return 1;
        }
        finally{pool?.Dispose();}
    }
    private static int RunPane(Guid id,bool interactive)
    {
        Store.Save(Store.FilePath(Path.Combine("attempts",id.ToString("N"),"runner-entered.json")),new{At=DateTimeOffset.UtcNow,Pid=Environment.ProcessId});
        using var mutex=new Mutex(false,Store.AttemptLock(id));bool owned;
        try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
        if(!owned)throw new IOException("该启动请求正在处理。");
        // The monitor serves one pipe at a time. In a full pane batch, concurrent begin requests can each create
        // a pool helper before queued resume requests are served, exhausting the 8-second IPC budget.
        // Queue only the spawn transaction; release it as soon as Started is durable, so Agents still run in parallel.
        using var startup=new Mutex(false,Store.RootJob+"-startup");var startupOwned=false;
        ProtectedProcess? child=null;Job? root=null;Job? pool=null;Job? job=null;JobMemoryWatch? memoryWatch=null;var resumeIntent=false;var claimCreated=false;int? memoryOffender=null;
        JobMemoryEvidence? exitMemory=null;int? poolOffender=null;Guid? panePoolId=null;DateTimeOffset? poolNoticeAt=null;string poolNoticeDetail="";var poolCheck=Stopwatch.StartNew();
        try
        {
            var claim=Path.Combine(Store.Root,"attempts",id.ToString("N"),"claim");Directory.CreateDirectory(Path.GetDirectoryName(claim)!);
            using(var file=new FileStream(claim,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){file.WriteByte(1);file.Flush(true);}claimCreated=true;
#if INTEGRATION_TESTS
            if(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAIL_AT")=="BeforeBegin")Environment.Exit(97);
#endif
            try{startupOwned=startup.WaitOne(TimeSpan.FromSeconds(60));}catch(AbandonedMutexException){startupOwned=true;}
            if(!startupOwned)throw new IOException("同一监控器的启动队列等待超时，尚未创建 Agent；请在运行记录中核对。");
            var grant=Ipc.SendAsync(new("begin",Guid.NewGuid(),Attempt:id)).GetAwaiter().GetResult();
            if(!grant.Ok||grant.Entries is not {Length:1})throw new IOException(grant.Message);
            var entry=grant.Entries[0];var plan=entry.Plan;
            root=new Job(Store.RootJob,openOnly:true);job=new Job(Store.JobName(id),openOnly:true);
            memoryWatch=new JobMemoryWatch(job);
            if(plan.PoolId is {} poolId){panePoolId=poolId;pool=new Job(Store.PoolName(poolId),openOnly:true);}
            if(PhysicalDirectory.Workspace(plan.Cwd).Key!=plan.Workspace.Key)throw new IOException("工作目录物理身份变化，已阻止启动。");
            if(interactive)TerminalAppearance.WritePaneHeading(plan);
            Write("SpawnIntent",0,0,null,"正在受保护地创建进程");
            child=pool==null?new ProtectedProcess(plan.Command,plan.Cwd,root,job,redirected:!interactive):new ProtectedProcess(plan.Command,plan.Cwd,[root,pool,job],redirected:!interactive);
            Write("CreatedSuspended",child.Process.Id,child.CreationTime,null,"已创建，尚未运行");
            var resume=Ipc.SendAsync(new("resume",Guid.NewGuid(),Attempt:id,Epoch:grant.Epoch,Digest:entry.Digest)).GetAwaiter().GetResult();
            if(!resume.Ok)throw new IOException(resume.Message);
            Write("ResumeIntent",child.Process.Id,child.CreationTime,null,"准备恢复初始线程");resumeIntent=true;
            try
            {
#if INTEGRATION_TESTS
                if(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAIL_AT")=="ResumeRejected")throw new System.ComponentModel.Win32Exception(6,"Controlled ResumeThread rejection");
#endif
                child.Resume();
            }
            catch(System.ComponentModel.Win32Exception){resumeIntent=false;throw;}
            Write("Started",child.Process.Id,child.CreationTime,null,"已启动进程（不代表任务完成）");
        }
        catch(Exception error)when(error is not OutOfMemoryException)
        {
            try
            {
                // Only this claim owner may publish evidence. A duplicate runner must not overwrite a live attempt's receipt.
                if(claimCreated&&child is null&&!resumeIntent)Write("FailedBeforeStart",0,0,null,"未启动："+error.Message);
                if(child!=null&&!resumeIntent){child.AbortBeforeResume();Write("FailedBeforeStart",child.Process.Id,child.CreationTime,null,"未运行的进程已回收："+error.Message);}
            }
            finally{memoryWatch?.Dispose();child?.Dispose();job?.Dispose();pool?.Dispose();root?.Dispose();}
            throw;
        }
        finally{if(startupOwned)startup.ReleaseMutex();mutex.ReleaseMutex();}
        try
        {
            while(!child!.Process.WaitForExit(250))ObserveMemory();
            if(pool!=null)try{Ipc.SendAsync(new("status",Guid.NewGuid())).GetAwaiter().GetResult();}catch(Exception e)when(e is IOException or OperationCanceledException or TimeoutException){poolNoticeDetail="共享池通知尚未核对；退出码不能单独证明内存超限。";}
            ObserveMemory(forcePool:true);var exit=child.Process.ExitCode;
            exitMemory=MemoryEvidence();var detail=ExitDiagnostics.Describe(exit,exitMemory);
            if(pool is not null)detail+="共享资源池："+ExitDiagnostics.MemoryText(PoolEvidence())+poolNoticeDetail;
            Write("Exited",child.Process.Id,child.CreationTime,exit,"进程已退出。"+detail);
            if(interactive&&exit!=0)Console.WriteLine("\nAgent 异常退出。"+detail+"\n工作目录已保留；如仍有工具运行，将等待它们结束。");
            while(job!.Members().Length>0){Thread.Sleep(250);ObserveMemory();}
            ObserveMemory(forcePool:true);exitMemory=MemoryEvidence();detail=ExitDiagnostics.Describe(exit,exitMemory)+(pool is null?"":"共享资源池："+ExitDiagnostics.MemoryText(PoolEvidence())+poolNoticeDetail);
            Write("Finished",child.Process.Id,child.CreationTime,exit,"全部受管进程已退出。"+detail);
#if !INTEGRATION_TESTS
            HoldPane(interactive,exit,exitMemory?.LimitReached==true||poolOffender!=null);
#endif
            return exit;
        }
        finally{memoryWatch?.Dispose();child?.Dispose();job?.Dispose();pool?.Dispose();root?.Dispose();}
        JobMemoryEvidence? MemoryEvidence()=>job?.QueryMemory() is {} evidence?evidence with{LimitReached=memoryOffender!=null,OffendingProcessId=memoryOffender}:null;
        JobMemoryEvidence? PoolEvidence()=>pool?.QueryMemory() is {} evidence?evidence with{LimitReached=poolOffender!=null,OffendingProcessId=poolOffender}:null;
        void ObserveMemory(bool forcePool=false)
        {
            var pids=memoryWatch!.Drain();var changed=pids.Length>0;
            if(changed){memoryOffender=pids[0];exitMemory=MemoryEvidence();}
            if(panePoolId is {} poolId&&(forcePool||poolCheck.ElapsedMilliseconds>=1000))
            {
                poolCheck.Restart();
                try
                {
                    var notice=Store.Read<PoolLimitNotice>(Store.FilePath(Path.Combine("pools",poolId.ToString("N")+".limit.json")));
                    if(notice is not null&&notice.PoolId==poolId&&notice.At>=DateTimeOffset.FromFileTime(child!.CreationTime)&&notice.At!=poolNoticeAt)
                    {
                        poolNoticeAt=notice.At;changed=true;
                        if(notice.Pid==child.Process.Id||job!.Members().Contains(notice.Pid))poolOffender=notice.Pid;
                        else poolNoticeDetail=$"同一共享池已收到内核分配拒绝通知（PID {notice.Pid}），该 PID 与本窗格的归属未确认。";
                    }
                }
                catch(Exception e)when(e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException){poolNoticeDetail="共享池通知不可读取，不能据此判断本窗格超限。";}
            }
            if(!changed)return;
            var text=ExitDiagnostics.MemoryText(exitMemory)+(pool is null?"":"共享池："+ExitDiagnostics.MemoryText(PoolEvidence())+poolNoticeDetail);
            Write(child!.Process.HasExited?"Exited":"Started",child.Process.Id,child.CreationTime,child.Process.HasExited?child.Process.ExitCode:null,text);
            if(interactive)Console.Error.WriteLine("\n"+text);
        }
        void Write(string phase,int pid,long time,int? code,string detail)
        {
            Store.Save(Store.ReceiptPath(id),new Receipt(phase,pid,time,code,detail,DateTimeOffset.UtcNow,exitMemory,PoolEvidence()));
#if INTEGRATION_TESTS
            if(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAIL_AT")==phase)Environment.Exit(97);
#endif
        }
    }
}
internal sealed class Monitor : IDisposable
{
    private readonly Sampler sampler=new();
    private readonly Dictionary<Guid,Job> jobs=[];
    private readonly Dictionary<Guid,Job> pools=[];
    private readonly Dictionary<Guid,JobMemoryWatch> poolWatches=[];
    private readonly Dictionary<Guid,string> poolWatchErrors=[];
    private readonly string epoch=Guid.NewGuid().ToString("N");
    private readonly string ledgerPath=Store.FilePath("control.json");
    private Ledger ledger=new();
    private readonly string configurationPath=Store.FilePath("configuration.json");
    private readonly Dictionary<Guid,ReceiptCacheEntry> receiptCache=[];
    private readonly Dictionary<Guid,StatusEntryCache> statusEntryCache=[];
    private int? rootCpuRate;
    private string? rootCpuReason;
    private bool retireForCpuChange;
    private Job? root;
    private MemorySnapshot? sample;
    private DateTime lastActivity=DateTime.UtcNow;
    private string? blocked;
    private bool reconcileOffered;
    private bool ledgerBroken;
    private string ledgerBrokenDetail="控制账本损坏或版本不支持，已停止启动与回收。请人工核对 control.json，必要时用 .bak 备份恢复。";
    private readonly PressureTracker pressureTracker=new();
    private bool pressure=>pressureTracker.Pressure;
    private readonly WaiverTable waivers=new(()=>DateTimeOffset.UtcNow);
    private static readonly TimeSpan IdleTimeout=TimeSpan.FromSeconds(ResourcePolicy.IdleTimeoutSeconds);
    private static readonly TimeSpan AcceptTick=TimeSpan.FromSeconds(1);
    private const long LedgerBudget=6L*1024*1024;
    public int Run()
    {
        using var single=new Mutex(false,@"Local\"+Store.Pipe+"-monitor");bool own;
        try{own=single.WaitOne(0);}catch(AbandonedMutexException){own=true;}
        if(!own)return 0;
        try
        {
            Directory.CreateDirectory(Store.Root);Store.Save(Store.FilePath("monitor-pid.json"),Environment.ProcessId);
            LoadLedger();
            var boot=Store.BootId();
            if(!ledgerBroken&&LedgerMigration.Rollover(ledger,boot))
            {
                // Retention must never kill the monitor at startup: fail closed with a reason the user can act on instead.
                try{Save();}catch(Exception e)when(e is InvalidDataException or IOException){ledgerBroken=true;ledgerBrokenDetail="控制账本无法安全写入，已停止启动与回收："+e.Message;blocked=ledgerBrokenDetail;}
            }
            root=new Job(Store.RootJob,root:true,cpuRate:RootCpuRate());
            RefreshRootCpu();
            sampler.Sample();Thread.Sleep(250);Refresh();
            while(true)
            {
                NamedPipeServerStream pipe;
                try{pipe=new NamedPipeServerStream(Store.Pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);}
                catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException){blocked="命名管道被占用或不可用，正在重试："+e.Message;Thread.Sleep(1000);continue;}
                using(pipe)
                {
                    var accept=pipe.WaitForConnectionAsync();var connected=false;
                    while(!connected)
                    {
                        // The accept task stays pending across ticks: a client that connects at a tick boundary is never stranded by disposal.
                        var tick=Task.WhenAny(accept,Task.Delay(AcceptTick)).GetAwaiter().GetResult();
                        if(tick==accept){connected=true;break;}
                        Refresh();
                        // Retire only when nothing can still be reconciled and no client polled recently; any accepted request resets lastActivity.
                        if(CanRetire()&&DateTime.UtcNow-lastActivity>=IdleTimeout)
                        {
                            // Give a simultaneous poll one final chance instead of stranding it on a disposed listener.
                            if(accept.IsCompleted||Task.WhenAny(accept,Task.Delay(250)).GetAwaiter().GetResult()==accept){connected=true;break;}
                            return 0;
                        }
                    }
                    lastActivity=DateTime.UtcNow;
                    using var readDeadline=new CancellationTokenSource(3000);
                    try
                    {
                        // WhenAny reports completion even when ConnectNamedPipe failed because its peer already closed.
                        // Observe that outcome before reading; otherwise PipeStream can throw "not connected" and exit the monitor.
                        accept.GetAwaiter().GetResult();
                        if(!pipe.IsConnected)continue;
                        var request=Ipc.ReadAsync<Request>(pipe,readDeadline.Token).GetAwaiter().GetResult();Refresh(forceSample:!ledger.Entries.Any(e=>!Store.IsTerminal(e)));
                        Response response;
                        try{response=Handle(request);}catch(Exception e)when(e is not OutOfMemoryException){response=new(false,e.Message);}
                        // Completed authority work must not spend the time reserved for transmitting its outcome.
                        using var replyDeadline=new CancellationTokenSource(3000);
                        Ipc.WriteResponseAsync(pipe,response,replyDeadline.Token).GetAwaiter().GetResult();
                        if(retireForCpuChange&&CanRetire())return 0;
                    }
                    catch(InvalidOperationException)when(!pipe.IsConnected){/* A peer may disappear between accept and the first read. */}
                    catch(Exception e)when(e is IOException or OperationCanceledException or JsonException){/* Peer failure never grants additional authorization. */}
                }
            }
        }
        finally{Dispose();single.ReleaseMutex();}
    }
    // A missing ledger is only a fresh install. Store.Save publishes control.json.bak on every replace, so its presence proves a
    // book existed; starting from an empty one would forget retained claims and let the same queue entry be claimed again.
    private void LoadLedger()
    {
        var lost=false;
        try
        {
            var stored=Store.Read<Ledger>(ledgerPath);
            lost=stored is null&&File.Exists(ledgerPath+".bak");
            if(lost)throw new InvalidDataException();
            ledger=LedgerMigration.Accept(stored??new());
            return;
        }
        catch(Exception e)when(e is JsonException or InvalidDataException or IOException)
        {
            ledgerBroken=true;ledger=new();
            ledgerBrokenDetail=lost
                ?"控制账本丢失但存在 .bak 备份，已停止启动与回收。请人工核对 control.json 并恢复备份后再启动。"
                :"控制账本损坏或版本不支持，已停止启动与回收。请人工核对 control.json，必要时用 .bak 备份恢复。";
        }
        blocked=ledgerBrokenDetail;
    }
    private void EnsureReservationCapacity(ControlEntry[] entries,PoolRecord[] addedPools,TaskReservationClaim? claim)
    {
        // Measure the whole proposed authority, not individual frames. History can be archived, active authority cannot.
        var projected=new Ledger{BootId=ledger.BootId,Entries=ledger.Entries.Where(e=>!Store.IsTerminal(e)).Concat(entries).ToList(),Pools=ledger.Pools.Concat(addedPools).ToList(),TaskClaims=ledger.TaskClaims.Concat(claim is null?[]:new[]{claim}).ToList()};
        var bytes=JsonSerializer.SerializeToUtf8Bytes(projected,Store.Json).LongLength;
        // State/receipt diagnostics and pool bookkeeping must still fit after a grant has been issued.
        var growth=512L*1024+projected.Entries.Count*64L*1024;
        if(bytes+growth>LedgerBudget)throw new IOException("控制账本容量不足（含运行状态增长余量），整批未预约；活动记录与领取证据不会裁剪。");
    }
    private void Save()
    {
        if(ledgerBroken)throw new IOException(ledgerBrokenDetail);
        try
        {
            foreach(var entry in ledger.Entries)if(entry.Detail.Length>2048)entry.Detail=entry.Detail[..2048];
            var total=PruneLedger();
            if(total>LedgerBudget)throw new InvalidDataException("控制账本超过 6 MiB 写入上限；活动记录未裁剪。");
            Store.Save(ledgerPath,ledger);
        }
        catch(Exception e)when(e is InvalidDataException)
        {
            ledgerBroken=true;blocked=ledgerBrokenDetail="控制账本无法安全写入，已停止启动与回收："+e.Message;throw;
        }
    }
    private long PruneLedger()
    {
        var total=JsonSerializer.SerializeToUtf8Bytes(ledger,Store.Json).LongLength;
        foreach(var entry in ledger.Entries.Where(Store.IsTerminal).ToArray())
        {
            if(total<=LedgerBudget)break;
            // Publish identity-bound terminal evidence BEFORE removing the display record. Never fall back to an old backup/receipt.
            if(string.IsNullOrEmpty(entry.Digest))entry.Digest=Store.Digest(entry.Plan);
            if(entry.Digest!=Store.Digest(entry.Plan))throw new InvalidDataException("终态计划摘要不符，未裁剪记录。");
            Store.Save(OutcomePath(entry.Plan.Id),new ArchivedAttempt(1,entry.Plan.Id,entry.Digest,entry));
            ledger.Entries.Remove(entry);statusEntryCache.Remove(entry.Plan.Id);
            total=JsonSerializer.SerializeToUtf8Bytes(ledger,Store.Json).LongLength;
        }
        return total;
    }
    private static string OutcomePath(Guid id)=>Path.Combine(Store.Root,"attempts",id.ToString("N"),"outcome.json");
    private Response Attempt(Request request)
    {
        if(ledgerBroken)return new(false,ledgerBrokenDetail);
        if(request.Attempt is not {} id||id==Guid.Empty)return new(false,"缺少尝试身份。");
        var entry=ledger.Entries.SingleOrDefault(e=>e.Plan.Id==id);
        if(entry is null)
        {
            var outcome=Store.Read<ArchivedAttempt>(OutcomePath(id));
            if(outcome is null)return new(false,"尝试结果尚无法确认；缺失不是结束证据。");
            if(outcome.SchemaVersion!=1||outcome.AttemptId!=id||outcome.Entry?.Plan?.Id!=id||!Store.IsTerminal(outcome.Entry)
                ||outcome.Digest!=outcome.Entry.Digest||outcome.Digest!=Store.Digest(outcome.Entry.Plan))return new(false,"尝试结果身份或摘要无效，需人工核对。");
            entry=outcome.Entry;
        }
        if(entry.Digest!=Store.Digest(entry.Plan)||request.Digest is {} digest&&digest!=entry.Digest)return new(false,"尝试计划摘要不符。");
        return new(true,"已按尝试身份核对",[entry],Epoch:epoch);
    }
    private void Refresh(bool forceSample=false)
    {
        var samplingNeeded=jobs.Count>0||ledgerBroken||blocked!=null||pressure||ledger.Entries.Any(e=>!Store.IsTerminal(e));
        if(forceSample||sample==null||(samplingNeeded&&DateTimeOffset.UtcNow-sample.At>=TimeSpan.FromSeconds(2)))
        {
            sample=sampler.Sample();
#if INTEGRATION_TESTS
            if(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE")=="1")sample=new(16*ResourcePolicy.GiB,12*ResourcePolicy.GiB,32*ResourcePolicy.GiB,24*ResourcePolicy.GiB,5,DateTimeOffset.UtcNow);
#endif
            ulong floor;
            try{floor=AuthorityPolicy().PhysicalFloor;}
            catch(Exception e)when(e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException){floor=ResourcePolicy.DefaultPhysicalFloor(sample.Total);}
            pressureTracker.Observe(sample,floor);
        }
        var changed=false;blocked=ledgerBroken?ledgerBrokenDetail:null;reconcileOffered=false;
        foreach(var record in ledger.Pools.Where(p=>!pools.ContainsKey(p.Id)))
        {
            try{pools[record.Id]=new Job(Store.PoolName(record.Id),openOnly:true);WatchPool(record.Id,pools[record.Id]);}
            catch(System.ComponentModel.Win32Exception ex) when(ex.NativeErrorCode==2)
            {
                blocked="资源池句柄丢失，无法证明整池成员已退出；保留池与预算，需人工核对后释放。";
                reconcileOffered=true;
            }
        }
        DrainPoolLimits();
        foreach(var e in ledger.Entries.Where(e=>!Store.IsTerminal(e)&&e.State!="Pending"))
        {
            using var gate=new Mutex(false,Store.AttemptLock(e.Plan.Id));bool own;
            try{own=gate.WaitOne(0);}catch(AbandonedMutexException){own=true;}
            if(!own)continue;
            try
            {
                if(e.Plan.PoolId is {} poolId && !pools.ContainsKey(poolId))
                {
                    try{pools[poolId]=new Job(Store.PoolName(poolId),openOnly:true);WatchPool(poolId,pools[poolId]);}
                    catch(System.ComponentModel.Win32Exception ex) when(ex.NativeErrorCode==2)
                    {
                        blocked="资源池句柄丢失，无法证明整池成员已退出；保留预算，系统重启后可核对释放。";
                        e.Detail=blocked;reconcileOffered=true;continue;
                    }
                }
                Job? j=null;
                if(!jobs.TryGetValue(e.Plan.Id,out j))
                {
                    try{jobs[e.Plan.Id]=j=new Job(Store.JobName(e.Plan.Id),openOnly:true);}
                    catch(System.ComponentModel.Win32Exception ex)when(ex.NativeErrorCode==2){/* Last handle may be gone while processes still exist; absence is not exit evidence. */}
                }
                var receipt=ReadReceipt(e.Plan.Id);
                if(j==null && receipt?.Phase is not ("Finished" or "FailedBeforeStart")) {blocked="资源组句柄丢失，无法证明后代已退出；保留预算，系统重启后可核对释放。";e.Detail=blocked;reconcileOffered=true;continue;}
                var members=j?.Members()??[];
                if(members.Length==0){e.State=receipt?.Phase is "Started" or "Exited" or "ResumeIntent" or "Finished"?"Settled":"FailedBeforeStart";e.Detail=receipt?.Detail??"创建者已退出，核对无存活进程";changed=true;continue;}
                if(receipt?.Phase is "SpawnIntent" or "CreatedSuspended")
                {
                    // Gate proves no creator is running; no persisted ResumeIntent means our children never ran.
                    foreach(var pid in members){using var p=Process.GetProcessById(pid);Native.Check(Native.IsProcessInJob(p.Handle,j!.Handle,out var member));if(!member)throw new IOException("进程归属变化");Native.Check(Native.TerminateProcess(p.Handle,1));p.WaitForExit(2000);}
                    e.State="FailedBeforeStart";e.Detail="已回收创建中断的未运行进程";changed=true;
                }
                else
                {
                    var state=receipt?.Phase switch{"Started"=>"Started","Exited"=>"Exited",_=>"Unknown"};
                    if(e.State!=state){e.State=state;changed=true;}
                    e.Detail=receipt?.Phase=="Exited"?"Agent 已退出，工具仍运行。"+receipt.Detail:receipt?.Phase=="Started"?"已启动进程（不代表任务完成）":"结果未确认；占用保留";
                    if(receipt?.PoolMemory?.LimitReached==true)e.Detail+="。共享池："+ExitDiagnostics.MemoryText(receipt.PoolMemory);
                    if(e.Plan.PoolId is {} watchedPool&&poolWatchErrors.TryGetValue(watchedPool,out var watchError))e.Detail+="。共享池通知不可用："+watchError;
                    if(receipt?.JobMemory?.LimitReached==true)e.Detail+="。"+ExitDiagnostics.MemoryText(receipt.JobMemory);
                    else if(receipt?.Phase=="Started"&&j?.QueryMemory() is {} memory&&ExitDiagnostics.NearLimit(memory))e.Detail+="。"+ExitDiagnostics.MemoryText(memory);
                }
            }
            catch(Exception ex)when(ex is not OutOfMemoryException){blocked="恢复核对失败："+ex.Message;e.Detail=blocked;}
            finally{gate.ReleaseMutex();}
        }
        if(changed)
        {
            PersistRefresh();
        }
        // Save can prune a just-finished record in the same tick, so absence from the book means "no longer active", not a defect.
        var activeIds=ledger.Entries.Where(e=>!Store.IsTerminal(e)).Select(e=>e.Plan.Id).ToHashSet();
        foreach(var id in jobs.Keys.Where(id=>!activeIds.Contains(id)).ToArray()){jobs[id].Dispose();jobs.Remove(id);receiptCache.Remove(id);}
        foreach(var id in pools.Keys.Where(id=>!ledger.Entries.Any(e=>!Store.IsTerminal(e)&&e.Plan.PoolId==id)).ToArray())
        {
            var pool=pools[id];
            if(pool.Members().Length>0){blocked="资源池仍有存活或未归属的进程，保留池与预算，等待全部退出。";continue;}
            if(poolWatches.Remove(id,out var watcher))watcher.Dispose();poolWatchErrors.Remove(id);
            pool.Dispose();pools.Remove(id);
            if(ledger.Pools.RemoveAll(p=>p.Id==id)>0)PersistRefresh();
        }
        foreach(var id in receiptCache.Keys.Where(id=>!ledger.Entries.Any(e=>e.Plan.Id==id&&!Store.IsTerminal(e))).ToArray())receiptCache.Remove(id);
        var known=jobs.Values.SelectMany(j=>j.Members()).Concat(pools.Values.SelectMany(j=>j.Members())).ToHashSet();
        if(blocked==null&&root!.Members().Any(pid=>!known.Contains(pid)))blocked="发现资源组内尚未归属的进程，暂停新启动。";
        RefreshRootCpu();
    }
    private void PersistRefresh()
    {
        try{Save();}
        catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException){ledgerBroken=true;blocked=ledgerBrokenDetail="控制账本未写入，已停止启动与回收："+e.Message;}
    }
    private bool CanRetire()=>!ledgerBroken&&blocked==null&&jobs.Count==0&&pools.Count==0&&ledger.Entries.All(Store.IsTerminal);
    private sealed record ReceiptCacheEntry(DateTime Written,long Length,Receipt? Receipt);
    private Receipt? ReadReceipt(Guid id)
    {
        var info=new FileInfo(Store.ReceiptPath(id));
        if(!info.Exists){receiptCache.Remove(id);return null;}
        var written=info.LastWriteTimeUtc;var length=info.Length;
        if(receiptCache.TryGetValue(id,out var cached)&&cached.Written==written&&cached.Length==length)return cached.Receipt;
        var receipt=Store.Read<Receipt>(info.FullName);info.Refresh();
        if(info.Exists&&info.LastWriteTimeUtc==written&&info.Length==length)receiptCache[id]=new(written,length,receipt);
        else receiptCache.Remove(id);
        return receipt;
    }
    private void RefreshRootCpu()
    {
        if(root==null)return;
        var actual=root.QueryCpuRate();
        rootCpuRate=actual is {} rate&&(rate.Flags&5)==5?checked((int)rate.Rate):null;
        retireForCpuChange=false;
        if(rootCpuRate==null){rootCpuReason="无法核对当前根 Job 的 CPU 硬封顶，已暂停新启动。";return;}
        try
        {
            var (settings,_,_,_)=LoadAuthority();var desired=settings.Mode=="custom"?settings.CpuRate:ResourcePolicy.DefaultCpuRate;
            if(rootCpuRate==desired){rootCpuReason=null;return;}
            rootCpuReason=$"当前根 Job CPU 封顶为 {rootCpuRate.Value/100.0:0.##}%，新配置为 {desired/100.0:0.##}%；现有 Job 不会修改。请等待现有任务结束并让监控器退出，再重新检查资源。";
            retireForCpuChange=CanRetire();
        }
        catch(Exception e)when(e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException){rootCpuReason="无法核对根 Job CPU 配置，已暂停新启动："+e.Message;}
    }
    private Response Handle(Request request)
    {
        try
        {
            if(request.Operation is not ("reserve" or "reserve-task" or "waiver"))return HandleRequest(request);
            var key=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(configurationPath).ToUpperInvariant())));
            using var gate=new Mutex(false,@"Local\AgentLauncher-file-"+key);bool own;
            try{own=gate.WaitOne(2000);}catch(AbandonedMutexException){own=true;}
            if(!own)throw new IOException("配置正在写入，请重新检查资源。");
            try{return HandleRequest(request);}finally{gate.ReleaseMutex();}
        }
        finally{if(request.Operation=="reserve"&&request.WaiverId is {} id)waivers.Invalidate(id);}
    }
    private Response HandleRequest(Request request)
    {
        if(request.Operation=="status")return StatusFor(request);
        if(request.Operation=="attempt")return Attempt(request);
        if(request.Operation=="waiver")
        {
            Refresh(forceSample:true);
            if(AdmissionBlockedReason() is {} reason)throw new IOException(reason);
            var items=request.Items??[];
            var binding=BindAuthority(request,items,consumeToken:false);
            ValidateStableSlots(request,items,binding);
            var decision=Decide(binding.Policy,ActiveBudgets(),items.Select(p=>ResourcePolicy.BudgetOf(p,p.PoolId is {} poolId?ledger.Pools.SingleOrDefault(pool=>pool.Id==poolId)?.MemoryLimit??binding.Policy.PoolMemoryLimit:null)).ToList());
            if(decision.Kind==AdmissionKind.Deny)waivers.InvalidatePlan(binding.Digest);
            return waivers.Issue(decision,binding.Revision,binding.Hash,binding.Digest,binding.Slots,sample?.At??DateTimeOffset.UtcNow,binding.ConfigurationIdentity) with{Memory=sample,Epoch=epoch,RootCpuRate=rootCpuRate};
        }
        if(request.Operation is "reserve" or "reserve-task")
        {
            if(request.WaiverId!=null)Refresh(forceSample:true);
            var items=request.Items??[];
            if(request.Operation=="reserve-task"&&ReplayTaskClaim(request,items) is {} replay)return replay;
            if(AdmissionBlockedReason() is {} earlyReason)throw new IOException(earlyReason);
            var binding=request.Operation=="reserve-task"?BindTaskAuthority(request,items):BindAuthority(request,items,consumeToken:request.WaiverId!=null);
            if(AdmissionBlockedReason() is {} reason)throw new IOException(reason);
            if(items.Length is <1 or >ResourcePolicy.MaxSlots||items.Select(i=>i.Id).Distinct().Count()!=items.Length)throw new IOException("启动项数量或 ID 无效。");
            var previous=ledger.Entries.Where(e=>items.Any(i=>i.Id==e.Plan.Id)).ToArray();
            if(previous.Length>0){if(request.Operation!="reserve-task"&&previous.Length==items.Length&&previous.All(e=>e.Digest==Store.Digest(items.Single(i=>i.Id==e.Plan.Id))))return new(true,"请求已记录",previous,sample,epoch);throw new IOException("启动 ID 已使用。");}
            // A retained claim owns one non-empty attempt identity. Exact replays returned above; any remaining reuse
            // (empty id, pruned history, or an unrelated entry with the same id) must fail before pools, entries or claims change.
            if(request.Operation=="reserve-task"&&items[0].Id==Guid.Empty)throw new IOException("队列预约缺少有效的尝试身份，未写入账本。");
            foreach(var item in items)
                foreach(var retainedClaim in ledger.TaskClaims)
                    if(retainedClaim.AttemptId==item.Id)throw new IOException($"尝试 {item.Id:N} 已由队列领取，不能重新预约；请核对原记录。");
            var active=ledger.Entries.Where(e=>!Store.IsTerminal(e)).ToList();
            foreach(var p in items)
            {
                if(p.Kind is not ("agent" or "git")||(p.Kind=="git"&&p.Budget!=ResourcePolicy.GitJobMemoryLimit)||(p.Kind=="agent"&&(p.Budget<ResourcePolicy.MinJobMemoryLimit||p.Budget>ResourcePolicy.MaxJobMemoryLimit))||!File.Exists(p.Command.Executable)||!p.Command.Executable.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new IOException("执行计划不支持。");
                if(p.RetryOf is {} sourceId)
                {
                    var source=ledger.Entries.SingleOrDefault(e=>e.Plan.Id==sourceId);
                    if(items.Length!=1||p.Kind!="agent"||source?.State is not ("FailedBeforeStart" or "Revoked"))throw new IOException("只有确认未运行的单项才能重试。");
                    if(!AttemptIdentity.CanRetryOutsideQueue(source.Plan,ledger.Entries.Select(e=>e.Plan),new TaskStore(Store.FilePath("tasks.json")).Load(),ledger.TaskClaims))
                        throw new IOException("该记录来自任务队列或来源尚待核对，请从任务卡重新入队，保留并发限制与尝试记录。");
                    if(ledger.Entries.Any(e=>e.Plan.RetryOf==sourceId))throw new IOException("该记录已经重试，请查看新尝试；不会重复提交。");
                    if(Store.Digest(p with{Id=source.Plan.Id,BatchId=source.Plan.BatchId,RetryOf=source.Plan.RetryOf})!=source.Digest)throw new IOException("重试计划与原项不同，请重新配置启动。");
                }
                if(PhysicalDirectory.Workspace(p.Cwd).Key!=p.Workspace.Key)throw new IOException("目录身份变化。");
                if(p.Kind=="git" && active.Any(e=>e.Plan.Kind=="git"))throw new IOException("已有 Git 准备正在运行，请稍后重试。");
                if(p.Kind=="agent" && active.Any(e=>e.Plan.Kind=="agent"&&PhysicalDirectory.Overlaps(e.Plan.Workspace,p.Workspace)&&(!p.Shared||!e.Plan.Shared)))throw new IOException("工作目录已被独立会话占用，请选择另一个工作目录。");
                active.Add(new(){Plan=p});
            }
            var reservedItems=ActiveBudgets();
            var requestedItems=items.Select(p=>ResourcePolicy.BudgetOf(p,p.PoolId is {} poolId?ledger.Pools.SingleOrDefault(pool=>pool.Id==poolId)?.MemoryLimit??binding.Policy.PoolMemoryLimit:null)).ToList();
            var decision=Decide(binding.Policy,reservedItems,requestedItems);
            if(decision.Kind==AdmissionKind.Deny)
            {
                waivers.InvalidatePlan(binding.Digest);
                throw new IOException(decision.Reason);
            }
            if(request.Operation!="reserve-task"&&(decision.Kind==AdmissionKind.RequireWaiver||request.WaiverId!=null))ValidateStableSlots(request,items,binding);
            if(decision.Kind==AdmissionKind.RequireWaiver&&binding.Consumed!=true)return new(false,"需要有效确认授权。",Memory:sample,Epoch:epoch,Decision:AdmissionKind.RequireWaiver.ToString(),RootCpuRate:rootCpuRate);
            var policy=binding.Policy;
            var poolIds=items.Where(i=>i.PoolId is not null).Select(i=>i.PoolId!.Value).Distinct().ToArray();
            if(poolIds.Length>0&&items.Select(i=>i.BatchId).Distinct().Count()>1)throw new IOException("只有同一批次才能共享资源池。");
            var addedPools=new List<PoolRecord>();
            foreach(var poolId in poolIds)
            {
                var record=ledger.Pools.SingleOrDefault(p=>p.Id==poolId);
                if(record==null)addedPools.Add(new(){Id=poolId,MemoryLimit=policy.PoolMemoryLimit,PolicyHash=binding.Hash,BatchId=items[0].BatchId});
                else if(record.MemoryLimit!=policy.PoolMemoryLimit||record.PolicyHash!=binding.Hash)throw new IOException("资源池策略已变化，请撤销后重新准备。");
            }
            var entries=items.Select(p=>new ControlEntry{Plan=p,Digest=Store.Digest(p),Epoch=epoch,Mode=policy.Mode,JobMemoryLimit=p.Kind=="git"?policy.GitJobMemoryLimit:p.Budget,PlanningBytes=ResourcePolicy.BudgetOf(p).PlanningBytes,PhysicalFloor=policy.PhysicalFloor,CommitFloor=policy.CommitFloor,CpuRate=rootCpuRate??policy.CpuRate,WaiverId=request.WaiverId,Pool=policy.PoolMode,PoolMemoryLimit=policy.PoolMode==ResourcePolicy.PoolShared?policy.PoolMemoryLimit:null}).ToArray();
            foreach(var candidate in entries)Ipc.EnsureEntryRepresentable(candidate);
            TaskReservationClaim? claim=request.Operation=="reserve-task"?new(request.TaskEntryId!.Value,items[0].Id):null;
            EnsureReservationCapacity(entries,addedPools.ToArray(),claim);
            try
            {
                foreach(var poolId in poolIds)EnsurePoolJob(poolId,policy.PoolMemoryLimit);
                ledger.Pools.AddRange(addedPools);ledger.Entries.AddRange(entries);
                if(claim!=null)ledger.TaskClaims.Add(claim);
                Save();
            }
            catch
            {
                ledger.Entries.RemoveAll(e=>entries.Contains(e));ledger.Pools.RemoveAll(p=>addedPools.Contains(p));
                if(claim!=null)ledger.TaskClaims.Remove(claim);
                foreach(var record in addedPools)if(pools.TryGetValue(record.Id,out var pool)&&pool.Members().Length==0){if(poolWatches.Remove(record.Id,out var watcher))watcher.Dispose();poolWatchErrors.Remove(record.Id);pool.Dispose();pools.Remove(record.Id);}
                throw;
            }
            return new(true,"已预约预算与工作目录",entries,sample,epoch);
        }
        if(request.Operation=="reconcile")return ReconcileRequests();
        var entry=ledger.Entries.SingleOrDefault(e=>e.Plan.Id==request.Attempt)??throw new IOException("启动请求不存在或已过期。");
        if(request.Operation=="revoke")
        {
            if(entry.State=="Pending"){var previousState=entry.State;var previousDetail=entry.Detail;entry.State="Revoked";entry.Detail="已撤销，迟到请求不会执行";try{Save();}catch{entry.State=previousState;entry.Detail=previousDetail;throw;}}
            return new(entry.State=="Revoked",entry.State=="Revoked"?entry.Detail:"已领取或可能运行，需继续核对",[entry],sample,epoch);
        }
        if(request.Operation is "begin" or "resume"
            &&(AuthorityBlockedReason()??(ledgerBroken?ledgerBrokenDetail:null)??blocked) is {} authorityReason)throw new IOException(authorityReason);
        if(request.Operation is "begin" or "resume")ValidateTaskProgram(entry);
        if(request.Operation=="begin")
        {
            if(entry.State!="Pending")throw new IOException("授权已撤销或被领取，不能重复启动。");
            if(UnrepresentableReason(ledger.Entries.Where(e=>!Store.IsTerminal(e)&&!Ipc.EntryRepresentable(e)).ToArray()) is {} reason)throw new IOException(reason);
            if(blocked!=null)throw new IOException(blocked);
            if(pressure)throw new IOException("资源压力尚未连续恢复，保留预约；可撤销此项。");
            var policy=PolicyFromEntry(entry);
            var beginDecision=Decide(policy,ActiveBudgets(),[],includeSoftBudget:false);
            if(beginDecision.Kind==AdmissionKind.Deny)throw new IOException(beginDecision.Reason+" 已保留预约，可撤销此项。");
            if(PhysicalDirectory.Workspace(entry.Plan.Cwd).Key!=entry.Plan.Workspace.Key)throw new IOException("目录物理身份已变化。");
            if(entry.Plan.PoolId is {} poolId)
            {
                if(!pools.TryGetValue(poolId,out var pool)){pool=new Job(Store.PoolName(poolId),openOnly:true);pools[poolId]=pool;}
                var poolLimit=pool.QueryMemory()?.LimitBytes??0;
                if(poolLimit==0)throw new IOException("资源池上限不可读，已停止创建。");
                jobs[entry.Plan.Id]=CreateItemJob(poolId,entry.Plan.Id,pool,poolLimit,entry.Plan.Budget);
            }
            else jobs[entry.Plan.Id]=new Job(Store.JobName(entry.Plan.Id),entry.Plan.Budget);
            entry.State="Creating";entry.Epoch=epoch;entry.Detail="正在创建进程";Save();return new(true,"单次创建授权",[entry],sample,epoch);
        }
        if(request.Operation=="resume")
        {
            if(entry.State!="Creating"||request.Epoch!=epoch||entry.Epoch!=epoch||request.Digest!=entry.Digest)throw new IOException("控制器代次或计划不符，请核对原尝试。");
            return new(true,"允许本次恢复线程",[entry],sample,epoch);
        }
        throw new IOException("未知协议操作。");
    }
    private Response Status()=>BuildStatus(null);
    private Response StatusFor(Request request)=>BuildStatus(request);
    private Response BuildStatus(Request? request)
    {
        var active=ledger.Entries.Where(e=>!Store.IsTerminal(e)).ToArray();
        var withheld=active.Where(entry=>!StatusEntry(entry).Representable).ToArray();
        var terminal=ledger.Entries.Where(Store.IsTerminal).TakeLast(40).Reverse();var kept=new List<ControlEntry>();long bytes=0;
        foreach(var entry in terminal){var size=StatusEntry(entry).Bytes;if(bytes+size>768*1024)break;kept.Add(entry);bytes+=size;}
        kept.Reverse();var omittedReason=UnrepresentableReason(withheld);
        var reason=omittedReason??AdmissionBlockedReason();
        var signature=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{Epoch=epoch,Blocked=blocked,Reason=reason,RootCpuRate=rootCpuRate,ReconcileOffered=reconcileOffered,Active=active.Select(e=>StatusEntry(e).Signature),History=kept.Select(e=>StatusEntry(e).Signature),Pools=ledger.Pools.Select(p=>new{p.Id,p.MemoryLimit})},Store.Json)));
        var unchanged=request?.StatusEpoch==epoch&&request.StatusSignature==signature;
        return new(blocked==null&&withheld.Length==0&&AuthorityBlockedReason()==null,reason??"监控器已核对",unchanged?null:active.Where(entry=>!withheld.Contains(entry)).Concat(kept).ToArray(),sample,epoch,reason,Unchanged:unchanged,StatusSignature:signature,RootCpuRate:rootCpuRate,ReconcileOffered:reconcileOffered,Pools:ledger.Pools.Count==0?null:ledger.Pools.ToArray());
    }
    // Fail-closed, user-confirmed release: settle only entries whose resource group provably no longer exists and whose receipt already proves the whole managed tree exited (Finished) or never ran (FailedBeforeStart). A dead recorded parent alone is not proof: jobs have no KILL_ON_JOB_CLOSE, so descendants may survive the handle loss and cannot be enumerated without it.
    private Response ReconcileRequests()
    {
        var settled=0;var changed=false;var refusals=new List<(ControlEntry Entry,string Reason)>();
        foreach(var e in ledger.Entries.Where(e=>!Store.IsTerminal(e)&&e.State!="Pending").ToArray())
        {
            using var gate=new Mutex(false,Store.AttemptLock(e.Plan.Id));bool own;
            try{own=gate.WaitOne(0);}catch(AbandonedMutexException){own=true;}
            if(!own){refusals.Add((e,"创建者仍在运行，不能证明无后代"));continue;}
            try
            {
                var jobGone=false;
                if(!jobs.ContainsKey(e.Plan.Id))
                {
                    try{using(var probe=new Job(Store.JobName(e.Plan.Id),openOnly:true)){}}
                    catch(System.ComponentModel.Win32Exception ex)when(ex.NativeErrorCode==2){jobGone=true;}
                    catch(System.ComponentModel.Win32Exception){/* Existence cannot be disproved; leave the entry to the normal recovery path. */}
                }
                if(!jobGone)continue;
                var receipt=ReadReceipt(e.Plan.Id);
                if(receipt==null){refusals.Add((e,"缺少回执，无法证明进程已退出"));continue;}
                if(receipt.Phase is "Finished" or "FailedBeforeStart")
                {
                    e.State=receipt.Phase=="Finished"?"Settled":"FailedBeforeStart";
                    e.Detail=$"经确认的核对释放：资源组不存在，回执阶段 {receipt.Phase}；{receipt.Detail??"无附加说明"}";
                    settled++;changed=true;continue;
                }
                if(receipt.Pid>0&&ProcessAlive(receipt.Pid,receipt.ProcessTime)){refusals.Add((e,$"记录的进程（PID {receipt.Pid}）仍在运行"));continue;}
                // A dead recorded parent is not tree proof: jobs have no KILL_ON_JOB_CLOSE, so the object can be gone while descendants survive, and without the handle they cannot be enumerated.
                refusals.Add((e,$"记录的父进程（PID {receipt.Pid}）已退出，但资源组句柄丢失使其派生进程无法枚举核对"));continue;
            }
            catch(Exception ex)when(ex is not OutOfMemoryException){refusals.Add((e,"核对失败："+ex.Message));}
            finally{gate.ReleaseMutex();}
        }
        if(changed)Save();
        Refresh(forceSample:true);
        foreach(var (entry,reason) in refusals)entry.Detail="核对释放被拒绝："+reason+"。";
        if(refusals.Count>0)Save();
        var snapshot=BuildStatus(null);
        var ok=refusals.Count==0&&snapshot.Ok;
        var message=settled>0||refusals.Count>0
            ?$"已核对释放 {settled} 条占用"+(refusals.Count>0?$"；{refusals.Count} 条保持阻塞："+string.Join("；",refusals.Select(r=>r.Reason)):"。")
            :"没有需要核对释放的占用。";
        if(!ok&&refusals.Count==0&&snapshot.AdmissionBlockedReason!=null)message+="当前仍阻塞："+snapshot.AdmissionBlockedReason;
        return snapshot with{Ok=ok,Message=message};
    }
    // PID reuse is not survival: the recorded start time (FILETIME UTC) must match exactly. When the start time cannot be read, treat the process as alive and refuse release. Only used to refine the refusal reason; a dead parent never proves the tree exited.
    private static bool ProcessAlive(int pid,long processTimeUtc)
    {
        try
        {
            using var process=Process.GetProcessById(pid);
            if(process.HasExited)return false;
            return process.StartTime.ToUniversalTime().ToFileTimeUtc()==processTimeUtc;
        }
        catch(ArgumentException){return false;}
        catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){return true;}
    }
    private readonly record struct StatusFields(LaunchItem Plan,string State,string Epoch,string Digest,DateTimeOffset Created,string Detail,string? Mode,ulong? JobMemoryLimit,ulong? PlanningBytes,ulong? PhysicalFloor,ulong? CommitFloor,int? CpuRate,Guid? WaiverId);
    private sealed record StatusEntryCache(StatusFields Fields,string Signature,long Bytes,bool Representable);
    private StatusEntryCache StatusEntry(ControlEntry entry)
    {
        var fields=new StatusFields(entry.Plan,entry.State,entry.Epoch,entry.Digest,entry.Created,entry.Detail,entry.Mode,entry.JobMemoryLimit,entry.PlanningBytes,entry.PhysicalFloor,entry.CommitFloor,entry.CpuRate,entry.WaiverId);
        if(statusEntryCache.TryGetValue(entry.Plan.Id,out var cached)&&cached.Fields==fields)return cached;
        var data=JsonSerializer.SerializeToUtf8Bytes(entry,Store.Json);
        cached=new(fields,Convert.ToHexString(SHA256.HashData(data)),data.LongLength,Ipc.EntryRepresentable(entry));
        statusEntryCache[entry.Plan.Id]=cached;return cached;
    }
    private static string? UnrepresentableReason(ControlEntry[] entries)
    {
        if(entries.Length==0)return null;
        // Names/arguments may themselves be oversized. Only bounded IDs belong in the response header.
        return $"{entries.Length} 条活动记录超出单帧边界，已暂停新启动；本次状态省略 ID：{string.Join("、",entries.Take(4).Select(e=>e.Plan.Id.ToString("N")))}。请人工核对 control.json 及备份；其他记录仍可撤销。";
    }
    private string PoolMarker(Guid id,string suffix)=>Store.FilePath(Path.Combine("pools",id.ToString("N")+"."+suffix));
    private Job EnsurePoolJob(Guid id,ulong limit)
    {
        if(pools.TryGetValue(id,out var cached))return cached;
        try{pools[id]=new Job(Store.PoolName(id),openOnly:true);WatchPool(id,pools[id]);return pools[id];}
        catch(System.ComponentModel.Win32Exception e)when(e.NativeErrorCode==2){}
        var ready=PoolMarker(id,"ready");var release=PoolMarker(id,"release");
        Directory.CreateDirectory(Path.GetDirectoryName(ready)!);
        File.Delete(ready);File.Delete(release);
        using var helper=new ProtectedProcess(new(Store.WorkerPath,["pool-host",Store.PoolName(id),"",ready,release,"1",limit.ToString(System.Globalization.CultureInfo.InvariantCulture)],"exe"),Store.Root,[root!]);
        helper.Resume();
        WaitForPool(ready,id);
        var pool=new Job(Store.PoolName(id),openOnly:true);
        File.WriteAllText(release,"1");
        helper.Process.WaitForExit(5000);
        pools[id]=pool;WatchPool(id,pool);
        return pool;
    }
    private void WatchPool(Guid id,Job pool)
    {
        if(poolWatches.ContainsKey(id))return;
        try{poolWatches[id]=new JobMemoryWatch(pool);poolWatchErrors.Remove(id);}
        catch(System.ComponentModel.Win32Exception e){poolWatchErrors[id]=e.Message;}
    }
    private void DrainPoolLimits()
    {
        foreach(var (id,watcher) in poolWatches)
        {
            try{foreach(var pid in watcher.Drain())Store.Save(Store.FilePath(Path.Combine("pools",id.ToString("N")+".limit.json")),new PoolLimitNotice(id,pid,DateTimeOffset.UtcNow));}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){poolWatchErrors[id]=e.Message;}
        }
    }
    private Job CreateItemJob(Guid poolId,Guid itemId,Job pool,ulong limit,ulong itemLimit)
    {
        var ready=PoolMarker(itemId,"ready");var release=PoolMarker(itemId,"release");
        Directory.CreateDirectory(Path.GetDirectoryName(ready)!);
        File.Delete(ready);File.Delete(release);
        using var helper=new ProtectedProcess(new(Store.WorkerPath,["pool-host",Store.PoolName(poolId),Store.JobName(itemId),ready,release,"0",limit.ToString(System.Globalization.CultureInfo.InvariantCulture),itemLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)],"exe"),Store.Root,[root!,pool]);
        helper.Resume();
        WaitForPool(ready,itemId);
        var item=new Job(Store.JobName(itemId),openOnly:true);
        File.WriteAllText(release,"1");
        helper.Process.WaitForExit(5000);
        return item;
    }
    private static void WaitForPool(string ready,Guid id)
    {
        var watch=Stopwatch.StartNew();string? text=null;
        while(text==null)
        {
            try{using var stream=new FileStream(ready,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(stream);text=reader.ReadToEnd();}
            catch(IOException)
            {
                if(watch.ElapsedMilliseconds>10000)throw new IOException("资源池创建超时："+id.ToString("N"));
                Thread.Sleep(20);
            }
        }
        if(text.StartsWith("error:",StringComparison.Ordinal))throw new IOException("资源池创建失败："+text[6..]);
    }
    private string? AuthorityBlockedReason()
    {
        try{LoadAuthority();return null;}
        catch(Exception e)when(e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException){return e.Message;}
    }
    private string? AdmissionBlockedReason()=>AuthorityBlockedReason()??UnrepresentableReason(ledger.Entries.Where(e=>!Store.IsTerminal(e)&&!StatusEntry(e).Representable).ToArray())??blocked??rootCpuReason??(pressure?"资源压力尚未连续恢复，稍后重新检查。":null);
    public void Dispose(){foreach(var watch in poolWatches.Values)watch.Dispose();poolWatches.Clear();poolWatchErrors.Clear();foreach(var j in jobs.Values)j.Dispose();jobs.Clear();foreach(var j in pools.Values)j.Dispose();pools.Clear();receiptCache.Clear();statusEntryCache.Clear();root?.Dispose();}
    private List<BudgetItem> ActiveBudgets()
    {
        var items=ledger.Entries.Where(e=>!Store.IsTerminal(e)).Select(e=>ResourcePolicy.BudgetOf(e.Plan,PoolLimitOf(e.Plan.PoolId))).ToList();
        foreach(var record in ledger.Pools)
        {
            if(ledger.Entries.Any(e=>!Store.IsTerminal(e)&&e.Plan.PoolId==record.Id))continue;
            var limit=(pools.TryGetValue(record.Id,out var pool)?pool.QueryMemory()?.LimitBytes:null)??record.MemoryLimit;
            if(limit==0)limit=record.MemoryLimit;
            if(limit==0)continue;
            items.Add(new(record.Id,"agent",ResourcePolicy.DefaultPlanningBytes,limit,record.Id,limit));
        }
        return items;
    }
    private ulong? PoolLimitOf(Guid? poolId)=>poolId is {} id?ledger.Pools.SingleOrDefault(p=>p.Id==id)?.MemoryLimit:null;
    private sealed record AuthorityBinding(ResourcePolicySnapshot Policy,string Hash,string Digest,long Revision,Guid[] Slots,bool? Consumed,Draft? Draft,string ConfigurationIdentity);
    private (ResourceSettings Settings,long Revision,Draft? Draft,string Identity) LoadAuthority(bool bind=false)
    {
        if(!File.Exists(configurationPath))
        {
            if(File.Exists(configurationPath+".bak"))throw new IOException("主配置丢失但存在 .bak 备份，已拒绝准入；请人工核对并恢复 configuration.json。");
            return (new ResourceSettings(),0,null,"missing");
        }
        var config=Store.Read<Configuration>(configurationPath)??throw new IOException("配置无法读取，已拒绝准入。");
        config=ConfigurationCompatibility.Accept(config);
        var identity="";
        if(bind)
        {
            var info=new FileInfo(configurationPath);
            identity=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(config,Store.Json)))+":"+info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return (config.Draft.Resource,config.Draft.Revision,config.Draft,identity);
    }
    private ResourcePolicySnapshot AuthorityPolicy()
    {
        var (settings,_,_,_)=LoadAuthority();
        if(sample==null)throw new IOException("资源读数不可用，暂停创建。");
        ResourcePolicy.ValidateSettings(settings,sample.Total,sample.CommitLimit);
        return ResourcePolicy.SnapshotFor(settings,sample.Total,sample.CommitLimit);
    }
    private Response? ReplayTaskClaim(Request request,LaunchItem[] items)
    {
        if(request.TaskEntryId is not {} entryId||entryId==Guid.Empty||items.Length!=1||items[0] is null||request.WaiverId!=null)
            throw new IOException("队列预约缺少单项授权身份，或包含不支持的确认令牌。");
        var claim=ledger.TaskClaims.SingleOrDefault(candidate=>candidate.EntryId==entryId);
        if(claim is null)return null;
        if(claim.AttemptId!=items[0].Id)throw new IOException($"该队列项已由尝试 {claim.AttemptId:N} 领取，不能另行预约；请核对原记录。");
        var prior=ledger.Entries.SingleOrDefault(entry=>entry.Plan.Id==claim.AttemptId);
        if(prior is null)throw new IOException($"该队列项已领取，尝试 {claim.AttemptId:N} 的历史已裁剪；不会重新执行。");
        var policy=PolicyFromEntry(prior);
        if(prior.Digest!=Store.Digest(items[0])||request.Policy!=policy||request.PolicyHash!=ResourcePolicy.PolicyHash(policy)
            ||request.PlanDigest!=ResourcePolicy.PlanDigest(items))throw new IOException("重试与已领取队列项的原计划或策略不一致。");
        return new(true,"请求已记录",[prior],sample,epoch);
    }

    private void ValidateTaskProgram(ControlEntry entry)
    {
        var claim=ledger.TaskClaims.SingleOrDefault(candidate=>candidate.AttemptId==entry.Plan.Id);
        if(claim is null)return;
        var book=new TaskStore(Store.FilePath("tasks.json")).Load();
        var run=book.Attempts.SingleOrDefault(candidate=>candidate.AttemptId==entry.Plan.Id);
        string fingerprint;
        if(run is not null)
        {
            if(run.Terminal||!AttemptIdentity.Matches(run,entry))throw new IOException("任务尝试身份或状态已变化，已保留预约，请核对原记录。");
            fingerprint=run.ProgramFingerprint;
        }
        else
        {
            var queued=book.Queue.SingleOrDefault(candidate=>candidate.Id==claim.EntryId);
            if(queued?.Deferred==true)throw new IOException("排队项已暂缓，不能使用旧预约启动；请核对原记录。");
            if(queued is null||queued.TaskId!=entry.Plan.SourceSlotId||queued.Command.Executable!=entry.Plan.Command.Executable
                ||queued.Command.Adapter!=entry.Plan.Command.Adapter||!queued.Command.Arguments.SequenceEqual(entry.Plan.Command.Arguments))
                throw new IOException("任务程序的冻结记录缺失或已变化，已保留预约，请核对后重新入队。");
            fingerprint=queued.ProgramFingerprint;
        }
        if(AgentProbe.Fingerprint(entry.Plan.Command)!=fingerprint)throw new IOException("任务程序已升级或更换，不能使用旧预约启动；请核对后重新入队。");
    }

    private AuthorityBinding BindTaskAuthority(Request request,LaunchItem[] items)
    {
        if(sample==null||request.TaskEntryId is not {} entryId||items.Length!=1||request.WaiverId!=null)
            throw new IOException("队列预约缺少单项授权身份，或包含不支持的确认令牌。");
        if(ledger.TaskClaims.Count>=LedgerMigration.MaxTaskClaims)throw new IOException("队列领取记录已达保留上限，已停止新增预约；不会删除领取证据以重放任务。");
        var book=new TaskStore(Store.FilePath("tasks.json")).Load();
        var queued=book.Queue.SingleOrDefault(entry=>entry.Id==entryId)??throw new IOException("队列项已移除或执行，拒绝重复预约。");
        if(queued.Deferred)throw new IOException("排队项已暂缓，请先明确恢复排队。");
        var task=book.Tasks.Single(entry=>entry.Id==queued.TaskId);
        // A pre-claim ledger cannot prove which queue entry owns a still-unrecorded task reservation. Never infer a new authorization.
        if(ledger.Entries.Any(entry=>entry.Plan.SourceSlotId==task.Id
            &&!ledger.TaskClaims.Any(claim=>claim.AttemptId==entry.Plan.Id)
            &&!book.Attempts.Any(run=>run.AttemptId==entry.Plan.Id)))
            throw new IOException("该任务存在旧版未登记的预约，无法证明队列领取身份；请核对原记录，禁止自动重放。");
        var plan=items[0];
        if(task.Revision!=queued.TaskRevision||plan.SourceSlotId!=task.Id||plan.Kind!="agent"||plan.AgentId!=queued.AgentId||plan.RetryOf!=null
            ||plan.Shared!=(queued.DirectoryMode=="shared")||plan.Command.Executable!=queued.Command.Executable||plan.Command.Adapter!=queued.Command.Adapter
            ||!plan.Command.Arguments.SequenceEqual(queued.Command.Arguments)||AgentProbe.Fingerprint(plan.Command)!=queued.ProgramFingerprint)
            throw new IOException("队列预约与冻结的任务或程序不一致。");
        if(book.Attempts.Count(run=>!run.Terminal)>=book.MaxRunning)throw new IOException("队列并发上限已满。");
        var source=PhysicalDirectory.Workspace(PhysicalDirectory.Resolve(queued.Project).Path);
        if(queued.QuickLaunch is {} quick&&source.Key!=quick.WorkspaceKey)throw new IOException("队列源目录物理身份已变化。");
        var resume=QueuePolicy.CanDispatchResume(book,queued,source.Key,queued.ProgramFingerprint);
        if(!resume.Allowed)throw new IOException(resume.Reason);
        if(queued.QuickLaunch is {IsolateWorkspace:true} isolated)
        {
            var root=PhysicalDirectory.Resolve(string.IsNullOrWhiteSpace(isolated.WorktreeRoot)?Store.FilePath("worktrees"):isolated.WorktreeRoot).Path.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(plan.Workspace.Key==source.Key||!plan.Workspace.Path.StartsWith(root,StringComparison.OrdinalIgnoreCase))
                throw new IOException("队列独立目录不属于已确认的工作目录根。");
        }
        else if(plan.Workspace.Key!=source.Key||PhysicalDirectory.Resolve(plan.Cwd).Key!=PhysicalDirectory.Resolve(queued.Project).Key)
            throw new IOException("队列预约工作目录与冻结目录不一致。");
        ResourcePolicy.ValidateSettings(queued.Resource,sample.Total,sample.CommitLimit);
        var policy=ResourcePolicy.SnapshotFor(queued.Resource,sample.Total,sample.CommitLimit);
        if(rootCpuRate is {} cpu&&cpu!=policy.CpuRate)throw new IOException("队列 CPU 配置与当前根 Job 不一致，现有 Job 不会修改。");
        var hash=ResourcePolicy.PolicyHash(policy);var digest=ResourcePolicy.PlanDigest(items);
        if(request.Policy!=policy||request.PolicyHash!=hash||request.PlanDigest!=digest)throw new IOException("队列策略或计划摘要与冻结授权不一致。");
        ResourcePolicy.ValidateLaunchItem(plan,policy,sample.Total);
        return new(policy,hash,digest,queued.TaskRevision,[task.Id],false,null,"queue:"+queued.Id.ToString("N"));
    }

    private AuthorityBinding BindAuthority(Request request,LaunchItem[] items,bool consumeToken)
    {
        if(sample==null)throw new IOException("资源读数不可用，暂停创建。");
        if(items.Length is <1 or >ResourcePolicy.MaxSlots||items.Any(i=>i==null)||items.Select(i=>i.Id).Distinct().Count()!=items.Length)throw new IOException("启动项数量或 ID 无效。");
        var (settings,revision,draft,identity)=LoadAuthority(bind:true);
        ResourcePolicy.ValidateSettings(settings,sample.Total,sample.CommitLimit);
        var policy=ResourcePolicy.SnapshotFor(settings,sample.Total,sample.CommitLimit);
        var hash=ResourcePolicy.PolicyHash(policy);var digest=ResourcePolicy.PlanDigest(items);
        var slots=draft?.Slots.Where(s=>s.Enabled).Select(s=>s.Id).ToArray()??[];
        waivers.InvalidateMismatched(revision,hash,identity);
        bool? consumed=null;
        if(consumeToken&&request.WaiverId is {} id)consumed=waivers.Consume(id,revision,hash,digest,slots,identity);
        if(!string.IsNullOrWhiteSpace(request.PolicyHash)&&request.PolicyHash!=hash)throw new IOException("策略快照与当前配置不一致。");
        if(!string.IsNullOrWhiteSpace(request.PlanDigest)&&request.PlanDigest!=digest)throw new IOException("计划摘要与当前启动项不一致。");
        if(request.DraftRevision is {} clientRevision&&clientRevision!=revision)throw new IOException("草稿修订号与当前配置不一致。");
        foreach(var item in items)ResourcePolicy.ValidateLaunchItem(item,policy,sample.Total);
        return new(policy,hash,digest,revision,slots,consumed,draft,identity);
    }
    private void ValidateStableSlots(Request request,LaunchItem[] items,AuthorityBinding binding)
    {
        var draft=binding.Draft??throw new IOException("确认授权需要先保存当前配置并重新准备启动计划。");
        if(request.EnabledSlotIds!=null&&!request.EnabledSlotIds.SequenceEqual(binding.Slots))throw new IOException("启用槽集合与当前配置不一致，请重新检查资源。");
        if(items.Length==1&&items[0].Kind=="git"&&items[0].RetryOf==null)return;
        if(items.Any(i=>i.Kind!="agent"||i.SourceSlotId==null||i.SourceSlotId==Guid.Empty))throw new IOException("确认授权缺少稳定来源槽，请重新准备启动计划。");
        foreach(var item in items)
        {
            var index=draft.Slots.FindIndex(slot=>slot.Id==item.SourceSlotId);
            if(index<0||!draft.Slots[index].Enabled||item.Position!=index+1||item.AgentId!=draft.Slots[index].AgentId)throw new IOException("启动项的稳定来源槽或 Agent 与当前配置不一致。");
        }
        if(items.Length==1&&items[0].RetryOf is {} sourceId)
        {
            var source=ledger.Entries.SingleOrDefault(e=>e.Plan.Id==sourceId);
            if(source==null||source.Plan.SourceSlotId!=items[0].SourceSlotId||source.Plan.AgentId!=items[0].AgentId)throw new IOException("重试只能使用原来的稳定槽，请重新配置启动。");
        }
        else if(items.Any(i=>i.RetryOf!=null)||!items.Select(i=>i.SourceSlotId!.Value).SequenceEqual(binding.Slots))throw new IOException("启动项必须与当前配置的有序启用槽集合一致。");
    }
    private ResourcePolicySnapshot PolicyFromEntry(ControlEntry entry)
    {
        var settings=new ResourceSettings
        {
            Mode=entry.Mode??entry.Plan.ResourceMode??"practical",
            JobMemoryLimit=entry.JobMemoryLimit??entry.Plan.Budget,
            PlanningBytes=entry.PlanningBytes??entry.Plan.PlanningBytes??ResourcePolicy.DefaultPlanningBytes,
            PhysicalFloorOverride=entry.PhysicalFloor,
            CommitFloorOverride=entry.CommitFloor,
            CpuRate=entry.CpuRate??ResourcePolicy.DefaultCpuRate,
            Pool=entry.Pool??ResourcePolicy.PoolIndependent,
            PoolMemoryLimit=entry.PoolMemoryLimit
        };
        return ResourcePolicy.SnapshotFor(settings,sample?.Total??0,sample?.CommitLimit??0);
    }
    private AdmissionDecision Decide(ResourcePolicySnapshot policy,IReadOnlyList<BudgetItem> reserved,IReadOnlyList<BudgetItem> requested,bool includeSoftBudget=true)
    {
        if(sample==null)return new(AdmissionKind.Deny,"资源读数不可用，暂停创建。",0,0,0,0,0,policy.PhysicalFloor,policy.CommitFloor,false);
        return ResourcePolicy.Evaluate(sample,policy,reserved,requested,includeSoftBudget);
    }
    private static int RootCpuRate()
    {
        try
        {
            var path=Store.FilePath("configuration.json");
            var config=Store.Read<Configuration>(path);
            if(config==null)return ResourcePolicy.DefaultCpuRate;
            config=ConfigurationCompatibility.Accept(config);
            return config.Draft.Resource.Mode=="custom"?config.Draft.Resource.CpuRate:ResourcePolicy.DefaultCpuRate;
        }
        catch(Exception e)when(e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return ResourcePolicy.DefaultCpuRate;
        }
    }
}
