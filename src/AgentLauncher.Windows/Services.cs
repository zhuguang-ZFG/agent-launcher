using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;
public static class Store
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented=false, Converters={new JsonStringEnumConverter()} };
#if INTEGRATION_TESTS
    public static string Root { get; } = Path.GetFullPath(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT") is { Length:>0 } test ? test : File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"test-root.txt")).Trim());
#else
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AgentLauncher");
#endif
    public static string Scope { get; }=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value+Root)))[..20];
    public static string Pipe => "AgentLauncher-"+Scope;
    public static string RootJob => @"Local\AgentLauncher-"+Scope+"-cpu";
    public static string JobName(Guid id)=>@"Local\AgentLauncher-"+Scope+"-"+id.ToString("N");
    public static string AttemptLock(Guid id)=>JobName(id)+"-lock";
    public static string ReceiptPath(Guid id)=>Path.Combine(Root,"attempts",id.ToString("N"),"receipt.json");
    public static string WorkerPath=>Path.Combine(AppContext.BaseDirectory,"AgentLauncher.Worker.exe");
    public static string FilePath(string name)=>Path.Combine(Root,name);
    public static void Save<T>(string path,T data) => WithFileLock(path,()=>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) {JsonSerializer.Serialize(stream,data,Json);stream.Flush(true);}
            RetrySharing(()=>{if(File.Exists(path))File.Replace(temp,path,path+".bak");else File.Move(temp,path);return true;});
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
        return true;
    });
    public static T? Read<T>(string path) => WithFileLock(path,()=>
    {
        try{return RetrySharing(()=>
        {
            // Writers only publish closed temp files; allow rename/metadata write access during ReplaceFile.
            using var stream=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(stream.Length>8*1024*1024)throw new InvalidDataException("记录超出大小边界。");
            return JsonSerializer.Deserialize<T>(stream,Json)??throw new InvalidDataException("记录为空。");
        });}
        catch(FileNotFoundException){return default(T);}
        catch(DirectoryNotFoundException){return default(T);}
    });
    private static T WithFileLock<T>(string path,Func<T> operation)
    {
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        using var mutex=new Mutex(false,@"Local\AgentLauncher-file-"+key);bool owned;
        try{owned=mutex.WaitOne(2000);}catch(AbandonedMutexException){owned=true;}
        if(!owned)throw new IOException("记录正在写入，请稍后核对。");
        try{return operation();}finally{mutex.ReleaseMutex();}
    }
    private static T RetrySharing<T>(Func<T> operation)
    {
        for(var attempt=0;;attempt++)
        {
            try{return operation();}
            catch(IOException e)when(attempt<5&&(e.HResult&0xffff) is 32 or 33){Thread.Sleep(10);}
        }
    }
    public static string Digest(LaunchItem item)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(item,Json)));
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int kind,IntPtr buffer,int size,out int returned);
    public static string BootId()
    {
        var p=Marshal.AllocHGlobal(32);try{if(NtQuerySystemInformation(90,p,32,out _)!=0)throw new IOException("无法读取系统启动身份。");return Marshal.PtrToStructure<Guid>(p).ToString("N");}finally{Marshal.FreeHGlobal(p);}
    }
    public static bool IsTerminal(ControlEntry e)=>e.State is "Revoked" or "Settled" or "FailedBeforeStart";
}
public static class Ipc
{
    public static Task<Response> SendAsync(Request request,CancellationToken ct=default)=>SendCoreAsync(request,ct,false);
    private static async Task<Response> SendCoreAsync(Request request,CancellationToken ct,bool ensureMonitor)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await using var pipe=new NamedPipeClientStream(".",Store.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        if(ensureMonitor)
        {
            using var connect=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);connect.CancelAfter(400);
            try{await pipe.ConnectAsync(connect.Token);}
            catch(OperationCanceledException)when(!timeout.IsCancellationRequested)
            {
                // A busy single-instance pipe also times out here, so only start a monitor when the pid file does not name a live
                // Worker. No request bytes have been written either way, so this can never replay an authorization.
                if(!MonitorRunning())StartMonitor();
                return await SendCoreAsync(request,timeout.Token,false);
            }
        }
        else await pipe.ConnectAsync(timeout.Token);
        await WriteAsync(pipe,request,timeout.Token);
#if INTEGRATION_TESTS
        // Fault injection: the request may already be committed server-side while the client observes a timeout.
        if(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_IPC_DROP_AFTER_SEND")=="1"){Environment.SetEnvironmentVariable("AGENT_LAUNCHER_IPC_DROP_AFTER_SEND",null);throw new TimeoutException("模拟客户端超时（请求已发送）。");}
#endif
        return await ReadResponseAsync(pipe,timeout.Token);
    }
    public static async Task<Response> ReserveWithCleanupAsync(LaunchItem[] items,CancellationToken ct=default,Action? beforeCleanup=null)
    {
        try{return await SendCoreAsync(new("reserve",Guid.NewGuid(),items),ct,true);}
        catch(Exception e)when(e is IOException or TimeoutException or OperationCanceledException)
        {
            // The server may have committed the reservation before the client saw a failure; revoke best-effort so the workspace is not held.
            beforeCleanup?.Invoke();
            foreach(var item in items){try{await SendAsync(new("revoke",Guid.NewGuid(),Attempt:item.Id));}catch(Exception inner)when(inner is IOException or TimeoutException or OperationCanceledException){}}
            throw new IOException("预约结果未确认，已尝试撤销全部启动项；请在运行记录中核对。",e);
        }
    }
    public const int FrameLimit=1024*1024;
    private const int MaxResponseFrames=64;
    private const long MaxResponseBytes=16L*1024*1024;
    public static async Task WriteResponseAsync(Stream stream,Response response,CancellationToken ct)
    {
        // Validate every frame before the first write so a failure never publishes a partial page set.
        foreach(var frame in SplitResponse(response))await WriteFrameAsync(stream,frame,ct);
    }
    public static async Task<Response> ReadResponseAsync(Stream stream,CancellationToken ct)
    {
        var firstFrame=await ReadFrameAsync<Response>(stream,ct);var first=firstFrame.Message;if(!first.More)return first;
        if(first.Entries is not {Length:>0})throw new IOException("协议分页帧缺少记录。");
        var entries=new List<ControlEntry>(first.Entries!);var frames=1;long bytes=firstFrame.Size;var current=first;
        while(current.More)
        {
            if(++frames>MaxResponseFrames)throw new IOException("协议分页帧数量超出边界。");
            var nextFrame=await ReadFrameAsync<Response>(stream,ct);var next=nextFrame.Message;
            if(next.Ok!=first.Ok||next.Message!=first.Message||next.Memory!=first.Memory||next.Epoch!=first.Epoch||next.AdmissionBlockedReason!=first.AdmissionBlockedReason)throw new IOException("协议分页帧不一致，已拒绝混合响应。");
            if(next.Entries is not {Length:>0})throw new IOException("协议分页帧缺少记录。");
            bytes+=nextFrame.Size;
            if(bytes>MaxResponseBytes)throw new IOException("协议分页响应超出边界。");
            entries.AddRange(next.Entries!);current=next;
        }
        return first with{Entries=[..entries],More=false};
    }
    public static bool EntryRepresentable(ControlEntry entry)
    {
        // One record must fit with a conservative status envelope; otherwise Save must fail closed before any grant exists.
        // Reserve room for the sample and bounded failure/omission messages as well as the entry itself.
        var probe=new Response(true,new string('x',4096),[entry],null,new string('0',64),new string('r',64),false);
        return JsonSerializer.SerializeToUtf8Bytes(probe,Store.Json).Length<=FrameLimit;
    }
    public static void EnsureEntryRepresentable(ControlEntry entry)
    {
        if(!EntryRepresentable(entry))throw new IOException("控制记录参数过大，无法在协议边界内完整传输；已拒绝保存或读取该记录。");
    }
    private static List<byte[]> SplitResponse(Response response)
    {
        var single=JsonSerializer.SerializeToUtf8Bytes(response with{More=false},Store.Json);if(single.Length<=FrameLimit)return [single];
        if(response.Entries is not {Length:>0})throw new IOException("响应超出单帧边界且没有可分页记录。");
        var header=response with{Entries=null,More=true};var frames=new List<Response>();var chunk=new List<ControlEntry>();
        var empty=JsonSerializer.SerializeToUtf8Bytes(header,Store.Json).LongLength;var bytes=empty;
        foreach(var entry in response.Entries!)
        {
            var size=JsonSerializer.SerializeToUtf8Bytes(entry,Store.Json).LongLength+8;
            if(chunk.Count>0&&bytes+size>FrameLimit){frames.Add(header with{Entries=chunk.ToArray()});chunk.Clear();bytes=empty;}
            chunk.Add(entry);bytes+=size;
        }
        frames.Add(header with{Entries=chunk.ToArray(),More=false});
        var encoded=new List<byte[]>(frames.Count);long total=0;
        foreach(var frame in frames){var data=JsonSerializer.SerializeToUtf8Bytes(frame,Store.Json);if(data.Length>FrameLimit)throw new IOException("控制记录参数过大，无法在协议边界内完整传输；已拒绝保存或读取该记录。");total+=data.Length;encoded.Add(data);}
        if(encoded.Count>MaxResponseFrames||total>MaxResponseBytes)throw new IOException("协议分页响应超出边界。");return encoded;
    }
    public static Task WriteAsync<T>(Stream stream,T message,CancellationToken ct)=>WriteFrameAsync(stream,JsonSerializer.SerializeToUtf8Bytes(message,Store.Json),ct);
    private static async Task WriteFrameAsync(Stream stream,byte[] bytes,CancellationToken ct)
    {
        if(bytes.Length>FrameLimit)throw new IOException("请求过大。");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length),ct);await stream.WriteAsync(bytes,ct);await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream,CancellationToken ct)=>(await ReadFrameAsync<T>(stream,ct)).Message;
    private static async Task<(T Message,int Size)> ReadFrameAsync<T>(Stream stream,CancellationToken ct)
    {
        var length=new byte[4];await stream.ReadExactlyAsync(length,ct);var size=BitConverter.ToInt32(length);
        if(size is <1 or >FrameLimit)throw new IOException("协议帧长度无效。");
        var bytes=new byte[size];await stream.ReadExactlyAsync(bytes,ct);return (JsonSerializer.Deserialize<T>(bytes,Store.Json)??throw new IOException("协议帧为空。"),size);
    }
    public static async Task<Response> EnsureMonitorAsync()
    {
        using var probe=new CancellationTokenSource(400);
        try{return await SendAsync(new("status",Guid.NewGuid()),probe.Token);}catch(TimeoutException){}catch(OperationCanceledException){}catch(IOException){}
        if(!MonitorRunning())StartMonitor();
        return await SendAsync(new("status",Guid.NewGuid()));
    }
    private static bool MonitorRunning()
    {
        // Deliberately avoids Store.Read: that helper waits up to 2 s for a file mutex, and this probe only decides whether to spawn.
        try
        {
            var path=Store.FilePath("monitor-pid.json");
            using var file=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(file.Length is <1 or >32)return false;
            using var reader=new StreamReader(file);
            if(!int.TryParse(reader.ReadToEnd().Trim(),out var pid)||pid<=0)return false;
            using var process=Process.GetProcessById(pid);
            return !process.HasExited&&StringComparer.OrdinalIgnoreCase.Equals(process.MainModule?.FileName,Store.WorkerPath);
        }
        catch(Exception e)when(e is not OutOfMemoryException){return false;}
    }
    private static void StartMonitor()
    {
        if(!File.Exists(Store.WorkerPath))throw new FileNotFoundException("缺少 Worker，请从完整发布目录启动。");
        // Test-only evidence counts spawn attempts, including a child that immediately loses the single-instance mutex.
#if INTEGRATION_TESTS
        var log=Environment.GetEnvironmentVariable("AGENT_LAUNCHER_MONITOR_SPAWN_LOG");
        if(!string.IsNullOrEmpty(log))File.AppendAllText(log,"monitor\n");
#endif
        using var p=Process.Start(new ProcessStartInfo(Store.WorkerPath,"monitor"){UseShellExecute=false,CreateNoWindow=true});
    }
}
public static class Commands
{
    private static readonly string[] ExecutableExtensions=[".exe",".cmd"];
    public static string? FindExe(string name)=>EnumerateInstalls(name,null).FirstOrDefault();
    // PATH plus the well-known per-user CLI install locations (npm/pnpm/yarn/scoop/bun globals). Bounded: fixed dirs, no recursion.
    public static IReadOnlyList<string> InstallSearchDirs()
    {
        var dirs=new List<string>();
        void Add(string path){if(Path.IsPathFullyQualified(path)&&Directory.Exists(path))dirs.Add(path);}
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"npm"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"pnpm"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Yarn","bin"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"scoop","shims"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".bun","bin"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".kimi-code","bin"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".local","bin"));
        var kimiDir=Environment.GetEnvironmentVariable("KIMI_INSTALL_DIR");if(!string.IsNullOrWhiteSpace(kimiDir))Add(Path.Combine(kimiDir,"bin"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".qoder","bin","qodercli"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".pi","agent","bin"));
        return dirs;
    }
    private static IEnumerable<string> SearchDirs(IReadOnlyList<string>? extra)
    {
        foreach(var value in (Environment.GetEnvironmentVariable("PATH")??"").Split(';').Concat(extra??InstallSearchDirs()))
        {
            var folder=value.Trim().Trim('"');
            if(Path.IsPathFullyQualified(folder))yield return folder;
        }
    }
    // Resolve only explicit paths or fixed search directories; never search the current project implicitly.
    private static IEnumerable<string> EnumerateInstalls(string name,IReadOnlyList<string>? extra)
    {
        if(string.IsNullOrWhiteSpace(name))yield break;
        if(Path.IsPathFullyQualified(name)){if(File.Exists(name))yield return Path.GetFullPath(name);yield break;}
        if(name.IndexOfAny(['\\','/'])>=0)yield break;
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var hasExtension=Path.HasExtension(name);
        foreach(var folder in SearchDirs(extra))
            for(var i=0;i<(hasExtension?1:ExecutableExtensions.Length);i++)
            {
                var path=Path.Combine(folder,hasExtension?name:name+ExecutableExtensions[i]);
                if(File.Exists(path)){var full=Path.GetFullPath(path);if(seen.Add(full))yield return full;}
            }
    }
    public static IReadOnlyList<string> DiscoverInstalls(string name,IReadOnlyList<string>? extraDirs=null)=>EnumerateInstalls(name,extraDirs).ToArray();
    public static CommandSpec Resolve(string agentId,AgentProfile profile)
    {
        var input=string.IsNullOrWhiteSpace(profile.Executable)?Agents.Get(agentId).Command:profile.Executable;
        var path=FindExe(input)??throw new FileNotFoundException($"未找到 {Agents.Get(agentId).Name} 命令，请选择程序位置。");
        if(Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase))return new(path,profile.Arguments,"exe");
        if(!Path.GetExtension(path).Equals(".cmd",StringComparison.OrdinalIgnoreCase))throw new IOException("首版只支持 exe 和已识别的 Node 启动脚本。");
        if(new FileInfo(path).Length>8192)throw new IOException("启动脚本超出适配范围。");
        var text=File.ReadAllText(path).Replace("\r\n","\n").Trim();var folder=Path.GetDirectoryName(path)!;
        // Full-template matching avoids translating arbitrary wrappers with hidden setup semantics.
        string? script=null;
        const string piShim="""
@ECHO off
SETLOCAL
SET "node=node"
"%node%" "%~dp0pi-launcher.js" %*
SET "pi_exit_code=%ERRORLEVEL%"
IF "%pi_exit_code%"=="9009" (
  IF "%node%"=="node" (
    "%SystemRoot%\System32\where.exe" /Q node
    IF ERRORLEVEL 1 GOTO node_missing
  ) ELSE IF NOT EXIST "%node%" GOTO node_missing
)
EXIT /B %pi_exit_code%
:node_missing
ECHO Pi could not find Node.js. Install Node.js 22.19.0 or newer, or rerun the installer at https://pi.dev. 1>&2
EXIT /B %pi_exit_code%
""";
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
        var npmStyle=false;
        if(Path.GetFileName(path).Equals("pi.cmd",StringComparison.OrdinalIgnoreCase)&&text==piShim.Replace("\r\n","\n"))script=Path.Combine(folder,"pi-launcher.js");
        else
        {
            npmStyle=true;
            var matches=System.Text.RegularExpressions.Regex.Matches(text,"\"%dp0%\\\\([^\"]+\\.(?:js|cjs|mjs))\"");
            var candidates=matches.Select(m=>m.Groups[1].Value).Distinct().ToArray();
            if(candidates.Length==1&&text==npmShim.Replace("{ENTRY}",candidates[0]).Replace("\r\n","\n"))script=Path.GetFullPath(Path.Combine(folder,candidates[0]));
        }
        if(script==null||!File.Exists(script))throw new IOException("脚本适配未识别。请选择实际 exe，或选择 Node.exe 并逐项填写入口脚本与参数。");
        // The npm shim itself prefers a sibling node.exe; the Pi shim always resolves node through PATH. Mirror each template's own selection semantics.
        var node=npmStyle&&File.Exists(Path.Combine(folder,"node.exe"))?Path.Combine(folder,"node.exe"):FindExe("node.exe")??throw new FileNotFoundException("未找到 Node.js。");
        return new(node,[script,..profile.Arguments],"node-shim");
    }
    public static string Terminal(string explicitPath)
    {
        if(!string.IsNullOrWhiteSpace(explicitPath)){if(File.Exists(explicitPath))return explicitPath;throw new FileNotFoundException("Terminal 路径不存在。");}
        var alias=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WindowsApps","wt.exe");
        if(File.Exists(alias))return alias;
        return TerminalDiscovery.Installs().FirstOrDefault()??throw new FileNotFoundException("未找到可用的 Terminal 入口。请启用执行别名，或在设置中选择已安装的 WindowsTerminal.exe / wt.exe。");
    }
    public static void SubmitTerminal(string terminal,IReadOnlyList<LaunchItem> items,string layout)
    {
        var start=new ProcessStartInfo(terminal){UseShellExecute=false};start.ArgumentList.Add("-w");start.ArgumentList.Add("agent-"+items[0].BatchId.ToString("N"));
        if(items.Count>1)start.ArgumentList.Add("--maximized");
        var useSystemColors=TerminalAppearance.UseSystemColors;
        for(var i=0;i<items.Count;i++)
        {
            if(i>0){start.ArgumentList.Add(";");if(items.Count==4&&i>=2){start.ArgumentList.Add("move-focus");start.ArgumentList.Add(i==2?"left":"right");start.ArgumentList.Add(";");}}
            start.ArgumentList.Add(i==0?"new-tab":"split-pane");
            if(i>0){start.ArgumentList.Add(i==1&&(items.Count>2||layout!="horizontal")?"-V":"-H");start.ArgumentList.Add("--size");start.ArgumentList.Add("0.5");}
            TerminalAppearance.AddPaneArguments(start,items[i],useSystemColors);
            start.ArgumentList.Add(Store.WorkerPath.Replace(";","\\;"));start.ArgumentList.Add("pane");start.ArgumentList.Add(items[i].Id.ToString("D"));
        }
        start.ArgumentList.Add(";");start.ArgumentList.Add("focus-pane");start.ArgumentList.Add("--target");start.ArgumentList.Add("0");
        using var process=Process.Start(start)??throw new IOException("无法提交给 Windows Terminal。");
    }
}
