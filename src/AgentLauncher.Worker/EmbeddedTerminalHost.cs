using System.Diagnostics;
using System.IO.Pipes;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Worker;

// The existing pane runner owns the claim, Job chain, synchronization and receipts.
// This process owns only its console, so a view can detach while the agent keeps running.
internal sealed class EmbeddedTerminalHost
{
    private readonly TerminalScreen screen = new(100, 28);
    private byte[]? latestFrame;
    private int requestedSize;
    private TerminalReadRequest? requestedRead;
    private byte[]? readbackFrame;
    private int connectedView;
    private int everConnected;
    private int processCompleted;

    public int Run(Guid attempt, bool reattach)
    {
        using var host = new ConPtyHost(100, 28);
        using var stopping = new CancellationTokenSource();
        var server = ServeAsync(attempt, host, stopping.Token);
        try
        {
            if (server.IsCompleted) server.GetAwaiter().GetResult();
            var args = reattach ? new[] { "pane", attempt.ToString(), "reattach" } : new[] { "embedded-pane", attempt.ToString() };
            using var runner = host.Start(new CommandSpec(Store.WorkerPath,args,"exe"), Store.Root, []);
            try
            {
                // 记录宿主身份：PID 加启动时间，供重新打开前核对宿主仍在，防止 PID 复用把死会话当成可重开。
                using var self=Process.GetCurrentProcess();
                Store.Save(Store.FilePath(Path.Combine("attempts",attempt.ToString("N"),"embedded.json")),new{Pid=self.Id,ProcessTime=self.StartTime.ToUniversalTime().ToFileTimeUtc()});
                runner.Resume();
            }
            catch { runner.AbortBeforeResume(); throw; }

            var bytes = new byte[16 * 1024];
            var frameAge = Stopwatch.StartNew();
            var dirty = true;
            do
            {
                var count = host.ReadOutput(bytes,TimeSpan.FromMilliseconds(40),default);
                if (count > 0) { screen.Feed(bytes.AsSpan(0,count)); dirty = true; }
                var size = Interlocked.Exchange(ref requestedSize,0);
                if (size != 0)
                {
                    var cols=(short)(size >> 16); var rows=(short)(size & 0xffff);
                    host.Resize(cols,rows); screen.Resize(cols,rows); dirty=true;
                }
                if (dirty && frameAge.ElapsedMilliseconds >= 50)
                {
                    Publish(); dirty=false; frameAge.Restart();
                }
                PublishReadback();
                if (runner.Process.HasExited && count==0) break;
            } while (!host.OutputClosed);
            runner.Process.WaitForExit();
            Volatile.Write(ref processCompleted,1);
            Publish(runner.Process.ExitCode);
            // 进程已结束，账本正常终结。仅为已打开的视图保留有界输出；关闭视图即释放宿主。
            // 极短任务可能早于首次连接结束，允许与客户端相同的 5 秒初次连接窗口。
            var completionAge=Stopwatch.StartNew();
            while(Volatile.Read(ref connectedView)!=0 || Volatile.Read(ref everConnected)==0&&completionAge.ElapsedMilliseconds<5000)
            {
                var finalSize=Interlocked.Exchange(ref requestedSize,0);
                if(finalSize!=0){screen.Resize(finalSize>>16,finalSize&0xffff);Publish(runner.Process.ExitCode);}
                PublishReadback();Thread.Sleep(40);
            }
            return runner.Process.ExitCode;
        }
        finally
        {
            stopping.Cancel();
            try { server.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
            catch(Exception e) when(e is OperationCanceledException or TimeoutException or IOException) { }
        }
    }

    private void Publish(int? exitCode=null)=>Volatile.Write(ref latestFrame,TerminalWire.Frame(TerminalWire.EncodeSnapshot(screen.Snapshot() with{ExitCode=exitCode})));

    private void PublishReadback()
    {
        var read=Interlocked.Exchange(ref requestedRead,null);
        if(read is not null)Volatile.Write(ref readbackFrame,TerminalWire.Frame(TerminalWire.EncodeReadback(screen.Readback(read))));
    }

    private async Task ServeAsync(Guid id,ConPtyHost host,CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var pipe=new NamedPipeServerStream(TerminalPipeClient.PipeName(id),PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(stop);
            Volatile.Write(ref readbackFrame, null);
            Interlocked.Exchange(ref requestedRead, null);
            Volatile.Write(ref connectedView,1);Volatile.Write(ref everConnected,1);
            using var connected=CancellationTokenSource.CreateLinkedTokenSource(stop);
            try
            {
                var input=ReadInputAsync(pipe,host,connected.Token);
                var output=WriteFramesAsync(pipe,connected.Token);
                await Task.WhenAny(input,output);
                connected.Cancel();pipe.Dispose();
                try { await Task.WhenAll(input,output); }
                catch(Exception e) when(e is IOException or ObjectDisposedException or OperationCanceledException or System.ComponentModel.Win32Exception) { }
            }
            finally{Volatile.Write(ref connectedView,0);}
        }
    }

    private async Task WriteFramesAsync(Stream pipe,CancellationToken ct)
    {
        byte[]? sent=null;
        byte[]? readSent=null;
        while(!ct.IsCancellationRequested)
        {
            var frame=Volatile.Read(ref latestFrame);
            if(frame!=null && !ReferenceEquals(sent,frame)) { await pipe.WriteAsync(frame,ct); sent=frame; }
            var page=Volatile.Read(ref readbackFrame);
            if(page!=null && !ReferenceEquals(readSent,page)) { await pipe.WriteAsync(page,ct); readSent=page; }
            await Task.Delay(40,ct);
        }
    }

    private async Task ReadInputAsync(Stream pipe,ConPtyHost host,CancellationToken ct)
    {
        while(await TerminalWire.ReadFrameAsync(pipe,ct) is {} frame)
        {
            var message=TerminalWire.Decode(frame);
            if(message is {Kind:TerminalWire.InputKind,Input:{} bytes} && bytes.Length<=TerminalPipeClient.MaxInput)
            {if(Volatile.Read(ref processCompleted)==0)host.WriteInput(bytes);}
            else if(message is {Kind:TerminalWire.ResizeKind,Columns:{} cols,Rows:{} rows} && cols is >=TerminalScreen.MinColumns and <=TerminalScreen.MaxColumns && rows is >=TerminalScreen.MinRows and <=TerminalScreen.MaxRows)
                Interlocked.Exchange(ref requestedSize,(cols<<16)|rows);
            else if(message is {Kind:TerminalWire.ReadKind,Request:{IsWellFormed:true} request})Interlocked.Exchange(ref requestedRead,request);
            else break;
        }
    }
}
