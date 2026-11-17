using System.Diagnostics;
using System.IO.Pipes;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

/// <summary>Independent handle owner. Never changes the ledger, starts Agents, or kills a Job.</summary>
public static class JobCustody
{
    private sealed record KeepRequest(Guid AttemptId,Guid? PoolId,string BootId);
    private sealed record KeepReply(bool Ok,string Error);
    private static string Pipe=>Store.Pipe+"-custody";

    public static void Retain(LaunchItem plan)
    {
        // Custody is idempotent and has no execution authority. Retry a disconnected handshake with a fresh pipe.
        for(var attempt=0;;attempt++)
        {
            try{RetainAsync(plan).GetAwaiter().GetResult();return;}
            catch(Exception e)when(attempt<2&&(e is IOException or OperationCanceledException)){Thread.Sleep(30);}
        }
    }
    private static async Task RetainAsync(LaunchItem plan)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var pipe=new NamedPipeClientStream(".",Pipe,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using(var connect=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
        {
            connect.CancelAfter(100);
            try{await pipe.ConnectAsync(connect.Token);}
            catch(OperationCanceledException)when(!deadline.IsCancellationRequested)
            {
                var start=new ProcessStartInfo(Store.WorkerPath){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};start.ArgumentList.Add("job-custody");
                using var process=Process.Start(start)??throw new IOException("无法启动资源句柄守护进程。");
                await pipe.ConnectAsync(deadline.Token);
            }
        }
        await Ipc.WriteAsync(pipe,new KeepRequest(plan.Id,plan.PoolId,Store.BootId()),deadline.Token);
        var reply=await Ipc.ReadAsync<KeepReply>(pipe,deadline.Token);
        if(!reply.Ok)throw new IOException("资源句柄托管失败："+reply.Error);
    }

    public static int Run()
    {
        using var single=new Mutex(false,@"Local\"+Pipe);bool owned;
        try{owned=single.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
        if(!owned)return 0;
        var items=new Dictionary<Guid,Job>();var pools=new Dictionary<Guid,Job>();Job? root=null;
        var boot=Store.BootId();var idle=Stopwatch.StartNew();
        try
        {
            Store.Save(Store.FilePath("custody.json"),new{Pid=Environment.ProcessId,BootId=boot,Started=DateTimeOffset.UtcNow});
            while(true)
            {
                using var pipe=new NamedPipeServerStream(Pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                var connected=pipe.WaitForConnectionAsync();
                while(!connected.Wait(1000))
                {Reap();if(items.Count==0&&pools.Count==0&&idle.Elapsed>TimeSpan.FromSeconds(10))return 0;}
                connected.GetAwaiter().GetResult();
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    var request=Ipc.ReadAsync<KeepRequest>(pipe,timeout.Token).GetAwaiter().GetResult();
                    if(request.AttemptId==Guid.Empty||request.BootId!=boot)throw new IOException("资源句柄身份无效。");
                    root??=new Job(Store.RootJob,openOnly:true);
                    if(request.PoolId is{} poolId&&!pools.ContainsKey(poolId))pools.Add(poolId,new Job(Store.PoolName(poolId),openOnly:true));
                    if(!items.ContainsKey(request.AttemptId))items.Add(request.AttemptId,new Job(Store.JobName(request.AttemptId),openOnly:true));
                    idle.Restart();Ipc.WriteAsync(pipe,new KeepReply(true,""),timeout.Token).GetAwaiter().GetResult();
                }
                catch(Exception e)when(e is not OutOfMemoryException)
                {try{Ipc.WriteAsync(pipe,new KeepReply(false,e.Message),timeout.Token).GetAwaiter().GetResult();}catch(Exception write)when(write is not OutOfMemoryException){}}
                Reap();
            }
        }
        finally{foreach(var job in items.Values.Concat(pools.Values))job.Dispose();root?.Dispose();single.ReleaseMutex();}

        void Reap()
        {
            try
            {
                var ledger=Store.Read<Ledger>(Store.FilePath("control.json"));
                if(ledger is null||ledger.BootId!=boot){idle.Restart();return;}
                foreach(var id in items.Keys.ToArray())
                {
                    var entry=ledger.Entries.SingleOrDefault(e=>e.Plan.Id==id);
                    if(entry is null)
                    {
                        var archived=Store.Read<ArchivedAttempt>(Store.FilePath(Path.Combine("attempts",id.ToString("N"),"outcome.json")));
                        if(archived is {SchemaVersion:1}&&archived.AttemptId==id&&archived.Entry.Plan.Id==id&&archived.Digest==Store.Digest(archived.Entry.Plan))entry=archived.Entry;
                    }
                    if(entry is null||!Store.IsTerminal(entry)||items[id].Members().Length!=0)continue;
                    items[id].Dispose();items.Remove(id);
                }
                foreach(var id in pools.Keys.ToArray())
                {
                    if(ledger.Entries.Any(e=>e.Plan.PoolId==id&&!Store.IsTerminal(e))||pools[id].Members().Length!=0)continue;
                    pools[id].Dispose();pools.Remove(id);
                }
                if(items.Count>0||pools.Count>0)idle.Restart();
                else{root?.Dispose();root=null;}
            }
            catch(Exception e)when(e is not OutOfMemoryException){idle.Restart();}
        }
    }
}
