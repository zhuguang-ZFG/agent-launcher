using System.Diagnostics;
using System.IO.Pipes;
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
            if(args.Length==2 && args[0] is "pane" or "run" && Guid.TryParse(args[1],out var id))return RunPane(id,args[0]=="pane");
            Console.Error.WriteLine("用法: monitor | pane <attempt-guid>");return 2;
        }
        catch(Exception e)when(e is not OutOfMemoryException){Console.Error.WriteLine("启动器: "+e.Message);return 1;}
    }
    private static int RunPane(Guid id,bool interactive)
    {
        using var mutex=new Mutex(false,Store.AttemptLock(id));bool owned;
        try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
        if(!owned)throw new IOException("该启动请求正在处理。");
        ProtectedProcess? child=null;Job? root=null;Job? job=null;var resumeIntent=false;
        try
        {
            var claim=Path.Combine(Store.Root,"attempts",id.ToString("N"),"claim");Directory.CreateDirectory(Path.GetDirectoryName(claim)!);
            using(var file=new FileStream(claim,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){file.WriteByte(1);file.Flush(true);}
#if INTEGRATION_TESTS
            if(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAIL_AT")=="BeforeBegin")Environment.Exit(97);
#endif
            var grant=Ipc.SendAsync(new("begin",Guid.NewGuid(),Attempt:id)).GetAwaiter().GetResult();
            if(!grant.Ok||grant.Entries is not {Length:1})throw new IOException(grant.Message);
            var entry=grant.Entries[0];var plan=entry.Plan;
            root=new Job(Store.RootJob,openOnly:true);job=new Job(Store.JobName(id),openOnly:true);
            if(PhysicalDirectory.Workspace(plan.Cwd).Key!=plan.Workspace.Key)throw new IOException("工作目录物理身份变化，已阻止启动。");
            if(interactive)TerminalAppearance.WritePaneHeading(plan);
            Write("SpawnIntent",0,0,null,"正在受保护地创建进程");
            child=new ProtectedProcess(plan.Command,plan.Cwd,root,job,redirected:!interactive);
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
        catch
        {
            if(child!=null&&!resumeIntent){child.AbortBeforeResume();Write("FailedBeforeStart",child.Process.Id,child.CreationTime,null,"未运行的进程已回收");}
            child?.Dispose();job?.Dispose();root?.Dispose();throw;
        }
        finally{mutex.ReleaseMutex();}
        try
        {
            child!.Process.WaitForExit();var exit=child.Process.ExitCode;
            Write("Exited",child.Process.Id,child.CreationTime,exit,$"进程已退出，退出码 {exit}");
            while(job!.Members().Length>0)Thread.Sleep(1000);
            Write("Finished",child.Process.Id,child.CreationTime,exit,$"全部受管进程已退出，退出码 {exit}");
#if !INTEGRATION_TESTS
            if(interactive){Console.WriteLine($"\nAgent 已退出（{exit}）。工作目录已保留。按 Enter 关闭此窗格。");Console.ReadLine();}
#endif
            return exit;
        }
        finally{child?.Dispose();job?.Dispose();root?.Dispose();}
        void Write(string phase,int pid,long time,int? code,string detail)
        {
            Store.Save(Store.ReceiptPath(id),new Receipt(phase,pid,time,code,detail,DateTimeOffset.UtcNow));
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
    private readonly string epoch=Guid.NewGuid().ToString("N");
    private readonly string ledgerPath=Store.FilePath("control.json");
    private Ledger ledger=new();
    private Job? root;
    private MemorySnapshot? sample;
    private DateTime lastActivity=DateTime.UtcNow;
    private string? blocked;
    private bool ledgerBroken;
    private readonly PressureTracker pressureTracker=new();
    private bool pressure=>pressureTracker.Pressure;
    private readonly WaiverTable waivers=new(()=>DateTimeOffset.UtcNow);
    private static readonly TimeSpan IdleTimeout=TimeSpan.FromSeconds(4);
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
            try{ledger=Store.Read<Ledger>(ledgerPath)??new();if(ledger.SchemaVersion!=1||ledger.Entries==null||ledger.Entries.Any(e=>e==null||e.Plan==null))throw new InvalidDataException();}
            catch(Exception e)when(e is JsonException or InvalidDataException or IOException){ledgerBroken=true;ledger=new();blocked="控制账本损坏或版本不支持，已停止启动与回收。请人工核对 control.json，必要时用 .bak 备份恢复。";}
            var boot=Store.BootId();
            if(!ledgerBroken&&ledger.BootId!=boot){foreach(var e in ledger.Entries.Where(e=>!Store.IsTerminal(e))){e.State="Settled";e.Detail="系统已重新启动，未重放旧请求";}ledger.BootId=boot;Save();}
            root=new Job(Store.RootJob,root:true,cpuRate:RootCpuRate());
            if(!root.CreatedNew){var actual=root.QueryCpuRate();/* Existing aggregate root keeps its original limits; do not rewrite them. */}
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
                        if(!ledgerBroken&&blocked==null&&jobs.Count==0&&ledger.Entries.All(Store.IsTerminal)&&DateTime.UtcNow-lastActivity>=IdleTimeout)
                        {
                            // Give a simultaneous poll one final chance instead of stranding it on a disposed listener.
                            if(accept.IsCompleted||Task.WhenAny(accept,Task.Delay(250)).GetAwaiter().GetResult()==accept){connected=true;break;}
                            return 0;
                        }
                    }
                    lastActivity=DateTime.UtcNow;
                    using var deadline=new CancellationTokenSource(3000);
                    try
                    {
                        var request=Ipc.ReadAsync<Request>(pipe,deadline.Token).GetAwaiter().GetResult();Refresh();
                        Response response;
                        try{response=Handle(request);}catch(Exception e)when(e is not OutOfMemoryException){response=new(false,e.Message);}
                        Ipc.WriteResponseAsync(pipe,response,deadline.Token).GetAwaiter().GetResult();
                    }
                    catch(Exception e)when(e is IOException or OperationCanceledException or JsonException){/* Peer failure never grants additional authorization. */}
                }
            }
        }
        finally{Dispose();single.ReleaseMutex();}
    }
    private void Save()
    {
        PruneLedger();
        Store.Save(ledgerPath,ledger);
    }
    private void PruneLedger()
    {
        // Terminal history is convenience only. Keep the published ledger well under Store.Read's 8 MiB bound: an unreadable
        // control.json sets ledgerBroken and refuses every launch until the file is repaired by hand. Pruning drops the oldest
        // terminal records first and never touches active ones, which cannot exceed the budget on their own (at most sixteen agent
        // plans plus one Git plan, each already bounded to a single frame by EnsureEntryRepresentable).
        var total=JsonSerializer.SerializeToUtf8Bytes(ledger,Store.Json).LongLength;
        if(total<=LedgerBudget)return;
        foreach(var entry in ledger.Entries.Where(Store.IsTerminal).ToList())
        {
            if(total<=LedgerBudget)return;
            total-=JsonSerializer.SerializeToUtf8Bytes(entry,Store.Json).LongLength+1;
            ledger.Entries.Remove(entry);
        }
    }
    private void Refresh(bool forceSample=false)
    {
        if(forceSample||sample==null||DateTimeOffset.UtcNow-sample.At>=TimeSpan.FromSeconds(2))
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
        var changed=false;blocked=ledgerBroken?"控制账本损坏或版本不支持，已停止启动与回收。请人工核对 control.json，必要时用 .bak 备份恢复。":null;
        foreach(var e in ledger.Entries.Where(e=>!Store.IsTerminal(e)&&e.State!="Pending"))
        {
            using var gate=new Mutex(false,Store.AttemptLock(e.Plan.Id));bool own;
            try{own=gate.WaitOne(0);}catch(AbandonedMutexException){own=true;}
            if(!own)continue;
            try
            {
                Job? j=null;
                if(!jobs.TryGetValue(e.Plan.Id,out j))
                {
                    try{jobs[e.Plan.Id]=j=new Job(Store.JobName(e.Plan.Id),openOnly:true);}
                    catch(System.ComponentModel.Win32Exception ex)when(ex.NativeErrorCode==2){/* Last handle may be gone while processes still exist; absence is not exit evidence. */}
                }
                var receipt=Store.Read<Receipt>(Store.ReceiptPath(e.Plan.Id));
                if(j==null && receipt?.Phase is not ("Finished" or "FailedBeforeStart")) {blocked="资源组句柄丢失，无法证明后代已退出；保留预算，系统重启后可核对释放。";e.Detail=blocked;continue;}
                var members=j?.Members()??[];
                if(members.Length==0){e.State=receipt?.Phase is "Started" or "Exited" or "ResumeIntent" or "Finished"?"Settled":"FailedBeforeStart";e.Detail=receipt?.Detail??"创建者已退出，核对无存活进程";changed=true;continue;}
                if(receipt?.Phase is "SpawnIntent" or "CreatedSuspended")
                {
                    // Gate proves no creator is running; no persisted ResumeIntent means our children never ran.
                    foreach(var pid in members){using var p=Process.GetProcessById(pid);Native.Check(Native.IsProcessInJob(p.Handle,j!.Handle,out var member));if(!member)throw new IOException("进程归属变化");Native.Check(Native.TerminateProcess(p.Handle,1));p.WaitForExit(2000);}
                    e.State="FailedBeforeStart";e.Detail="已回收创建中断的未运行进程";changed=true;
                }
                else {e.Detail=receipt?.Phase=="Exited"?"Agent 已退出，工具仍运行":receipt?.Phase=="Started"?"已启动进程（不代表任务完成）":"结果未确认；占用保留";}
            }
            catch(Exception ex)when(ex is not OutOfMemoryException){blocked="恢复核对失败："+ex.Message;e.Detail=blocked;}
            finally{gate.ReleaseMutex();}
        }
        if(changed)Save();
        foreach(var id in jobs.Keys.Where(id=>Store.IsTerminal(ledger.Entries.Single(e=>e.Plan.Id==id))).ToArray()){jobs[id].Dispose();jobs.Remove(id);}
        var known=jobs.Values.SelectMany(j=>j.Members()).ToHashSet();
        if(root!.Members().Any(pid=>!known.Contains(pid)))blocked="发现资源组内尚未归属的进程，暂停新启动。";
    }
    private Response Handle(Request request)
    {
        if(request.Operation=="status")return Status();
        if(request.Operation=="waiver")
        {
            Refresh(forceSample:true);
            if(AdmissionBlockedReason() is {} reason)throw new IOException(reason);
            var items=request.Items??[];
            var binding=BindAuthority(request,items,consumeToken:false);
            var decision=Decide(binding.Policy,ActiveBudgets(),items.Select(ResourcePolicy.BudgetOf).ToList());
            if(decision.Kind==AdmissionKind.Deny)waivers.InvalidatePlan(binding.Digest);
            return waivers.Issue(decision,binding.Revision,binding.Hash,binding.Digest,binding.Slots,sample?.At??DateTimeOffset.UtcNow) with{Memory=sample,Epoch=epoch};
        }
        if(request.Operation=="reserve")
        {
            if(request.WaiverId!=null)Refresh(forceSample:true);
            var items=request.Items??[];
            var binding=BindAuthority(request,items,consumeToken:request.WaiverId!=null);
            if(AdmissionBlockedReason() is {} reason)throw new IOException(reason);
            if(items.Length is <1 or >ResourcePolicy.MaxSlots||items.Select(i=>i.Id).Distinct().Count()!=items.Length)throw new IOException("启动项数量或 ID 无效。");
            var previous=ledger.Entries.Where(e=>items.Any(i=>i.Id==e.Plan.Id)).ToArray();
            if(previous.Length>0){if(previous.Length==items.Length&&previous.All(e=>e.Digest==Store.Digest(items.Single(i=>i.Id==e.Plan.Id))))return new(true,"请求已记录",previous,sample,epoch);throw new IOException("启动 ID 已使用。");}
            var active=ledger.Entries.Where(e=>!Store.IsTerminal(e)).ToList();
            foreach(var p in items)
            {
                if(p.Kind is not ("agent" or "git")||(p.Kind=="git"&&p.Budget!=ResourcePolicy.GitJobMemoryLimit)||(p.Kind=="agent"&&(p.Budget<ResourcePolicy.MinJobMemoryLimit||p.Budget>ResourcePolicy.MaxJobMemoryLimit))||!File.Exists(p.Command.Executable)||!p.Command.Executable.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new IOException("执行计划不支持。");
                if(p.RetryOf is {} sourceId)
                {
                    var source=ledger.Entries.SingleOrDefault(e=>e.Plan.Id==sourceId);
                    if(items.Length!=1||p.Kind!="agent"||source?.State is not ("FailedBeforeStart" or "Revoked"))throw new IOException("只有确认未运行的单项才能重试。");
                    if(ledger.Entries.Any(e=>e.Plan.RetryOf==sourceId))throw new IOException("该记录已经重试，请查看新尝试；不会重复提交。");
                    if(Store.Digest(p with{Id=source.Plan.Id,BatchId=source.Plan.BatchId,RetryOf=source.Plan.RetryOf})!=source.Digest)throw new IOException("重试计划与原项不同，请重新配置启动。");
                }
                if(PhysicalDirectory.Workspace(p.Cwd).Key!=p.Workspace.Key)throw new IOException("目录身份变化。");
                if(p.Kind=="git" && active.Any(e=>e.Plan.Kind=="git"))throw new IOException("已有 Git 准备正在运行，请稍后重试。");
                if(p.Kind=="agent" && active.Any(e=>e.Plan.Kind=="agent"&&PhysicalDirectory.Overlaps(e.Plan.Workspace,p.Workspace)&&(!p.Shared||!e.Plan.Shared)))throw new IOException("工作目录已被独立会话占用，请选择另一个工作目录。");
                active.Add(new(){Plan=p});
            }
            foreach(var item in items)ResourcePolicy.ValidateLaunchItem(item,binding.Policy,sample!.Total);
            var reservedItems=ActiveBudgets();
            var requestedItems=items.Select(ResourcePolicy.BudgetOf).ToList();
            var decision=Decide(binding.Policy,reservedItems,requestedItems);
            if(decision.Kind==AdmissionKind.Deny)
            {
                waivers.InvalidatePlan(binding.Digest);
                throw new IOException(decision.Reason);
            }
            if(decision.Kind==AdmissionKind.RequireWaiver && binding.Consumed!=true)throw new IOException("需要有效确认授权。");
            var policy=binding.Policy;
            var entries=items.Select(p=>new ControlEntry{Plan=p,Digest=Store.Digest(p),Epoch=epoch,Mode=policy.Mode,JobMemoryLimit=p.Kind=="git"?policy.GitJobMemoryLimit:p.Budget,PlanningBytes=p.Kind=="git"?policy.GitPlanningBytes:p.PlanningBytes??ResourcePolicy.DefaultPlanningBytes,PhysicalFloor=policy.PhysicalFloor,CommitFloor=policy.CommitFloor,CpuRate=policy.CpuRate,WaiverId=request.WaiverId}).ToArray();
            // Reject an unrepresentable plan before it can be persisted or granted; active records are never truncated later.
            foreach(var candidate in entries)Ipc.EnsureEntryRepresentable(candidate);
            ledger.Entries.AddRange(entries);
            try{Save();}catch{ledger.Entries.RemoveAll(e=>entries.Contains(e));throw;}
            return new(true,"已预约预算与工作目录",entries,sample,epoch);
        }
        var entry=ledger.Entries.SingleOrDefault(e=>e.Plan.Id==request.Attempt)??throw new IOException("启动请求不存在或已过期。");
        if(request.Operation=="revoke")
        {
            if(entry.State=="Pending"){var previousState=entry.State;var previousDetail=entry.Detail;entry.State="Revoked";entry.Detail="已撤销，迟到请求不会执行";try{Save();}catch{entry.State=previousState;entry.Detail=previousDetail;throw;}}
            return new(entry.State=="Revoked",entry.State=="Revoked"?entry.Detail:"已领取或可能运行，需继续核对",[entry],sample,epoch);
        }
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
            jobs[entry.Plan.Id]=new Job(Store.JobName(entry.Plan.Id),entry.Plan.Budget);
            entry.State="Creating";entry.Epoch=epoch;entry.Detail="正在创建进程";Save();return new(true,"单次创建授权",[entry],sample,epoch);
        }
        if(request.Operation=="resume")
        {
            if(entry.State!="Creating"||request.Epoch!=epoch||entry.Epoch!=epoch||request.Digest!=entry.Digest)throw new IOException("控制器代次或计划不符，请核对原尝试。");
            return new(true,"允许本次恢复线程",[entry],sample,epoch);
        }
        throw new IOException("未知协议操作。");
    }
    private Response Status()
    {
        var active=ledger.Entries.Where(e=>!Store.IsTerminal(e)).ToArray();
        // Withhold only an active record that cannot cross the frame boundary: failing the whole status would hide every other
        // revoke entry and leave the user without a recovery path.
        var withheld=active.Where(entry=>!Ipc.EntryRepresentable(entry)).ToArray();
        var listed=active.Where(entry=>!withheld.Contains(entry)).ToArray();
        // Each response frame is capped; keep the newest terminal entries that fit rather than a fixed count.
        var terminal=ledger.Entries.Where(Store.IsTerminal).TakeLast(40).Reverse().ToList();var kept=new List<ControlEntry>();long bytes=0;
        foreach(var e in terminal){var size=JsonSerializer.SerializeToUtf8Bytes(e,Store.Json).LongLength;if(bytes+size>768*1024)break;kept.Add(e);bytes+=size;}
        kept.Reverse();
        var omittedReason=UnrepresentableReason(withheld);
        return new(blocked==null&&withheld.Length==0,omittedReason??blocked??"监控器已核对",listed.Concat(kept).ToArray(),sample,epoch,omittedReason??AdmissionBlockedReason());
    }
    private static string? UnrepresentableReason(ControlEntry[] entries)
    {
        if(entries.Length==0)return null;
        // Names/arguments may themselves be oversized. Only bounded IDs belong in the response header.
        return $"{entries.Length} 条活动记录超出单帧边界，已暂停新启动；本次状态省略 ID：{string.Join("、",entries.Take(4).Select(e=>e.Plan.Id.ToString("N")))}。请人工核对 control.json 及备份；其他记录仍可撤销。";
    }
    private string? AdmissionBlockedReason()=>UnrepresentableReason(ledger.Entries.Where(e=>!Store.IsTerminal(e)&&!Ipc.EntryRepresentable(e)).ToArray())??blocked??(pressure?"资源压力尚未连续恢复，稍后重新检查。":null);
    public void Dispose(){foreach(var j in jobs.Values)j.Dispose();root?.Dispose();}
    private List<BudgetItem> ActiveBudgets()=>ledger.Entries.Where(e=>!Store.IsTerminal(e)).Select(e=>ResourcePolicy.BudgetOf(e.Plan)).ToList();
    private sealed record AuthorityBinding(ResourcePolicySnapshot Policy,string Hash,string Digest,long Revision,Guid[] Slots,bool? Consumed);
    private (ResourceSettings Settings,long Revision) LoadAuthority()
    {
        var path=Store.FilePath("configuration.json");
        if(!File.Exists(path))return (new ResourceSettings(),0);
        var config=Store.Read<Configuration>(path)??throw new IOException("配置无法读取，已拒绝准入。");
        config=ConfigurationCompatibility.Accept(config);
        return (config.Draft.Resource??new ResourceSettings(),config.Draft.Revision);
    }
    private ResourcePolicySnapshot AuthorityPolicy()
    {
        var (settings,_)=LoadAuthority();
        if(sample==null)throw new IOException("资源读数不可用，暂停创建。");
        ResourcePolicy.ValidateSettings(settings,sample.Total,sample.CommitLimit);
        return ResourcePolicy.SnapshotFor(settings,sample.Total,sample.CommitLimit);
    }
    private AuthorityBinding BindAuthority(Request request,LaunchItem[] items,bool consumeToken)
    {
        if(sample==null)throw new IOException("资源读数不可用，暂停创建。");
        var (settings,revision)=LoadAuthority();
        ResourcePolicy.ValidateSettings(settings,sample.Total,sample.CommitLimit);
        var policy=ResourcePolicy.SnapshotFor(settings,sample.Total,sample.CommitLimit);
        var hash=ResourcePolicy.PolicyHash(policy);
        var digest=ResourcePolicy.PlanDigest(items??[]);
        var slots=(request.EnabledSlotIds??(items??[]).Select(i=>i.Id)).ToArray();
        waivers.InvalidateMismatched(revision,hash);
        bool? consumed=null;
        if(consumeToken && request.WaiverId is {} id)consumed=waivers.Consume(id,revision,hash,digest,slots);
        if(!string.IsNullOrWhiteSpace(request.PolicyHash)&&request.PolicyHash!=hash)throw new IOException("策略快照与当前配置不一致。");
        if(!string.IsNullOrWhiteSpace(request.PlanDigest)&&request.PlanDigest!=digest)throw new IOException("计划摘要与当前启动项不一致。");
        if(request.DraftRevision is {} clientRevision&&clientRevision!=revision)throw new IOException("草稿修订号与当前配置不一致。");
        return new(policy,hash,digest,revision,slots,consumed);
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
            CpuRate=entry.CpuRate??ResourcePolicy.DefaultCpuRate
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
