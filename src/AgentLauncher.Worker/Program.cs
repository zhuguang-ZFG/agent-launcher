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
            var grant=Ipc.SendAsync(new("begin",Guid.NewGuid(),Attempt:id)).GetAwaiter().GetResult();
            if(!grant.Ok||grant.Entries is not {Length:1})throw new IOException(grant.Message);
            var entry=grant.Entries[0];var plan=entry.Plan;
            root=new Job(Store.RootJob,openOnly:true);job=new Job(Store.JobName(id),openOnly:true);
            if(PhysicalDirectory.Workspace(plan.Cwd).Key!=plan.Workspace.Key)throw new IOException("工作目录物理身份变化，已阻止启动。");
            Write("SpawnIntent",0,0,null,"正在受保护地创建进程");
            child=new ProtectedProcess(plan.Command,plan.Cwd,root,job,redirected:!interactive);
            Write("CreatedSuspended",child.Process.Id,child.CreationTime,null,"已创建，尚未运行");
            var resume=Ipc.SendAsync(new("resume",Guid.NewGuid(),Attempt:id,Epoch:grant.Epoch,Digest:entry.Digest)).GetAwaiter().GetResult();
            if(!resume.Ok)throw new IOException(resume.Message);
            Write("ResumeIntent",child.Process.Id,child.CreationTime,null,"准备恢复初始线程");resumeIntent=true;
            child.Resume();Write("Started",child.Process.Id,child.CreationTime,null,"已启动进程（不代表任务完成）");
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
    private bool pressure;
    private int healthy,busy;
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
            root=new Job(Store.RootJob,root:true);sampler.Sample();Thread.Sleep(250);Refresh();
            while(true)
            {
                NamedPipeServerStream pipe;
                try{pipe=new NamedPipeServerStream(Store.Pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);}
                catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException){blocked="命名管道被占用或不可用，正在重试："+e.Message;Thread.Sleep(1000);continue;}
                using(pipe)
                {
                    using var wait=new CancellationTokenSource(2000);
                    try{pipe.WaitForConnectionAsync(wait.Token).GetAwaiter().GetResult();}
                    catch(OperationCanceledException){Refresh();if(!ledgerBroken&&ledger.Entries.All(Store.IsTerminal)&&DateTime.UtcNow-lastActivity>TimeSpan.FromSeconds(5))return 0;continue;}
                    lastActivity=DateTime.UtcNow;
                    using var deadline=new CancellationTokenSource(3000);
                    try
                    {
                        var request=Ipc.ReadAsync<Request>(pipe,deadline.Token).GetAwaiter().GetResult();Refresh();
                        Response response;
                        try{response=Handle(request);}catch(Exception e)when(e is not OutOfMemoryException){response=new(false,e.Message);}
                        Ipc.WriteAsync(pipe,response,deadline.Token).GetAwaiter().GetResult();
                    }
                    catch(Exception e)when(e is IOException or OperationCanceledException or JsonException){/* Peer failure never grants additional authorization. */}
                }
            }
        }
        finally{Dispose();single.ReleaseMutex();}
    }
    private void Save()=>Store.Save(ledgerPath,ledger);
    private void Refresh()
    {
        if(sample==null||DateTimeOffset.UtcNow-sample.At>=TimeSpan.FromSeconds(2))
        {
            sample=sampler.Sample();
#if INTEGRATION_TESTS
            if(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE")=="1")sample=new(16*ResourcePolicy.GiB,12*ResourcePolicy.GiB,32*ResourcePolicy.GiB,24*ResourcePolicy.GiB,5,DateTimeOffset.UtcNow);
#endif
            busy=sample.CpuPercent>=80?busy+1:0;
            if(sample.Available<ResourcePolicy.Reserve(sample.Total)||busy>=3){pressure=true;healthy=0;}
            else if(pressure&&++healthy>=5)pressure=false;
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
        if(request.Operation=="reserve")
        {
            if(blocked!=null)throw new IOException(blocked);if(pressure)throw new IOException("资源压力尚未连续恢复，稍后重新检查。");
            var items=request.Items??[];if(items.Length is <1 or >4||items.Select(i=>i.Id).Distinct().Count()!=items.Length)throw new IOException("启动项数量或 ID 无效。");
            var previous=ledger.Entries.Where(e=>items.Any(i=>i.Id==e.Plan.Id)).ToArray();
            if(previous.Length>0){if(previous.Length==items.Length&&previous.All(e=>e.Digest==Store.Digest(items.Single(i=>i.Id==e.Plan.Id))))return new(true,"请求已记录",previous,sample,epoch);throw new IOException("启动 ID 已使用。");}
            var active=ledger.Entries.Where(e=>!Store.IsTerminal(e)).ToList();
            foreach(var p in items)
            {
                if(p.Kind is not ("agent" or "git")||p.Budget!=(p.Kind=="git"?ResourcePolicy.GitBudget:ResourcePolicy.AgentBudget)||!File.Exists(p.Command.Executable)||!p.Command.Executable.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new IOException("执行计划不支持。");
                if(PhysicalDirectory.Workspace(p.Cwd).Key!=p.Workspace.Key)throw new IOException("目录身份变化。");
                if(p.Kind=="git" && active.Any(e=>e.Plan.Kind=="git"))throw new IOException("已有 Git 准备正在运行，请稍后重试。");
                if(p.Kind=="agent" && active.Any(e=>e.Plan.Kind=="agent"&&PhysicalDirectory.Overlaps(e.Plan.Workspace,p.Workspace)&&(!p.Shared||!e.Plan.Shared)))throw new IOException("工作目录已被独立会话占用，请选择另一个工作目录。");
                active.Add(new(){Plan=p});
            }
            var reserved=ledger.Entries.Where(e=>!Store.IsTerminal(e)).Aggregate(0UL,(sum,e)=>sum+e.Plan.Budget);
            var decision=ResourcePolicy.Check(sample!,reserved,items.Aggregate(0UL,(sum,p)=>sum+p.Budget));if(!decision.Allowed)throw new IOException(decision.Reason);
            var entries=items.Select(p=>new ControlEntry{Plan=p,Digest=Store.Digest(p),Epoch=epoch}).ToArray();ledger.Entries.AddRange(entries);
            try{Save();}catch{ledger.Entries.RemoveAll(e=>entries.Contains(e));throw;}
            return new(true,"已预约预算与工作目录",entries,sample,epoch);
        }
        var entry=ledger.Entries.SingleOrDefault(e=>e.Plan.Id==request.Attempt)??throw new IOException("启动请求不存在或已过期。");
        if(request.Operation=="revoke")
        {
            if(entry.State=="Pending"){entry.State="Revoked";entry.Detail="已撤销，迟到请求不会执行";try{Save();}catch{entry.State="Pending";throw;}}
            return new(entry.State=="Revoked",entry.State=="Revoked"?entry.Detail:"已领取或可能运行，需继续核对",[entry],sample,epoch);
        }
        if(request.Operation=="begin")
        {
            if(entry.State!="Pending")throw new IOException("授权已撤销或被领取，不能重复启动。");
            if(blocked!=null)throw new IOException(blocked);
            if(pressure||sample!.Available<ResourcePolicy.Reserve(sample.Total)||sample.CpuPercent>=80)throw new IOException("资源压力升高，保留预约，暂停创建；可撤销此项。");
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
    private Response Status()=>new(blocked==null,blocked??"监控器已核对",ledger.Entries.Where(e=>!Store.IsTerminal(e)).Concat(ledger.Entries.Where(Store.IsTerminal).TakeLast(40)).ToArray(),sample,epoch);
    public void Dispose(){foreach(var j in jobs.Values)j.Dispose();root?.Dispose();}
}
