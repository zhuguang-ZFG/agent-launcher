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
            if(args is ["qoder-memory-hook"])return QoderMemoryHook.RunAsync(Console.OpenStandardInput(),Console.Out,Environment.GetEnvironmentVariable(OmpMemoryGuard.PipeEnvironment)).GetAwaiter().GetResult();
            if(args is ["terminal-host",var hostId,var mode] && Guid.TryParse(hostId,out var hosted) && mode is "start" or "reattach")
                return new EmbeddedTerminalHost().Run(hosted,mode=="reattach");
            if(args is ["embedded-pane",var paneId] && Guid.TryParse(paneId,out var embedded))return RunPane(embedded,true,false);
            if(args is ["monitor"])return new Monitor().Run();
            if(args is ["job-custody"])return JobCustody.Run();
            if(args is ["root-anchor"])return 0;
            if(args.Length>=7&&args[0]=="pool-items")return PoolItems(args);
            if(args.Length>=6&&args[0]=="pool-host")return PoolHost(args);
            if(args.Length==2 && args[0] is "pane" or "run" && Guid.TryParse(args[1],out var id))return RunPane(id,args[0]=="pane");
            if(args is ["pane",var reattachText,"reattach"] && Guid.TryParse(reattachText,out var reattachId))return RunReattach(reattachId);
            Console.Error.WriteLine("用法: monitor | pane <attempt-guid> | pane <attempt-guid> reattach | pool-host <池> <项> <就绪> <释放> <新建池>");return 2;
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {
            if(args is ["monitor"])
                try{Store.Save(Store.FilePath("monitor-error.json"),new{At=DateTimeOffset.UtcNow,Type=e.GetType().FullName,e.HResult,Error=e.ToString()});}catch(Exception writeError)when(writeError is not OutOfMemoryException){}
            if((args.Length==2&&args[0] is "pane" or "run"||args is ["pane",_,"reattach"])&&Guid.TryParse(args[1],out var failedAttempt))
                try{Store.Save(Store.FilePath(Path.Combine("attempts",failedAttempt.ToString("N"),"runner-error.json")),new{At=DateTimeOffset.UtcNow,Pid=Environment.ProcessId,Type=e.GetType().FullName,e.HResult,Error=e.ToString()});}catch(Exception writeError)when(writeError is not OutOfMemoryException){}
            Console.Error.WriteLine("启动器: "+e.Message);HoldPane(args is ["pane",_] or ["pane",_,"reattach"],1,false);return 1;
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
    // Git for Windows may detach fsmonitor or a credential helper into this Job. Agent panes must wait for real tools; a preparation command must not.
    private static Dictionary<string,string> GitLaunchEnvironment()
    {
        var env=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_TERMINAL_PROMPT"]="0",["GIT_PAGER"]="cat",["GCM_INTERACTIVE"]="never",["GIT_OPTIONAL_LOCKS"]="0",["GIT_LITERAL_PATHSPECS"]="1",["LC_ALL"]="C"
        };
        var count=0;if(int.TryParse(Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"),out var existing)&&existing is >0 and <64)count=existing;
        void Add(string key,string value){env["GIT_CONFIG_KEY_"+count]=key;env["GIT_CONFIG_VALUE_"+count]=value;count++;}
        Add("core.fsmonitor","false");Add("core.pager","cat");Add("core.quotepath","false");Add("advice.detachedHead","false");
        env["GIT_CONFIG_COUNT"]=count.ToString(System.Globalization.CultureInfo.InvariantCulture);return env;
    }
    // 同步证据尽力写入（A1 sync-final / B3 sync-session）：失败只警告不阻断启动/退出——回执已携带同步结论，证据仅供徽章回退。
    private static void TryWriteSyncEvidence<T>(string path,T record)
    {
        try{Store.Save(path,record);}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or NotSupportedException){Console.Error.WriteLine($"同步证据写入失败（{Path.GetFileName(path)}）：{e.Message}");}
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
    // One helper establishes all empty leaf Jobs in a reserved pool. This does
    // not claim sibling attempts or create their Agents; each still needs begin/resume.
    private static int PoolItems(string[] args)
    {
        var items=new List<Job>();
        try
        {
            if((args.Length-5)%2!=0||(args.Length-5)/2>ResourcePolicy.MaxSlots)throw new IOException("资源池子组参数无效。");
            using var pool=new Job(args[1],openOnly:true);
            var limit=ulong.Parse(args[4],System.Globalization.CultureInfo.InvariantCulture);
            if(pool.QueryMemory()?.LimitBytes!=limit)throw new IOException("资源池上限未生效。");
            for(var index=5;index<args.Length;index+=2)
            {
                var itemLimit=ulong.Parse(args[index+1],System.Globalization.CultureInfo.InvariantCulture);
                if(itemLimit==0||itemLimit>limit)throw new IOException("单项上限无效。");
                var item=new Job(args[index]);items.Add(item);
                if(!item.CreatedNew)throw new IOException("资源子组已存在，未重复使用。");
                item.SetMemoryLimit(itemLimit);
                if(item.QueryMemory()?.LimitBytes!=itemLimit)throw new IOException("单项上限未生效。");
            }
            File.WriteAllText(args[2],"ready");
            var watch=Stopwatch.StartNew();
            while(!File.Exists(args[3])){if(watch.ElapsedMilliseconds>15000)return 3;Thread.Sleep(20);}
            return 0;
        }
        catch(Exception error)when(error is not OutOfMemoryException)
        {
            try{File.WriteAllText(args[2],"error:"+error.Message);}catch{}
            return 1;
        }
        finally{foreach(var item in items)item.Dispose();}
    }
    private static int RunPane(Guid id,bool interactive,bool holdOnExit=true)
    {
        Store.Save(Store.FilePath(Path.Combine("attempts",id.ToString("N"),"runner-entered.json")),new{At=DateTimeOffset.UtcNow,Pid=Environment.ProcessId});
        using var mutex=new Mutex(false,Store.AttemptLock(id));bool owned;
        try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
        if(!owned)throw new IOException("该启动请求正在处理。");
        // The monitor serves one pipe at a time. In a full pane batch, concurrent begin requests can each create
        // a pool helper before queued resume requests are served, exhausting the 8-second IPC budget.
        // Queue only the spawn transaction; release it as soon as Started is durable, so Agents still run in parallel.
        using var startup=new Mutex(false,Store.RootJob+"-startup");var startupOwned=false;
        ProtectedProcess? child=null;SshRunnerBackend? sshChild=null;Job? root=null;Job? pool=null;Job? job=null;JobMemoryWatch? memoryWatch=null;var resumeIntent=false;var claimCreated=false;int? memoryOffender=null;
        LaunchItem? plan=null;string? remoteHost=null;bool qoderGuard=false;  // 提升到函数级作用域（wait try 块需要访问 RemoteExecution）
        OmpMemoryGuard? toolGuard=null;Admission? observedToolDecision=null;var observedHandshake=false;
        JobMemoryEvidence? exitMemory=null;int? poolOffender=null;Guid? panePoolId=null;DateTimeOffset? poolNoticeAt=null;string poolNoticeDetail="";string? memoryNotificationError=null;var poolCheck=Stopwatch.StartNew();
        // Limit recovery: kernel notices fire once per crossing episode (measured: six refusals, one notice),
        // so recovery is a commit-only signal—four consecutive samples below 85% of the limit. The latched hit
        // history (LimitReached) is never rewritten; notices still reset the sample window when they do arrive.
        var lowCommitSamples=0;var poolLowSamples=0;var memoryRecovered=false;var poolRecovered=false;
        // Set immediately before the bounded handshake wait, so the receipt can report how long the hello took.
        DateTimeOffset handshakeWaitBegin=default;
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
            var entry=grant.Entries[0];plan=entry.Plan;remoteHost=plan.RemoteExecution?.Host;
            
            // 远程执行：不创建本地 Job/custody（远程进程不受本地资源限制，用 SSH PID/创建时间核验存活）
            if(plan.RemoteExecution is null)
            {
                root=new Job(Store.RootJob,openOnly:true);job=new Job(Store.JobName(id),openOnly:true);
                try
                {
#if INTEGRATION_TESTS
                    if(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAIL_AT")=="MemoryWatchStart")throw new System.ComponentModel.Win32Exception(6,"Controlled notification initialization failure");
#endif
                    memoryWatch=new JobMemoryWatch(job);
                }
                catch(System.ComponentModel.Win32Exception e){memoryNotificationError=$"Win32 {e.NativeErrorCode}: {e.Message}";}
                if(plan.PoolId is {} poolId){panePoolId=poolId;pool=new Job(Store.PoolName(poolId),openOnly:true);}
            }
            if(PhysicalDirectory.Workspace(plan.Cwd).Key!=plan.Workspace.Key)throw new IOException("工作目录物理身份变化，已阻止启动。");
            
            // 远程执行：拒绝共享池/本地内存策略（远程进程不受本地限制）
            if(plan.RemoteExecution is not null)
            {
                if(pool is not null)throw new IOException("远程执行不支持共享资源池（本地 Job 限制不适用于远程进程）。");
                if(plan.PlanningBytes is not null||plan.ResourceMode is not null)throw new IOException("远程执行不支持本地内存策略（远程进程不受本地 Job 限制）。");
            }
            
            // 远程执行：跳过本地 custody（远程进程不受本地 Job 限制，用 SSH PID/创建时间核验存活）
            if(plan.RemoteExecution is null)JobCustody.Retain(plan);
            LaunchEvidence.CaptureBaseline(plan);
            if(interactive)TerminalAppearance.WritePaneHeading(plan);
            Dictionary<string,string>? guardEnvironment=null;
            
            // 远程执行：跳过本地 MemoryGuard（远程进程无法加载 Windows 端 guard）
            if(plan.RemoteExecution is null)
            {
                if(job is null)throw new InvalidOperationException("本地执行需要 job（逻辑错误）");
                qoderGuard=QoderMemoryGuard.IsAttached(plan.Command);
                if(qoderGuard)QoderMemoryGuard.Prepare();
                if(OmpMemoryGuard.IsAttached(plan.Command)||qoderGuard)
                {
                    toolGuard=new OmpMemoryGuard(job,pool,entry.PhysicalFloor??2*ResourcePolicy.GiB,entry.CommitFloor??2*ResourcePolicy.GiB);
                    guardEnvironment=new(){[OmpMemoryGuard.PipeEnvironment]=toolGuard.PipeName};
                }
            }
            Write("SpawnIntent",0,0,null,"正在受保护地创建进程");
            
            // 远程执行：如果 LaunchItem 有 RemoteExecution，用 SshRunnerBackend 替代 ProtectedProcess
            // 明确：远程进程不受本地 Windows Job 限制，资源管理由远程主机负责
            if(plan.RemoteExecution is not null)
            {
                // 启动工作区同步（如果启用），在 Create 之前（同步会话的远程路径会替换 Spec.RemoteWorkingDirectory）
                IWorkspaceSyncSession? syncSession=null;
                var remoteSpec=plan.RemoteExecution;
                if(remoteSpec.WorkspaceSync is not null&&remoteSpec.WorkspaceSync.Mode!=WorkspaceSyncMode.None)
                {
                    try
                    {
                        // 超时 30 秒（Mutagen/SSHFS 启动可能需要时间）
                        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        syncSession=WorkspaceSync.StartAsync(remoteSpec.WorkspaceSync,remoteSpec,timeout.Token).GetAwaiter().GetResult();
                        // B3 会话名持久化：会话名含随机 GUID 无法从配置反推；创建后立即落盘，App 重启后据此按名匹配 mutagen sync list。
                        if(syncSession.SessionName is {} syncSessionName)TryWriteSyncEvidence(Store.SyncSessionPath(id),new SyncSessionRecord(1,syncSessionName,remoteSpec.WorkspaceSync.LocalPath,remoteSpec.WorkspaceSync.RemotePath,DateTimeOffset.UtcNow));
                        try
                        {
                            syncSession.WaitReadyAsync(timeout.Token).GetAwaiter().GetResult();
                        }
                        catch(Exception readyError)when(readyError is not OutOfMemoryException)
                        {
                            // 就绪失败：dispose 会话（清理 Mutagen 会话/SSHFS 挂载）
                            try{syncSession.DisposeAsync().AsTask().Wait(5000);}catch{}
                            // A1：WaitReady 报问题/超时后启动被阻止（FailedBeforeStart 终态）；徽章回退该记录而不是显示「未同步」。
                            TryWriteSyncEvidence(Store.SyncFinalPath(id),new SyncFinalRecord(1,SyncFinalRecord.Problem,"同步就绪失败："+readyError.Message,DateTimeOffset.UtcNow));
                            throw;
                        }
                        // 用同步会话的远程路径替换 RemoteWorkingDirectory
                        remoteSpec=remoteSpec with{RemoteWorkingDirectory=syncSession.GetRemoteWorkingDirectory()};
                    }
                    catch(Exception ex)when(ex is IOException or TimeoutException or OperationCanceledException)
                    {
                        // 同步失败：fail-closed（用户明确要求远程 agent 编辑本地工作区，同步失败不能静默降级）
                        Write("FailedBeforeStart",0,0,null,$"工作区同步失败（{ex.Message}）：远程 agent 无法访问本地工作区，已阻止启动。请检查 Mutagen/SSHFS 是否可用。");
                        throw new IOException($"工作区同步失败：{ex.Message}",ex);
                    }
                }
                
                sshChild=SshRunnerBackend.Create(remoteSpec,plan.Id);
                // 保持同步会话引用（Dispose 时清理）
                if(syncSession is not null)sshChild.SyncSession=syncSession;
                Write("CreatedSuspended",0,0,null,$"已创建 SSH 远程会话配置（{remoteSpec.Host}，不受本地资源限制），尚未运行");
            }
            else
            {
                if(root is null||job is null)throw new InvalidOperationException("本地执行需要 root/job（逻辑错误）");
                var launchEnvironment=guardEnvironment;if(plan.Kind=="git"){var gitEnvironment=GitLaunchEnvironment();if(guardEnvironment!=null)foreach(var pair in guardEnvironment)gitEnvironment[pair.Key]=pair.Value;launchEnvironment=gitEnvironment;}
                child=pool==null?new ProtectedProcess(plan.Command,plan.Cwd,root,job,redirected:!interactive,createNoWindow:plan.Kind=="git",environment:launchEnvironment):new ProtectedProcess(plan.Command,plan.Cwd,[root,pool,job],redirected:!interactive,createNoWindow:plan.Kind=="git",environment:launchEnvironment);
                Write("CreatedSuspended",child.Process.Id,child.CreationTime,null,"已创建，尚未运行");
            }
            
            var resume=Ipc.SendAsync(new("resume",Guid.NewGuid(),Attempt:id,Epoch:grant.Epoch,Digest:entry.Digest)).GetAwaiter().GetResult();
            if(!resume.Ok)throw new IOException(resume.Message);
            if(child is not null)Write("ResumeIntent",child.Process.Id,child.CreationTime,null,"准备恢复初始线程");else Write("ResumeIntent",0,0,null,"准备启动 SSH 远程会话");
            try
            {
#if INTEGRATION_TESTS
                if(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAIL_AT")=="ResumeRejected")throw new System.ComponentModel.Win32Exception(6,"Controlled ResumeThread rejection");
#endif
                if(child is not null)child.Resume();else sshChild!.Start(keepStandardInput:interactive);
                resumeIntent=true;  // Start 成功后才设置（任何异常都跳过 FailedBeforeStart）
            }
            catch(System.ComponentModel.Win32Exception){resumeIntent=false;throw;}
            var startedDetail="已启动进程（不代表任务完成）";
            if(sshChild is not null)startedDetail=$"已启动 SSH 远程会话（{plan.RemoteExecution!.Host}，不受本地资源限制）";
            // Fail open: an unconfirmed extension load weakens only the pre-limit, never the Job hard caps.
            handshakeWaitBegin=DateTimeOffset.UtcNow;
            if(toolGuard is not null&&!toolGuard.WaitForHandshake(TimeSpan.FromSeconds(3)))
                startedDetail+=$"；{(qoderGuard?"Qoder":"Omp")} 内存保护未确认加载：工具可能不受提前限流（硬上限仍生效），请核对版本与插件加载提示";
            Write("Started",(child?.Process??sshChild!.Process).Id,(child?.CreationTime??sshChild!.CreationTime),null,startedDetail);
        }
        catch(Exception error)when(error is not OutOfMemoryException)
        {
            try
            {
                // Only this claim owner may publish evidence. A duplicate runner must not overwrite a live attempt's receipt.
                if(claimCreated&&child is null&&sshChild is null&&!resumeIntent)Write("FailedBeforeStart",0,0,null,"未启动："+error.Message);
                if(child!=null&&!resumeIntent){child.AbortBeforeResume();Write("FailedBeforeStart",child.Process.Id,child.CreationTime,null,"未运行的进程已回收："+error.Message);}
                if(sshChild!=null&&!resumeIntent){sshChild.Abort();Write("FailedBeforeStart",0,0,null,"未运行的 SSH 会话已回收："+error.Message);}
            }
            finally{toolGuard?.Dispose();memoryWatch?.Dispose();child?.Dispose();sshChild?.Dispose();job?.Dispose();pool?.Dispose();root?.Dispose();}
            throw;
        }
        finally{if(startupOwned)startup.ReleaseMutex();mutex.ReleaseMutex();}
        try
        {
            var process=(child?.Process??sshChild!.Process);
            while(!process.WaitForExit(250))if(child is not null)ObserveMemory();
            sshChild?.WaitForConsoleInput();
            if(pool!=null)try{Ipc.SendAsync(new("status",Guid.NewGuid())).GetAwaiter().GetResult();}catch(Exception e)when(e is IOException or OperationCanceledException or TimeoutException){poolNoticeDetail="共享池通知尚未核对；退出码不能单独证明内存超限。";}
            if(child is not null)ObserveMemory(forcePool:true);var exit=process.ExitCode;
            // 工作区同步收敛（硬性要求：远程 agent 的改动必须落回本机）。先于 Exited/Finished 回执 flush，
            // 成功则终止会话；失败/超时保留会话并把原因写入回执，不假装已收敛。覆盖正常退出与 255 断连两条路径。
            var syncNote="";
            if(sshChild?.SyncSession is {} syncToFlush)
            {
                string? flushError;
                try{flushError=syncToFlush.TryDisposeAsync().GetAwaiter().GetResult();}
                catch(Exception ex){flushError="工作区同步收敛异常："+ex.Message;}
                sshChild.SyncSession=null;  // 已处置，避免 Dispose 重复 flush
                syncNote=flushError is null?" 工作区已同步收敛回本机。":" "+flushError;
                // A1 终态证据：会话即将终止，App 之后找不到活会话；徽章回退该记录（synced/problem），不再误报「未同步」。
                TryWriteSyncEvidence(Store.SyncFinalPath(id),new SyncFinalRecord(1,flushError is null?SyncFinalRecord.Synced:SyncFinalRecord.Problem,flushError??"Mutagen flush 成功，会话已终止；远端改动已收敛落回本机。",DateTimeOffset.UtcNow));
            }
            exitMemory=child is not null?MemoryEvidence():null;var detail=ExitDiagnostics.Describe(exit,exitMemory)+syncNote;
            
            // SSH 连接错误（255）写 Disconnected，不是 Exited/Finished
            if(sshChild is not null&&sshChild.IsSshConnectionError)
            {
                detail=$"SSH 连接断开（{remoteHost}，退出码 255，不受本地资源限制）：网络错误或主机不可达";
                Write("Disconnected",process.Id,sshChild.CreationTime,255,detail);
                if(interactive)Console.WriteLine("\nSSH 连接断开。"+detail+"\n请检查网络连接和远程主机状态。");
                return 255;
            }
            
            if(sshChild is not null)detail=$"SSH 远程会话（{remoteHost}，不受本地资源限制）："+detail;
            if(pool is not null)detail+="共享资源池："+ExitDiagnostics.MemoryText(PoolEvidence(),sharedPool:true)+poolNoticeDetail;
            if(memoryRecovered)detail+="当前已回落：提交低于上限 85%（历史触及不撤销）。";
            Write("Exited",process.Id,(child?.CreationTime??sshChild!.CreationTime),exit,"进程已退出。"+detail);
            if(interactive&&exit!=0)Console.WriteLine("\nAgent 异常退出。"+detail+"\n工作目录已保留；如仍有工具运行，将等待它们结束。");
            var reapedGit=false;
            if(job is not null)
            {
                var grace=Stopwatch.StartNew();
                while(job.Members().Length>0)
                {
                    // Preparation only. Agent panes keep waiting: a live tool is not a finished task, and this is not kill-on-close.
                    if(plan?.Kind=="git"&&grace.Elapsed>=TimeSpan.FromSeconds(2))
                    {
                        Native.Check(Native.TerminateJobObject(job.Handle,1));reapedGit=true;
                        var drained=Stopwatch.StartNew();while(job.Members().Length>0&&drained.Elapsed<TimeSpan.FromSeconds(2))Thread.Sleep(50);
                        break;
                    }
                    Thread.Sleep(250);if(child is not null)ObserveMemory();
                }
                if(reapedGit&&job.Members().Length>0)throw new IOException("Git 准备结束后仍有受管进程，已停止等待。");
            }
            if(child is not null)ObserveMemory(forcePool:true);exitMemory=child is not null?MemoryEvidence():null;detail=ExitDiagnostics.Describe(exit,exitMemory)+(pool is null?"":"共享资源池："+ExitDiagnostics.MemoryText(PoolEvidence(),sharedPool:true)+poolNoticeDetail)+(memoryRecovered?"当前已回落：提交低于上限 85%（历史触及不撤销）。":"")+(reapedGit?"Git 准备留下的后台进程已结束，不再等待。":"");
            if(sshChild is not null)detail=$"SSH 远程会话（{remoteHost}，不受本地资源限制）："+detail;
            Write("Finished",process.Id,(child?.CreationTime??sshChild!.CreationTime),exit,"全部受管进程已退出。"+detail);
#if !INTEGRATION_TESTS
            HoldPane(interactive&&holdOnExit,exit,exitMemory?.LimitReached==true||poolOffender!=null);
#endif
            return exit;
        }
        finally{toolGuard?.Dispose();memoryWatch?.Dispose();child?.Dispose();sshChild?.Dispose();job?.Dispose();pool?.Dispose();root?.Dispose();}
        JobMemoryEvidence? MemoryEvidence()=>job?.QueryMemory() is {} evidence?evidence with{LimitReached=memoryOffender!=null,OffendingProcessId=memoryOffender,NotificationError=memoryNotificationError}:null;
        JobMemoryEvidence? PoolEvidence()=>pool?.QueryMemory() is {} evidence?evidence with{LimitReached=poolOffender!=null,OffendingProcessId=poolOffender}:null;
        void ObserveMemory(bool forcePool=false)
        {
            int[] pids=[];var changed=false;
            // A late handshake must clear the launch-time warning; measured kernel behavior posts a limit
            // notice once per crossing episode, so offender changes rewrite the receipt only for a new PID.
            var handshake=toolGuard?.HandshakeConfirmed??false;
            if(handshake!=observedHandshake){observedHandshake=handshake;changed=true;}
            var decision=toolGuard?.LastDecision;
            if(decision!=observedToolDecision){observedToolDecision=decision;changed=true;}
            try
            {
#if INTEGRATION_TESTS
                if(memoryWatch is not null&&Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAIL_AT")=="MemoryWatchDrain")throw new System.ComponentModel.Win32Exception(6,"Controlled notification read failure");
#endif
                pids=memoryWatch?.Drain()??[];
            }
            catch(System.ComponentModel.Win32Exception e)
            {
                memoryNotificationError=$"Win32 {e.NativeErrorCode}: {e.Message}";memoryWatch?.Dispose();memoryWatch=null;changed=true;
            }
            if(pids.Length>0)
            {
                // A fresh notice also withdraws the recovery overlay until the commit window re-qualifies.
                lowCommitSamples=0;
                if(memoryRecovered){memoryRecovered=false;changed=true;}
                if(memoryOffender!=pids[0]){memoryOffender=pids[0];changed=true;}
            }
            if(memoryOffender is not null&&exitMemory is {LimitBytes:>0} hit&&job?.QueryCurrentCommit() is {} commit)
            {
                // Slow to set (4 samples), fast to clear (one above-threshold sample): the note may describe
                // the present only while sampling continues, so a rebound withdraws it immediately.
                var below=commit<hit.LimitBytes*85/100;
                if(memoryRecovered){if(!below){memoryRecovered=false;lowCommitSamples=0;changed=true;}}
                else if(below){if(++lowCommitSamples>=4){memoryRecovered=true;changed=true;}}
                else lowCommitSamples=0;
            }
            if(changed)exitMemory=MemoryEvidence();
            if(panePoolId is {} poolId&&(forcePool||poolCheck.ElapsedMilliseconds>=1000))
            {
                poolCheck.Restart();
                try
                {
                    var notice=Store.Read<PoolLimitNotice>(Store.FilePath(Path.Combine("pools",poolId.ToString("N")+".limit.json")));
                    if(notice is not null&&notice.PoolId==poolId&&notice.At>=DateTimeOffset.FromFileTime(child!.CreationTime)&&notice.At!=poolNoticeAt)
                    {
                        poolNoticeAt=notice.At;changed=true;
                        // A fresh pool notice resets the recovery interval as well.
                        poolLowSamples=0;if(poolRecovered)poolRecovered=false;
                        if(notice.Pid==child.Process.Id||job!.Members().Contains(notice.Pid))poolOffender=notice.Pid;
                        else poolNoticeDetail=$"同一共享池已收到内核分配拒绝通知（PID {notice.Pid}），该 PID 与本窗格的归属未确认。";
                    }
                }
                catch(Exception e)when(e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException){poolNoticeDetail="共享池通知不可读取，不能据此判断本窗格超限。";}
                if(poolOffender is not null&&pool is not null&&PoolEvidence() is {LimitBytes:>0} poolHit&&pool.QueryCurrentCommit() is {} poolCommit)
                {
                    var poolBelow=poolCommit<poolHit.LimitBytes*85/100;
                    if(poolRecovered){if(!poolBelow){poolRecovered=false;poolLowSamples=0;changed=true;}}
                    else if(poolBelow){if(++poolLowSamples>=4){poolRecovered=true;changed=true;}}
                    else poolLowSamples=0;
                }
            }
            if(!changed)return;
            var text="已启动进程（不代表任务完成）"+(exitMemory is null?"":"；"+ExitDiagnostics.MemoryText(exitMemory))+(pool is null?"":"；共享池："+ExitDiagnostics.MemoryText(PoolEvidence(),sharedPool:true)+poolNoticeDetail);
            if(toolGuard is not null&&!toolGuard.HandshakeConfirmed)text+="；内存保护未确认加载";
            if(observedToolDecision is {Allowed:false})text+="；"+ToolRecovery.BlockedMarker+observedToolDecision.Reason;
            else if(observedToolDecision is {Allowed:true})text+="；最近一次工具已通过内存检查";
            if(memoryRecovered)text+="；当前已回落：提交低于上限 85%（历史触及不撤销；内核通知为一次性事件，不能据此判断是否仍有分配被拒）";
            if(poolRecovered)text+="；共享池当前已回落（历史触及不撤销）";
            Write(child!.Process.HasExited?"Exited":"Started",child.Process.Id,child.CreationTime,child.Process.HasExited?child.Process.ExitCode:null,text);
            if(interactive)Console.Error.WriteLine("\n"+text);
        }
        void Write(string phase,int pid,long time,int? code,string detail)
        {
            // Handshake evidence: the first hello is written once, so repeat hellos cannot shorten the reported wait.
            var helloAt=toolGuard?.FirstHelloAt;
            var helloWaitMs=helloAt is{} h&&handshakeWaitBegin!=default?(long?)Math.Max(0L,(long)(h-handshakeWaitBegin).TotalMilliseconds):null;
            Store.Save(Store.ReceiptPath(id),new Receipt(phase,pid,time,code,detail,DateTimeOffset.UtcNow,exitMemory,PoolEvidence(),helloAt,helloWaitMs));
#if INTEGRATION_TESTS
            if(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAIL_AT")==phase)Environment.Exit(97);
#endif
        }
    }
    // 同一条账本上的重新挂接窗格。不走 begin/resume，不创建本地 Job，不把尝试写成终态。
    // 本地 SSH 结束后回到 Disconnected：远程进程是否还在仍要操作员用 R28 判断，这里不自动释放。
    private static int RunReattach(Guid id)
    {
        Store.Save(Store.FilePath(Path.Combine("attempts",id.ToString("N"),"runner-entered.json")),new{At=DateTimeOffset.UtcNow,Pid=Environment.ProcessId,Reattach=true});
        using var mutex=new Mutex(false,Store.AttemptLock(id));bool owned;
        try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
        if(!owned)throw new IOException("该启动请求正在处理。");
        SshRunnerBackend? ssh=null;IWorkspaceSyncSession? sync=null;var started=false;var claimed=false;
        try
        {
            var claim=Path.Combine(Store.Root,"attempts",id.ToString("N"),"claim");Directory.CreateDirectory(Path.GetDirectoryName(claim)!);
            var grant=Ipc.SendAsync(new("reattach-begin",Guid.NewGuid(),Attempt:id)).GetAwaiter().GetResult();
            if(!grant.Ok||grant.Entries is not {Length:1})throw new IOException(grant.Message);
            using(var file=new FileStream(claim,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){claimed=true;file.WriteByte(1);file.Flush(true);}
            var plan=grant.Entries[0].Plan;var remote=plan.RemoteExecution??throw new IOException("该条目不是远程执行，不能重新挂接。");
            if(RemoteReattach.RestoresSync(remote.WorkspaceSync?.Mode)&&remote.WorkspaceSync is {} syncSpec)
            {
                SyncFinalRecord? final=null;SyncSessionRecord? saved=null;
                try{if(File.Exists(Store.SyncFinalPath(id)))final=Store.Read<SyncFinalRecord>(Store.SyncFinalPath(id));}catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException){}
                try{if(File.Exists(Store.SyncSessionPath(id)))saved=Store.Read<SyncSessionRecord>(Store.SyncSessionPath(id));}catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException){}
                if(saved is not null&&final is not {State:SyncFinalRecord.Synced})syncSpec=syncSpec with{MutagenSessionName=saved.Name};
                else if(final is {State:SyncFinalRecord.Synced})syncSpec=syncSpec with{MutagenSessionName=null};
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                sync=WorkspaceSync.StartAsync(syncSpec,remote,timeout.Token).GetAwaiter().GetResult();
                if(sync.SessionName is {} syncSessionName)TryWriteSyncEvidence(Store.SyncSessionPath(id),new SyncSessionRecord(1,syncSessionName,syncSpec.LocalPath,syncSpec.RemotePath,DateTimeOffset.UtcNow));
                sync.WaitReadyAsync(timeout.Token).GetAwaiter().GetResult();
                try{if(File.Exists(Store.SyncFinalPath(id)))File.Delete(Store.SyncFinalPath(id));}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
            }
            ssh=SshRunnerBackend.CreateAttachment(remote,id);
            ssh.Start(keepStandardInput:true);
            var syncNote=RemoteReattach.RestoresSync(remote.WorkspaceSync?.Mode)?"并重新建立双向同步":"未启用同步，不视为已同步";
            WriteReceipt(id,"Started",ssh.Process.Id,ssh.CreationTime,null,$"已重新挂接本地 SSH（{remote.Host} 的 {remote.RemoteWorkingDirectory}，{syncNote}）。未新启动 Agent，未释放占用。");
            started=true;
        }
        catch(Exception error)when(error is not OutOfMemoryException)
        {
            var pid=0;long time=0;
            if(started&&ssh is not null){pid=ssh.Process.Id;time=ssh.CreationTime;}
            if(claimed)WriteReceipt(id,"Disconnected",pid,time,null,"重新挂接失败："+error.Message+" 占用保留，未释放，也未把条目标为已结束。");
            if(ssh is not null){ssh.SyncSession=null;ssh.Dispose();ssh=null;}
            throw;
        }
        finally{if(owned)mutex.ReleaseMutex();}
        try
        {
            while(!ssh!.WaitForExit(250)){}
            ssh.WaitForConsoleInput();
            var exit=ssh.ExitCode;
            var detail=exit==255
                ?$"重新挂接的本地 SSH 再次断开（{ssh.Spec.Host}，退出码 255）。远程进程可能仍在，占用保留。"
                :$"重新挂接的本地 SSH 已结束（退出码 {exit}）。这不证明远程进程已退出，也不释放占用。若进程已不在，请使用「核对并确认释放」。";
            WriteReceipt(id,"Disconnected",ssh.Process.Id,ssh.CreationTime,exit,detail);
            Console.WriteLine("\n"+detail);
            return exit==0?0:exit;
        }
        finally{if(ssh is not null){ssh.SyncSession=null;ssh.Dispose();}}
        static void WriteReceipt(Guid attempt,string phase,int pid,long time,int? code,string detail)=>Store.Save(Store.ReceiptPath(attempt),new Receipt(phase,pid,time,code,detail,DateTimeOffset.UtcNow));
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
    private readonly Dictionary<Guid,PaneActivity> paneActivity=[];
    private ulong trimPhysicalFloor;
    private TimeSpan nextTrimAt;
    private DateTimeOffset lastTrimSampleAt;
#if INTEGRATION_TESTS
    private Func<TimeSpan>? trimClockOverride=null;
#endif
    private int? rootCpuRate;
    private string? rootCpuReason;
    private bool retireForCpuChange;
    private Job? root;
    private MemorySnapshot? sample;
    private MemorySnapshot? budgetBaseline;
    private DateTimeOffset pressureObservedAt=DateTimeOffset.MinValue;
#if INTEGRATION_TESTS
    private Func<MemorySnapshot>? sampleOverride=null;
#endif
    private DateTime lastActivity=DateTime.UtcNow;
    private string? blocked;
    private bool reconcileOffered;
    private bool ledgerBroken;
    private string ledgerBrokenDetail="控制账本损坏或版本不支持，已停止启动与回收。请人工核对 control.json，必要时用 .bak 备份恢复。";
    private readonly PressureTracker pressureTracker=new();
    private bool pressure=>pressureTracker.Pressure;
    private static readonly TimeSpan IdleTimeout=TimeSpan.FromSeconds(ResourcePolicy.IdleTimeoutSeconds);
    private static readonly TimeSpan AcceptTick=TimeSpan.FromSeconds(1);
    private sealed record ReattachProbe(Guid RequestId,DateTimeOffset Expires,Task<string> Result);
    private readonly Dictionary<Guid,ReattachProbe> reattachProbes=[];
    private const long LedgerBudget=6L*1024*1024;
    private static readonly TimeSpan TrimNoteLifetime=TimeSpan.FromMinutes(10);
    private const ulong TrimReportFloor=32UL*1024*1024;
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
            if(root.CreatedNew)
            {
                // Fix the root's ancestor chain from the monitor before a Terminal-owned Worker can be its first member.
                // Otherwise an empty root first used by Terminal can inherit a different outer Job and reject later pool helpers.
                using var anchor=new ProtectedProcess(new(Store.WorkerPath,["root-anchor"],"exe"),Store.Root,[root],createNoWindow:true);
                anchor.Resume();
                if(!anchor.Process.WaitForExit(5000)||anchor.Process.ExitCode!=0)throw new IOException("资源保护根初始化未完成，已停止启动；请核对监控器记录。");
            }
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
                        try{response=Handle(request);}
                        catch(Exception e)when(e is not OutOfMemoryException)
                        {
                            if(e is System.ComponentModel.Win32Exception)
                                try{Store.Save(Store.FilePath("monitor-native-error.json"),new{At=DateTimeOffset.UtcNow,request.Operation,request.Attempt,Type=e.GetType().FullName,e.HResult,Error=e.ToString()});}catch(Exception writeError)when(writeError is not OutOfMemoryException){}
                            response=new(false,e.Message);
                        }
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
        foreach(var id in reattachProbes.Where(p=>p.Value.Expires<=DateTimeOffset.UtcNow).Select(p=>p.Key).ToArray())reattachProbes.Remove(id);
        var samplingNeeded=jobs.Count>0||ledgerBroken||blocked!=null||pressure||ledger.Entries.Any(e=>!Store.IsTerminal(e));
        if(forceSample||sample==null||(samplingNeeded&&DateTimeOffset.UtcNow-sample.At>=TimeSpan.FromSeconds(2)))
            SampleResources();
        var changed=false;blocked=ledgerBroken?ledgerBrokenDetail:null;reconcileOffered=false;
        foreach(var record in ledger.Pools.Where(p=>!pools.ContainsKey(p.Id)))
        {
            try{pools[record.Id]=new Job(Store.PoolName(record.Id),openOnly:true);WatchPool(record.Id,pools[record.Id]);}
            catch(System.ComponentModel.Win32Exception ex) when(ex.NativeErrorCode==2)
            {
                blocked="资源池句柄丢失，无法证明整池成员已退出；保留池与预算，系统重启后自动释放。";
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
                        blocked="资源池句柄丢失，无法证明整池成员已退出；保留池与预算，系统重启后自动释放。";
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
                if(receipt?.Phase is "Exited" or "Finished" && e.ExitCode!=receipt.ExitCode){e.ExitCode=receipt.ExitCode;changed=true;}
                
                // 远程执行：没有本地 Job，用 SSH PID/创建时间核验存活
                if(e.Plan.RemoteExecution is not null)
                {
                    if(receipt?.Phase is "Started"&&receipt.Pid>0)
                    {
                        // 检查 SSH 进程是否仍存活（PID + 创建时间核验）
                        try
                        {
                            using var sshProcess=Process.GetProcessById(receipt.Pid);
                            if(sshProcess.StartTime.ToFileTimeUtc()==receipt.ProcessTime)
                            {
                                // SSH 进程仍存活（PID 和创建时间匹配），保持 Started 状态
                                if(e.State!="Started"){e.State="Started";changed=true;}
                                e.Detail=receipt.Detail;
                                continue;  // 不进入本地 Job reconciliation
                            }
                        }
                        catch(ArgumentException){/* 进程不存在 */}
                    }
                    // 重新挂接已授权、新窗格尚未写回执时，旧的 Disconnected 回执不能把状态打回去。
                    // 新回执晚于 operator-reattach.json 且说明本地 SSH 又结束了，才回到 Disconnected；不终态化、不释放。
                    if(e.State=="Reattaching")
                    {
                        var reattachRecord=TryReadReattach(e.Plan.Id);
                        var attachmentEnded=reattachRecord!=null&&receipt!=null&&receipt.At>reattachRecord.At&&(receipt.Phase=="Disconnected"||(receipt.Phase=="Started"&&receipt.Pid>0&&!ProcessAlive(receipt.Pid,receipt.ProcessTime)));
                        if(attachmentEnded)
                        {
                            e.State="Disconnected";e.Detail=receipt!.Phase=="Disconnected"?receipt.Detail:"重新挂接的本地 SSH 已结束，远程进程未证明退出；占用保留。";changed=true;continue;
                        }
                        continue;
                    }
                    // SSH 进程已退出或无法核验，进入 Disconnected 状态
                    if(receipt?.Phase is "Started")
                    {
                        e.State="Disconnected";e.Detail="SSH 连接断开（远程进程可能仍存活，请手动核对远程主机状态）";changed=true;reconcileOffered=true;continue;
                    }
                }
                
                // 嵌入式窗格的 pane Worker（RunPane）已退出而回执仍停在 Started：它不会再写任何回执。保持非终态与占用，不猜测退出码（不写 255）。
                // 只有 terminal-host 写过 embedded.json 的尝试才适用本分支；普通 run 的窗格退出由 Job/custody 排水路径如实结算（UX12）。
                if(e.Plan.RemoteExecution is null&&receipt?.Phase=="Started"&&EmbeddedAttempt(e.Plan.Id)&&PaneWorkerGone(e.Plan.Id))
                {
                    if(e.Detail!="宿主已退出，退出码未知；占用保留"){e.Detail="宿主已退出，退出码未知；占用保留";changed=true;}
                    var hostExit=Store.FilePath(Path.Combine("attempts",e.Plan.Id.ToString("N"),"host-exit.json"));
                    if(!File.Exists(hostExit))Store.Save(hostExit,new{KnownExitCode=(int?)null,Detail="宿主已退出，退出码未知",At=DateTimeOffset.UtcNow});
                    reconcileOffered=true;continue;
                }
                if(j==null && receipt?.Phase is not ("Finished" or "FailedBeforeStart" or "Disconnected") && e.Plan.RemoteExecution is null) {blocked="资源组句柄丢失，无法证明后代已退出；保留预算，系统重启后可核对释放。";e.Detail=blocked;reconcileOffered=true;continue;}
                // SSH 连接断开（Disconnected）且 Job 句柄丢失：远程执行没有本地 Job，保持可见未知状态
                if(j==null && receipt?.Phase is "Disconnected")
                {
                    e.State="Disconnected";e.Detail=receipt.Detail+"（远程进程可能仍存活，请手动核对远程主机状态）";changed=true;reconcileOffered=true;continue;
                }
                var members=j?.Members()??[];
                if(members.Length==0)
                {
                    // SSH 连接断开（Disconnected）：远程进程可能存活，保持可见未知状态，防止盲目重启
                    if(receipt?.Phase is "Disconnected")
                    {
                        e.State="Disconnected";e.Detail=receipt.Detail+"（远程进程可能仍存活，请手动核对远程主机状态）";changed=true;reconcileOffered=true;continue;
                    }
                    // 远程执行：不可验证的创建阶段（ResumeIntent/SpawnIntent/CreatedSuspended）映射到 Disconnected（远程进程可能存活）
                    if(e.Plan.RemoteExecution is not null && receipt?.Phase is "ResumeIntent" or "SpawnIntent" or "CreatedSuspended")
                    {
                        e.State="Disconnected";e.Detail="远程创建中断（远程进程可能仍存活，请手动核对远程主机状态）";changed=true;reconcileOffered=true;continue;
                    }
                    e.State=receipt?.Phase is "Started" or "Exited" or "ResumeIntent" or "Finished"?"Settled":"FailedBeforeStart";e.Detail=receipt?.Detail??"创建者已退出，核对无存活进程";changed=true;continue;
                }
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
                    // Keep memory-observation receipts on the neutral summary, but surface the launch receipt's disclosure.
                    var startedDetail=receipt?.Detail?.StartsWith("已启动进程",StringComparison.Ordinal)==true?receipt.Detail:"已启动进程（不代表任务完成）";
                    e.Detail=receipt?.Phase=="Exited"?"Agent 已退出，工具仍运行。"+receipt.Detail:receipt?.Phase=="Started"?startedDetail:"结果未确认；占用保留";
                    if(receipt?.PoolMemory?.LimitReached==true)e.Detail+="。共享池："+ExitDiagnostics.MemoryText(receipt.PoolMemory,sharedPool:true);
                    if(e.Plan.PoolId is {} watchedPool&&poolWatchErrors.TryGetValue(watchedPool,out var watchError))e.Detail+="。共享池通知不可用："+watchError;
                    if(receipt?.JobMemory is { } itemMemory&&(itemMemory.LimitReached||itemMemory.NotificationError is not null))e.Detail+="。"+ExitDiagnostics.MemoryText(itemMemory);
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
        RefreshIdleWorkingSets();
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
    private sealed class PaneActivity
    {
        public IdleWorkingSetTracker Tracker { get; }=new();
        public string? Note { get; set; }
        public TimeSpan NotedAt { get; set; }
    }
    private void RefreshIdleWorkingSets()
    {
        var now=Stopwatch.GetElapsedTime(0);
#if INTEGRATION_TESTS
        if(trimClockOverride is not null)now=trimClockOverride();
#endif
        var candidates=ledger.Entries.Where(e=>e.State=="Started"&&e.Plan.Kind!="git").ToArray();
        var ids=candidates.Select(e=>e.Plan.Id).ToHashSet();
        foreach(var id in paneActivity.Keys.Where(id=>!ids.Contains(id)).ToArray())paneActivity.Remove(id);
        var validSample=sample is not null&&ResourcePolicy.IsSampleValid(sample);
        // Trimming cannot relieve commit-only or CPU-only pressure. Pace attempts across panes and demand a
        // newer system sample after each attempt so an old shortage cannot trigger a burst of paging.
        var memoryPressure=blocked==null&&!ledgerBroken&&pressure&&validSample&&sample!.Available<trimPhysicalFloor;
        foreach(var e in candidates)
        {
            if(!paneActivity.TryGetValue(e.Plan.Id,out var activity))paneActivity[e.Plan.Id]=activity=new();
            try
            {
                jobs.TryGetValue(e.Plan.Id,out var job);
                activity.Tracker.Observe(validSample?job?.QueryCpuTicks():null,now);
                if(job is not null&&memoryPressure&&now>=nextTrimAt&&sample!.At>lastTrimSampleAt&&activity.Tracker.TryBeginTrim(now))
                {
                    nextTrimAt=now+TimeSpan.FromSeconds(5);lastTrimSampleAt=sample.At;
                    var before=JobUsageReader.Read(job).PrivateWorkingSetBytes;
                    var result=job.TrimWorkingSets();
                    var after=JobUsageReader.Read(job).PrivateWorkingSetBytes;
                    if(result.Trimmed>0&&before is {} b&&after is {} a&&b>a&&b-a>=TrimReportFloor)
                    {
                        activity.Note=$"空闲会话工作集减少 {(b-a)/1048576.0:0} MiB；再次使用时按需载入";
                        activity.NotedAt=now;
                    }
                }
            }
            catch(Exception ex)when(ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException or OverflowException)
            {
                // An optional optimization cannot corrupt launch authority or terminate the monitor.
                activity.Tracker.Observe(null,now);
            }
            if(activity.Note is {Length:>0} note&&now-activity.NotedAt<TrimNoteLifetime&&!e.Detail.Contains(note,StringComparison.Ordinal))e.Detail+="；"+note;
        }
    }
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
            var (settings,_)=LoadAuthority();var desired=settings.Mode=="custom"?settings.CpuRate:ResourcePolicy.DefaultCpuRate;
            if(rootCpuRate==desired){rootCpuReason=null;return;}
            rootCpuReason=$"当前根 Job CPU 封顶为 {rootCpuRate.Value/100.0:0.##}%，新配置为 {desired/100.0:0.##}%；现有 Job 不会修改。请等待现有任务结束并让监控器退出，再重新检查资源。";
            retireForCpuChange=CanRetire();
        }
        catch(Exception e)when(e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException){rootCpuReason="无法核对根 Job CPU 配置，已暂停新启动："+e.Message;}
    }
    private Response Handle(Request request)
    {
        if(request.Operation is not ("reserve" or "reserve-task"))return HandleRequest(request);
        var key=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(configurationPath).ToUpperInvariant())));
        using var gate=new Mutex(false,@"Local\AgentLauncher-file-"+key);bool own;
        try{own=gate.WaitOne(2000);}catch(AbandonedMutexException){own=true;}
        if(!own)throw new IOException("配置正在写入，请重新检查资源。");
        try{return HandleRequest(request);}finally{gate.ReleaseMutex();}
    }
    private Response HandleRequest(Request request)
    {
        if(request.Operation=="status")return StatusFor(request);
        if(request.Operation=="attempt")return Attempt(request);
        if(request.Operation is "reserve" or "reserve-task")
        {
            var items=request.Items??[];
            if(request.Operation=="reserve-task"&&ReplayTaskClaim(request,items) is {} replay)return replay;
            // Pressure depends on the entry's frozen mode, so it is never an early gate: the global configuration mode must not
            // decide a queue reservation whose snapshot was frozen as practical. Everything else still precedes the binding.
            if(AdmissionHardReason() is {} earlyReason)throw new IOException(earlyReason);
            var binding=request.Operation=="reserve-task"?BindTaskAuthority(request,items):BindAuthority(request,items);
            if(pressure&&binding.Policy.Mode=="conservative"&&items.Any(i=>i.RemoteExecution is null))throw new IOException("资源压力尚未连续恢复，稍后重新检查。");
            if(AdmissionHardReason() is {} reason)throw new IOException(reason);
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
                // 执行域分域校验：远程计划只受远程合同约束，本地 Job/可执行文件规则不适用于远程域。
                if(p.RemoteExecution is {} remotePlan)
                {
                    if(p.Kind!="agent"||p.Budget!=0||p.PoolId is not null||p.PlanningBytes is not null||p.ResourceMode is not null||p.Command.Adapter!="remote"||p.Command.Executable!=remotePlan.AgentType)throw new IOException("执行计划不支持。");
                }
                else
                {
                    if(p.Kind is not ("agent" or "git")||(p.Kind=="git"&&p.Budget!=ResourcePolicy.GitJobMemoryLimit)||(p.Kind=="agent"&&(p.Budget<ResourcePolicy.MinJobMemoryLimit||p.Budget>ResourcePolicy.MaxJobMemoryLimit))||!File.Exists(p.Command.Executable)||!p.Command.Executable.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new IOException("执行计划不支持。");
                }
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
            var requestedItems=items.Where(p=>p.RemoteExecution is null).Select(p=>ResourcePolicy.BudgetOf(p,p.PoolId is {} poolId?ledger.Pools.SingleOrDefault(pool=>pool.Id==poolId)?.MemoryLimit??binding.Policy.PoolMemoryLimit:null)).ToList();
            // 远程域不消耗本地预算：纯远程批次跳过本地准入决策（安全类闸门仍在上方共享路径生效）。
            if(requestedItems.Count>0)
            {
                var decision=Decide(binding.Policy,reservedItems,requestedItems);
                if(decision.Kind==AdmissionKind.Deny)throw new IOException(decision.Reason);
            }
            // Ray-style fixed concurrency pool: the per-process Job cap stays the hard wall, this ceiling keeps the
            // machine responsive. Entries this request already covers are not counted twice on replay/retry paths.
            var concurrent=ledger.Entries.Count(e=>!Store.IsTerminal(e)&&(e.Plan.Kind is null or "agent")&&!items.Any(i=>i.Id==e.Plan.Id))
                +items.Count(i=>i.Kind is null or "agent");
            if(concurrent>ResourcePolicy.MaxConcurrentAgents)
                throw new IOException($"同时运行的 Agent 窗格最多 {ResourcePolicy.MaxConcurrentAgents} 个；请先结束或撤销部分 Agent 后再启动。");
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
            var entries=items.Select(p=>new ControlEntry{Plan=p,Digest=Store.Digest(p),Epoch=epoch,Mode=policy.Mode,JobMemoryLimit=p.Kind=="git"?policy.GitJobMemoryLimit:p.RemoteExecution is not null?policy.JobMemoryLimit:p.Budget,PlanningBytes=ResourcePolicy.BudgetOf(p).PlanningBytes,PhysicalFloor=policy.PhysicalFloor,CommitFloor=policy.CommitFloor,CpuRate=rootCpuRate??policy.CpuRate,Pool=policy.PoolMode,PoolMemoryLimit=policy.PoolMode==ResourcePolicy.PoolShared?policy.PoolMemoryLimit:null}).ToArray();
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
            // A ledger written before the 8-pane cap can still hold more Pending agent entries. Refuse the grant without
            // consuming it so every extra entry stays revocable; Git preparation is not an Agent pane and is not counted.
            if(ledger.Entries.Count(e=>!Store.IsTerminal(e)&&(e.Plan.Kind is null or "agent"))>ResourcePolicy.MaxConcurrentAgents)throw new IOException($"同时运行的 Agent 窗格最多 {ResourcePolicy.MaxConcurrentAgents} 个；请先结束或撤销部分 Agent 后再启动。");
            if(UnrepresentableReason(ledger.Entries.Where(e=>!Store.IsTerminal(e)&&!Ipc.EntryRepresentable(e)).ToArray()) is {} reason)throw new IOException(reason);
            if(blocked!=null)throw new IOException(blocked);
            if(PhysicalDirectory.Workspace(entry.Plan.Cwd).Key!=entry.Plan.Workspace.Key)throw new IOException("目录物理身份已变化。");
            // 执行域分域：远程条目不重建本地策略、不做本地准入决策、不创建本地 Job；
            // 资源压力与预算属于本地域，远程进程的资源由远程主机负责。安全类闸门已在上方共享路径核对。
            if(entry.Plan.RemoteExecution is null)
            {
                var policy=PolicyFromEntry(entry);
                if(pressure&&policy.Mode=="conservative")throw new IOException("资源压力尚未连续恢复，暂不恢复运行。已保留预约，可撤销此项。");
                var beginDecision=Decide(policy,ActiveBudgets(),[],includeSoftBudget:false);
                if(beginDecision.Kind==AdmissionKind.Deny)throw new IOException(beginDecision.Reason.Contains("暂停新增任务",StringComparison.Ordinal)
                    ?"资源压力尚未连续恢复，暂不恢复运行。已保留预约，可撤销此项。":beginDecision.Reason+" 已保留预约，可撤销此项。");
                if(entry.Plan.PoolId is {} poolId)
                {
                    if(!pools.TryGetValue(poolId,out var pool)){pool=new Job(Store.PoolName(poolId),openOnly:true);pools[poolId]=pool;}
                    var poolLimit=pool.QueryMemory()?.LimitBytes??0;
                    if(poolLimit==0)throw new IOException("资源池上限不可读，已停止创建。");
                    jobs[entry.Plan.Id]=CreateItemJob(poolId,entry.Plan.Id,pool,poolLimit,entry.Plan.Budget);
                }
                else
                {
                    jobs[entry.Plan.Id]=new Job(Store.JobName(entry.Plan.Id),entry.Plan.Budget);
                }
            }
            entry.State="Creating";entry.Epoch=epoch;entry.Detail="正在创建进程";Save();return new(true,"单次创建授权",[entry],sample,epoch);
        }
        if(request.Operation=="resume")
        {
            if(entry.State!="Creating"||request.Epoch!=epoch||entry.Epoch!=epoch||request.Digest!=entry.Digest)throw new IOException("控制器代次或计划不符，请核对原尝试。");
            return new(true,"允许本次恢复线程",[entry],sample,epoch);
        }
        if(request.Operation=="release-remote")return ReleaseRemote(entry,request.Release);
        if(request.Operation is "reattach-remote" or "reattach-poll")return ReattachRemote(entry,request);
        if(request.Operation=="reattach-begin")return ReattachBegin(entry);
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
        var budgets=blocked==null&&active.Length>0?ActiveBudgets().ToArray():null;
        var hard=AdmissionHardReason();
        var reason=omittedReason??AdmissionBlockedReason();
        // A raw pressure flip must change the signature even when the composed reason text does not (global practical mode),
        // otherwise a paged client reusing the previous signature would skip the frame that carries it.
        var signature=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{Epoch=epoch,Blocked=blocked,Reason=reason,Hard=hard,Pressure=pressure,RootCpuRate=rootCpuRate,ReconcileOffered=reconcileOffered,Active=active.Select(e=>StatusEntry(e).Signature),History=kept.Select(e=>StatusEntry(e).Signature),Pools=ledger.Pools.Select(p=>new{p.Id,p.MemoryLimit})},Store.Json)));
        var unchanged=request?.StatusEpoch==epoch&&request.StatusSignature==signature;
        return new(blocked==null&&withheld.Length==0&&AuthorityBlockedReason()==null,reason??"监控器已核对",unchanged?null:active.Where(entry=>!withheld.Contains(entry)).Concat(kept).ToArray(),sample,epoch,reason,Unchanged:unchanged,StatusSignature:signature,RootCpuRate:rootCpuRate,ReconcileOffered:reconcileOffered,Pools:ledger.Pools.Count==0?null:ledger.Pools.ToArray(),Budgets:budgets,AdmissionHardBlockedReason:hard,AdmissionPressure:pressure);
    }
    // Fail-closed, user-confirmed release: settle only entries whose resource group provably no longer exists and whose receipt already proves the whole managed tree exited (Finished) or never ran (FailedBeforeStart). A dead recorded parent alone is not proof: jobs have no KILL_ON_JOB_CLOSE, so descendants may survive the handle loss and cannot be enumerated without it.
    private Response ReconcileRequests()
    {
        if(ledgerBroken)return BuildStatus(null) with{Ok=false,Message=ledgerBrokenDetail};
        var before=ledger.Entries.Select(entry=>(Entry:entry,entry.State,entry.Detail)).ToArray();
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
                // Reboot proof: a recorded root started before the current boot means every tree member was
                // spawned before the boot too (post-boot processes cannot be spawned by pre-boot ones), so the
                // whole tree ended with the previous session even though the Job handle is lost. This is what
                // makes the "系统重启后可核对释放" guidance true.
                if(receipt.ProcessTime>0&&receipt.ProcessTime<DateTime.UtcNow.Subtract(TimeSpan.FromMilliseconds(Native.GetTickCount64())).ToFileTimeUtc())
                {
                    e.State="Settled";
                    e.Detail=$"经确认的核对释放：记录进程启动早于本次系统启动，整树已随重启结束；原回执阶段 {receipt.Phase}；{receipt.Detail??"无附加说明"}";
                    settled++;changed=true;continue;
                }
                // A dead recorded parent is not tree proof: jobs have no KILL_ON_JOB_CLOSE, so the object can be gone while descendants survive, and without the handle they cannot be enumerated.
                refusals.Add((e,$"记录的父进程（PID {receipt.Pid}）已退出，但资源组句柄丢失使其派生进程无法枚举核对"));continue;
            }
            catch(Exception ex)when(ex is not OutOfMemoryException){refusals.Add((e,"核对失败："+ex.Message));}
            finally{gate.ReleaseMutex();}
        }
        if(changed&&!SaveReconciliation(before))return BuildStatus(null) with{Ok=false,Message=ledgerBrokenDetail};
        Refresh(forceSample:true);
        before=ledger.Entries.Select(entry=>(Entry:entry,entry.State,entry.Detail)).ToArray();
        foreach(var (entry,reason) in refusals)entry.Detail="核对释放被拒绝："+reason+"。";
        if(refusals.Count>0&&!SaveReconciliation(before))return BuildStatus(null) with{Ok=false,Message=ledgerBrokenDetail};
        var snapshot=BuildStatus(null);
        var ok=refusals.Count==0&&snapshot.Ok;
        var message=settled>0||refusals.Count>0
            ?$"已核对释放 {settled} 条占用"+(refusals.Count>0?$"；{refusals.Count} 条保持阻塞："+string.Join("；",refusals.Select(r=>r.Reason)):"。")
            :"没有需要核对释放的占用。";
        // A lost pool handle can hide surviving direct members no entry proves dead (ACC-S3B03), so reconcile never
        // retires pools within a boot — boot rollover does. Disclose what this pass cannot release instead of
        // letting "已核对释放" read as if the pool hold were cleared too.
        var retainedPools=ledger.Pools.Count(p=>!pools.ContainsKey(p.Id));
        if(retainedPools>0)message=message.TrimEnd('。')+$"；{retainedPools} 个资源池句柄丢失：成员无法枚举核对，保留至系统重启自动释放。";
        if(!ok&&refusals.Count==0&&snapshot.AdmissionBlockedReason!=null)message+="当前仍阻塞："+snapshot.AdmissionBlockedReason;
        return snapshot with{Ok=ok,Message=message};
    }
    private bool SaveReconciliation((ControlEntry Entry,string State,string Detail)[] before)
    {
        try{Save();return true;}
        catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ledger.Entries=before.Select(previous=>previous.Entry).ToList();
            foreach(var previous in before){previous.Entry.State=previous.State;previous.Entry.Detail=previous.Detail;}
            ledgerBroken=true;blocked=ledgerBrokenDetail="核对结果未写入，已停止启动与回收；原占用和证据保留，请修复记录目录后重新核对："+e.Message;
            return false;
        }
    }
    // R28 操作员确认释放：结束本地 SSH 客户端不能证明远程进程已退出，只有 Disconnected 远程条目在
    // 「已不在（Gone）+ 释放原因 + 二次确认」齐备时才终态化；其余组合一律拒绝且账本不变（fail-closed）。
    // 系统重启后的 boot 证明仍走 ReconcileRequests 的既有分支；本操作不提供会盲目再拉起远程进程的重试。
    private Response ReleaseRemote(ControlEntry entry,OperatorRelease? release)
    {
        if(ledgerBroken)return new(false,ledgerBrokenDetail,[entry],sample,epoch);
        if(entry.Plan.RemoteExecution is not {} remote||entry.State is not ("Disconnected" or "Reattaching"))return new(false,"只有连接断开或正在重新挂接、且本地 SSH 已结束的远程条目才能操作员确认释放；当前条目状态不变。",[entry],sample,epoch);
        if(release is null)return new(false,"缺少操作员核对记录（判断与释放原因），未释放。",[entry],sample,epoch);
        if(release.Judgment!="Gone")return new(false,"判断为仍在或无法判断时拒绝释放：结束本地 SSH 不能证明远程进程已退出。账本不变，请继续在远程主机核对。",[entry],sample,epoch);
        var reason=release.Reason?.Trim()??"";
        if(reason.Length==0)return new(false,"缺少释放原因，未释放；请填写核对结论后重新提交。",[entry],sample,epoch);
        if(reason.Length>2048)return new(false,"释放原因超过 2048 字上限，未释放；请精简后重新提交。",[entry],sample,epoch);
        if(!release.Confirmed)return new(false,"操作员尚未二次确认，未释放；条目保持连接断开，预算保留。",[entry],sample,epoch);
        var receipt=ReadReceipt(entry.Plan.Id);
        if(receipt?.Phase=="Started"&&receipt.Pid>0&&ProcessAlive(receipt.Pid,receipt.ProcessTime))return new(false,"本地 SSH 客户端仍在运行，条目不是断开状态；请等待核对刷新后再决定。",[entry],sample,epoch);
        using var gate=new Mutex(false,Store.AttemptLock(entry.Plan.Id));bool own;
        try{own=gate.WaitOne(0);}catch(AbandonedMutexException){own=true;}
        if(!own)return new(false,"创建者仍在运行，不能核对释放；请稍后重试。",[entry],sample,epoch);
        try
        {
            // 先发布身份绑定的操作员证据，再终态化账本（与裁剪前归档 outcome.json 同一纪律）；写入失败不终态化。
            Store.Save(Store.OperatorReleasePath(entry.Plan.Id),new OperatorReleaseRecord(1,entry.Plan.Id,remote.Host,remote.RemoteWorkingDirectory,release.Judgment,reason,true,DateTimeOffset.UtcNow,entry.Digest));
            var previousState=entry.State;var previousDetail=entry.Detail;
            entry.State="Settled";entry.Detail=$"操作员确认释放：已核对 {remote.Host} 的 {remote.RemoteWorkingDirectory}，判断远程进程已不在；原因：{reason}";
            try{Save();}catch{entry.State=previousState;entry.Detail=previousDetail;throw;}
            return new(true,"操作员确认释放完成：条目已终态，占用与预算已释放；核对记录见 operator-release.json。",[entry],sample,epoch);
        }
        finally{gate.ReleaseMutex();}
    }
    // R33 操作员确认重新挂接。不自动重连。成功只把同一条账本改为 Reattaching，不释放预算、不写 operator-release、不新增条目。
    // 远程进程已不在、回执尚不能证明启动过、或操作员未确认时拒绝，账本保持原样。窗格由调用方另行启动。
    private Response ReattachRemote(ControlEntry entry,Request request)
    {
        if(ledgerBroken)return new(false,ledgerBrokenDetail,[entry],sample,epoch);
        if(entry.Plan.RemoteExecution is not {} remote||entry.State is not ("Disconnected" or "Reattaching"))return new(false,"只有连接断开或正在重新挂接的远程条目才能重新挂接；当前条目状态不变。不会自动重连。",[entry],sample,epoch);
        if(request.Reattach is not {Confirmed:true})return new(false,"操作员尚未确认重新挂接；不会自动重连，账本不变。",[entry],sample,epoch);
        var receipt=ReadReceipt(entry.Plan.Id);
        if(receipt is null||receipt.Phase is not ("Disconnected" or "Started"))return new(false,"远程工作尚未进入已启动，不能重新挂接；账本不变。若远程进程已不在，请使用「核对并确认释放」。",[entry],sample,epoch);
        if(receipt.Phase=="Started"&&receipt.Pid>0&&ProcessAlive(receipt.Pid,receipt.ProcessTime))return new(false,"本地 SSH 仍在运行，不必重新挂接；账本不变。",[entry],sample,epoch);
        using var gate=new Mutex(false,Store.AttemptLock(entry.Plan.Id));bool own;
        try{own=gate.WaitOne(0);}catch(AbandonedMutexException){own=true;}
        if(!own)return new(false,"创建者仍在运行，不能重新挂接；请稍后重试。账本不变。",[entry],sample,epoch);
        try
        {
            var id=entry.Plan.Id;
            if(!reattachProbes.TryGetValue(id,out var pending))
            {
                if(request.Operation=="reattach-poll")return new(false,"重新挂接核对已失效，请重新确认。",[entry],sample,epoch);
                reattachProbes[id]=new(request.RequestId,DateTimeOffset.UtcNow.AddSeconds(35),Task.Run(()=>ProbeRemoteAsync(remote,id)));
                return new(false,"正在核对远程进程。",Epoch:epoch,ReattachProbePending:true);
            }
            if(request.RequestId!=pending.RequestId || (request.Operation=="reattach-poll"&&request.Epoch!=epoch))
                return new(false,"已有远程进程核对请求，请稍后重试。",[entry],sample,epoch);
            if(request.Operation!="reattach-poll"||!pending.Result.IsCompleted)return new(false,"正在核对远程进程。",Epoch:epoch,ReattachProbePending:true);
            reattachProbes.Remove(id);
            var probe=pending.Result.GetAwaiter().GetResult();
            if(probe is "timeout" or "error")return new(false,"无法登录远程主机核对进程，重新挂接已拒绝；账本不变。",[entry],sample,epoch);
            if(probe=="gone")return new(false,"远程进程已不在，重新挂接已拒绝；账本不变。请使用「核对并确认释放」。本操作不会自动释放。",[entry],sample,epoch);
            if(probe!="alive")return new(false,"远程进程核对结果无法判断，重新挂接已拒绝；账本不变。不会自动重连，也不会自动释放。",[entry],sample,epoch);
            string? session=null;
            try{if(File.Exists(Store.SyncSessionPath(entry.Plan.Id)))session=Store.Read<SyncSessionRecord>(Store.SyncSessionPath(entry.Plan.Id))?.Name;}catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException){}
            var syncMode=remote.WorkspaceSync?.Mode.ToString()??"None";
            var claim=Path.Combine(Store.Root,"attempts",id.ToString("N"),"claim");
            if(File.Exists(claim))File.Move(claim,claim+".before-reattach",overwrite:true);
            Store.Save(Store.OperatorReattachPath(entry.Plan.Id),new OperatorReattachRecord(1,entry.Plan.Id,remote.Host,remote.RemoteWorkingDirectory,syncMode,session,"ALIVE",true,DateTimeOffset.UtcNow,entry.Digest));
            var previousState=entry.State;var previousDetail=entry.Detail;
            entry.State="Reattaching";entry.Detail=$"操作员已确认重新挂接：{remote.Host} 的 {remote.RemoteWorkingDirectory}；远程进程仍在。等待本地 SSH 窗格挂上。此操作不释放占用。";
            try{Save();}catch{entry.State=previousState;entry.Detail=previousDetail;throw;}
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){return new(false,"重新挂接未能写入，账本保持原样："+ex.Message,[entry],sample,epoch);}
        finally{gate.ReleaseMutex();}
        return new(true,"已授权重新挂接：远程进程仍在，占用未释放。请打开本地 SSH 窗格；此操作不会把条目标为已结束。",[entry],sample,epoch);
    }
    private Response ReattachBegin(ControlEntry entry)
    {
        if(entry.Plan.RemoteExecution is null||entry.State!="Reattaching")return new(false,"重新挂接授权已失效；条目状态不变。",[entry],sample,epoch);
        return new(true,"允许本次重新挂接",[entry],sample,epoch);
    }
    private static async Task<string> ProbeRemoteAsync(RemoteExecutionSpec remote,Guid attemptId)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            if(!string.IsNullOrEmpty(remote.SshKeyPath))await SshAgent.EnsureKeyInAgentAsync(remote.SshKeyPath,deadline.Token);
            var shell=RemoteReattach.ProbeShell(attemptId);
            var wrapped=remote.SshUser==remote.AgentUser?shell:SshRemoteCommand.WrapSu(remote.AgentUser,shell);
            var result=await SshRemoteCommand.RunAsync(SshRemoteCommand.CreateStartInfo(remote.Host,remote.SshPort,remote.SshUser,remote.SshKeyPath,wrapped),TimeSpan.FromSeconds(20),deadline.Token);
            return RemoteReattach.ClassifyProbe(result.ExitCode,result.TimedOut,result.StdOut);
        }
        catch(OperationCanceledException){return "timeout";}
        catch(Exception e)when(e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException){return "error";}
    }
    private OperatorReattachRecord? TryReadReattach(Guid id)
    {
        try{return File.Exists(Store.OperatorReattachPath(id))?Store.Read<OperatorReattachRecord>(Store.OperatorReattachPath(id)):null;}
        catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException){return null;}
    }
    private sealed record RunnerEntered(DateTimeOffset At,int Pid);
    // A pane worker that died before writing a terminal receipt can no longer report the host's exit code; the recorded pid itself being gone is the only hard evidence. Unreadable or missing evidence keeps the entry on the normal path.
    // 只有嵌入式宿主（terminal-host）写下的尝试才适用「宿主已退出」的保留语义；普通 run 与外部窗格的退出由 Job 排水如实结算。
    private static bool EmbeddedAttempt(Guid id)=>File.Exists(Store.FilePath(Path.Combine("attempts",id.ToString("N"),"embedded.json")));
    private static bool PaneWorkerGone(Guid id)
    {
        try
        {
            var entered=Store.Read<RunnerEntered>(Store.FilePath(Path.Combine("attempts",id.ToString("N"),"runner-entered.json")));
            if(entered?.Pid is not >0)return false;
            using var process=Process.GetProcessById(entered.Pid);
            return process.HasExited;
        }
        catch(ArgumentException){return true;}
        catch{return false;}
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
    private readonly record struct StatusFields(LaunchItem Plan,string State,string Epoch,string Digest,DateTimeOffset Created,string Detail,string? Mode,ulong? JobMemoryLimit,ulong? PlanningBytes,ulong? PhysicalFloor,ulong? CommitFloor,int? CpuRate,int? ExitCode);
    private sealed record StatusEntryCache(StatusFields Fields,string Signature,long Bytes,bool Representable);
    private StatusEntryCache StatusEntry(ControlEntry entry)
    {
        var fields=new StatusFields(entry.Plan,entry.State,entry.Epoch,entry.Digest,entry.Created,entry.Detail,entry.Mode,entry.JobMemoryLimit,entry.PlanningBytes,entry.PhysicalFloor,entry.CommitFloor,entry.CpuRate,entry.ExitCode);
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
        using var helper=new ProtectedProcess(new(Store.WorkerPath,["pool-host",Store.PoolName(id),"",ready,release,"1",limit.ToString(System.Globalization.CultureInfo.InvariantCulture)],"exe"),Store.Root,[root!],createNoWindow:true);
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
        if(jobs.TryGetValue(itemId,out var existing))
        {
            if(existing.QueryMemory()?.LimitBytes!=itemLimit)throw new IOException("已准备的单项上限不符。");
            if(existing.Members().Length!=0)throw new IOException("预建资源子组存在未核对进程，已停止创建 Agent。");
            return existing;
        }
        var pending=ledger.Entries.Where(entry=>entry.State=="Pending"&&entry.Plan.PoolId==poolId&&!jobs.ContainsKey(entry.Plan.Id)).OrderBy(entry=>entry.Plan.Id==itemId?0:1).Take(ResourcePolicy.MaxSlots).ToArray();
        if(!pending.Any(entry=>entry.Plan.Id==itemId))throw new IOException("资源子组没有匹配的预约。");
        var ready=PoolMarker(itemId,"ready");var release=PoolMarker(itemId,"release");
        Directory.CreateDirectory(Path.GetDirectoryName(ready)!);
        File.Delete(ready);File.Delete(release);
        var arguments=new[]{"pool-items",Store.PoolName(poolId),ready,release,limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}
            .Concat(pending.SelectMany(entry=>new[]{Store.JobName(entry.Plan.Id),entry.Plan.Budget.ToString(System.Globalization.CultureInfo.InvariantCulture)})).ToArray();
        using var helper=new ProtectedProcess(new(Store.WorkerPath,arguments,"exe"),Store.Root,[root!,pool],createNoWindow:true);
        helper.Resume();
        var opened=new Dictionary<Guid,Job>();
        try
        {
            WaitForPool(ready,itemId);
            foreach(var entry in pending)
            {
                var item=new Job(Store.JobName(entry.Plan.Id),openOnly:true);opened.Add(entry.Plan.Id,item);
                if(item.QueryMemory()?.LimitBytes!=entry.Plan.Budget)throw new IOException("资源子组上限不符。");
            }
            File.WriteAllText(release,"1");
            if(!helper.Process.WaitForExit(5000)||helper.Process.ExitCode!=0)throw new IOException("资源子组助手未正常退出，已停止创建 Agent。");
            foreach(var (id,item) in opened)jobs.Add(id,item);
            opened.Clear();
            return jobs[itemId];
        }
        finally
        {
            foreach(var item in opened.Values)item.Dispose();
            File.WriteAllText(release,"1");
        }
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
    private string? AdmissionBlockedReason()=>AdmissionReason(includePressure:true);
    // Pressure is a reading evaluated against the caller's frozen mode, so the reserve gates ask for the reason without it.
    // A default parameter would have changed this method's arity; reflection callers in the existing checks pass no arguments.
    private string? AdmissionHardReason()=>AdmissionReason(includePressure:false);
    private string? AdmissionReason(bool includePressure)
    {
        string? authority;bool conservative;
        try{var (settings,_)=LoadAuthority();authority=null;conservative=settings.Mode=="conservative";}
        // Pressure is a reading, not an authority: only conservative mode refuses on it, and an unreadable policy stays fail-closed.
        catch(Exception e)when(e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException){authority=e.Message;conservative=true;}
        return authority??UnrepresentableReason(ledger.Entries.Where(e=>!Store.IsTerminal(e)&&!StatusEntry(e).Representable).ToArray())??blocked??rootCpuReason??(includePressure&&pressure&&conservative?"资源压力尚未连续恢复，稍后重新检查。":null);
    }
    public void Dispose(){foreach(var watch in poolWatches.Values)watch.Dispose();poolWatches.Clear();poolWatchErrors.Clear();foreach(var j in jobs.Values)j.Dispose();jobs.Clear();foreach(var j in pools.Values)j.Dispose();pools.Clear();receiptCache.Clear();statusEntryCache.Clear();paneActivity.Clear();root?.Dispose();}
    private List<BudgetItem> ActiveBudgets()
    {
        SampleResources();budgetBaseline=sample;
        var poolUsage=pools.ToDictionary(pair=>pair.Key,pair=>pair.Value.QueryMemory()?.LimitBytes==ledger.Pools.SingleOrDefault(p=>p.Id==pair.Key)?.MemoryLimit?JobUsageReader.Read(pair.Value):null);
        var items=ledger.Entries.Where(e=>!Store.IsTerminal(e)&&e.Plan.RemoteExecution is null).Select(e=>
        {
            var usage=jobs.TryGetValue(e.Plan.Id,out var job)&&job.QueryMemory()?.LimitBytes==e.Plan.Budget?JobUsageReader.Read(job):null;
            var pooled=e.Plan.PoolId is {} poolId?poolUsage.GetValueOrDefault(poolId):null;
            return ResourcePolicy.BudgetOf(e.Plan,PoolLimitOf(e.Plan.PoolId)) with{CurrentCommitBytes=usage?.CommitBytes,CurrentPhysicalBytes=usage?.PrivateWorkingSetBytes,PoolCommitBytes=pooled?.CommitBytes};
        }).ToList();
        foreach(var record in ledger.Pools)
        {
            if(ledger.Entries.Any(e=>!Store.IsTerminal(e)&&e.Plan.PoolId==record.Id))continue;
            var limit=(pools.TryGetValue(record.Id,out var pool)?pool.QueryMemory()?.LimitBytes:null)??record.MemoryLimit;
            if(limit==0)limit=record.MemoryLimit;
            if(limit==0)continue;
            var usage=poolUsage.GetValueOrDefault(record.Id);
            items.Add(new(record.Id,"agent",ResourcePolicy.DefaultPlanningBytes,limit,record.Id,limit,CurrentPhysicalBytes:usage?.PrivateWorkingSetBytes,PoolCommitBytes:usage?.CommitBytes));
        }
        return items;
    }
    private ulong? PoolLimitOf(Guid? poolId)=>poolId is {} id?ledger.Pools.SingleOrDefault(p=>p.Id==id)?.MemoryLimit:null;
    private sealed record AuthorityBinding(ResourcePolicySnapshot Policy,string Hash);
    private (ResourceSettings Settings,long Revision) LoadAuthority()
    {
        if(!File.Exists(configurationPath))
        {
            if(File.Exists(configurationPath+".bak"))throw new IOException("主配置丢失但存在 .bak 备份，已拒绝准入；请人工核对并恢复 configuration.json。");
            return (new ResourceSettings(),0);
        }
        var config=Store.Read<Configuration>(configurationPath)??throw new IOException("配置无法读取，已拒绝准入。");
        config=ConfigurationCompatibility.Accept(config);
        return (config.Draft.Resource,config.Draft.Revision);
    }
    private ResourcePolicySnapshot AuthorityPolicy()
    {
        var (settings,_)=LoadAuthority();
        if(sample==null)throw new IOException("资源读数不可用，暂停创建。");
        ResourcePolicy.ValidateSettings(settings,sample.Total,sample.CommitLimit);
        return ResourcePolicy.SnapshotFor(settings,sample.Total,sample.CommitLimit);
    }
    private Response? ReplayTaskClaim(Request request,LaunchItem[] items)
    {
        if(request.TaskEntryId is not {} entryId||entryId==Guid.Empty||items.Length!=1||items[0] is null)
            throw new IOException("队列预约缺少单项授权身份。");
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
        if(sample==null||request.TaskEntryId is not {} entryId||items.Length!=1)
            throw new IOException("队列预约缺少单项授权身份。");
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
        return new(policy,hash);
    }

    private AuthorityBinding BindAuthority(Request request,LaunchItem[] items)
    {
        if(sample==null)throw new IOException("资源读数不可用，暂停创建。");
        if(items.Length is <1 or >ResourcePolicy.MaxSlots||items.Any(i=>i==null)||items.Select(i=>i.Id).Distinct().Count()!=items.Length)throw new IOException("启动项数量或 ID 无效。");
        var (settings,revision)=LoadAuthority();
        ResourcePolicy.ValidateSettings(settings,sample.Total,sample.CommitLimit);
        var policy=ResourcePolicy.SnapshotFor(settings,sample.Total,sample.CommitLimit);
        var hash=ResourcePolicy.PolicyHash(policy);var digest=ResourcePolicy.PlanDigest(items);
        if(!string.IsNullOrWhiteSpace(request.PolicyHash)&&request.PolicyHash!=hash)throw new IOException("策略快照与当前配置不一致。");
        if(!string.IsNullOrWhiteSpace(request.PlanDigest)&&request.PlanDigest!=digest)throw new IOException("计划摘要与当前启动项不一致。");
        if(request.DraftRevision is {} clientRevision&&clientRevision!=revision)throw new IOException("草稿修订号与当前配置不一致。");
        foreach(var item in items)ResourcePolicy.ValidateLaunchItem(item,policy,sample.Total);
        return new(policy,hash);
    }
    private ResourcePolicySnapshot PolicyFromEntry(ControlEntry entry)
    {
        var settings=new ResourceSettings
        {
            Mode=entry.Mode??entry.Plan.ResourceMode??"practical",
            JobMemoryLimit=(entry.Plan.RemoteExecution is not null&&entry.JobMemoryLimit is null or 0)?ResourcePolicy.DefaultJobMemoryLimit:entry.JobMemoryLimit??entry.Plan.Budget,
            PlanningBytes=(entry.Plan.RemoteExecution is not null&&entry.PlanningBytes is null or 0)?ResourcePolicy.DefaultPlanningBytes:entry.PlanningBytes??entry.Plan.PlanningBytes??ResourcePolicy.DefaultPlanningBytes,
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
        // Bracket the Job queries with global samples. A concurrent free must not credit both
        // the older Job charge and the newly freed system capacity in the same admission.
        // Sampling must not reconcile/remove entries or replace the authority underlying this decision.
        SampleResources();
        if(sample==null)return new(AdmissionKind.Deny,"资源读数不可用，暂停创建。",0,0,0,0,0,policy.PhysicalFloor,policy.CommitFloor,false);
        if(pressure&&policy.Mode=="conservative")return new(AdmissionKind.Deny,"资源压力尚未连续恢复，暂停新增任务。",0,0,0,0,0,policy.PhysicalFloor,policy.CommitFloor,ResourcePolicy.IsSampleValid(sample));
        var admissionSample=budgetBaseline is {} before?sample with{Available=Math.Min(sample.Available,before.Available),CommitAvailable=Math.Min(sample.CommitAvailable,before.CommitAvailable),At=before.At}:sample;
        return ResourcePolicy.Evaluate(admissionSample,policy,reserved,requested,includeSoftBudget);
    }
    private void SampleResources()
    {
        sample=sampler.Sample();
#if INTEGRATION_TESTS
        if(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE")=="1")sample=new(16*ResourcePolicy.GiB,12*ResourcePolicy.GiB,32*ResourcePolicy.GiB,24*ResourcePolicy.GiB,5,DateTimeOffset.UtcNow);
        if(sampleOverride is not null)sample=sampleOverride();
#endif
        ulong floor,commitFloor;
        try{var policy=AuthorityPolicy();floor=policy.PhysicalFloor;commitFloor=policy.CommitFloor;}
        catch(Exception e)when(e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException){floor=ResourcePolicy.DefaultPhysicalFloor(sample.Total);commitFloor=ResourcePolicy.DefaultCommitFloor(sample.CommitLimit);}
        trimPhysicalFloor=floor;
        if(DateTimeOffset.UtcNow-pressureObservedAt>=TimeSpan.FromSeconds(2))
        {
            pressureTracker.Observe(sample,floor,commitFloor);pressureObservedAt=DateTimeOffset.UtcNow;
        }
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
