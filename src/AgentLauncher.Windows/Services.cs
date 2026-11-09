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
    public static async Task<Response> SendAsync(Request request,CancellationToken ct=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await using var pipe=new NamedPipeClientStream(".",Store.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);await WriteAsync(pipe,request,timeout.Token);
#if INTEGRATION_TESTS
        // Fault injection: the request may already be committed server-side while the client observes a timeout.
        if(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_IPC_DROP_AFTER_SEND")=="1"){Environment.SetEnvironmentVariable("AGENT_LAUNCHER_IPC_DROP_AFTER_SEND",null);throw new TimeoutException("模拟客户端超时（请求已发送）。");}
#endif
        return await ReadAsync<Response>(pipe,timeout.Token);
    }
    public static async Task<Response> ReserveWithCleanupAsync(LaunchItem[] items,CancellationToken ct=default)
    {
        try{return await SendAsync(new("reserve",Guid.NewGuid(),items),ct);}
        catch(Exception e)when(e is IOException or TimeoutException or OperationCanceledException)
        {
            // The server may have committed the reservation before the client saw a failure; revoke best-effort so the workspace is not held.
            foreach(var item in items){try{await SendAsync(new("revoke",Guid.NewGuid(),Attempt:item.Id));}catch(Exception inner)when(inner is IOException or TimeoutException or OperationCanceledException){}}
            throw new IOException("预约结果未确认，已尝试撤销全部启动项；请在运行记录中核对。",e);
        }
    }
    public static async Task WriteAsync<T>(Stream stream,T message,CancellationToken ct)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(message,Store.Json);if(bytes.Length>1024*1024)throw new IOException("请求过大。");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length),ct);await stream.WriteAsync(bytes,ct);await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream,CancellationToken ct)
    {
        var length=new byte[4];await stream.ReadExactlyAsync(length,ct);var size=BitConverter.ToInt32(length);
        if(size is <1 or >1024*1024)throw new IOException("协议帧长度无效。");
        var bytes=new byte[size];await stream.ReadExactlyAsync(bytes,ct);return JsonSerializer.Deserialize<T>(bytes,Store.Json)??throw new IOException("协议帧为空。");
    }
    public static async Task<Response> EnsureMonitorAsync()
    {
        using var probe=new CancellationTokenSource(400);
        try{return await SendAsync(new("status",Guid.NewGuid()),probe.Token);}catch(TimeoutException){}catch(OperationCanceledException){}catch(IOException){}
        if(!File.Exists(Store.WorkerPath))throw new FileNotFoundException("缺少 Worker，请从完整发布目录启动。");
        using var p=Process.Start(new ProcessStartInfo(Store.WorkerPath,"monitor"){UseShellExecute=false,CreateNoWindow=true});
        return await SendAsync(new("status",Guid.NewGuid()));
    }
}
public static class Commands
{
    public static string? FindExe(string name)
    {
        if(Path.IsPathFullyQualified(name))return File.Exists(name)?name:null;
        if(name.IndexOfAny(['\\','/'])>=0)return null;
        foreach(var folder in SearchDirs(null))
            foreach(var ext in Path.HasExtension(name)?new[]{""}:new[]{".exe",".cmd"}) {var path=Path.Combine(folder.Trim('"'),name+ext);if(File.Exists(path))return Path.GetFullPath(path);}
        return null;
    }
    // PATH plus the well-known per-user CLI install locations (npm/pnpm/yarn/scoop/bun globals). Bounded: fixed dirs, no recursion.
    public static IReadOnlyList<string> InstallSearchDirs()
    {
        var dirs=new List<string>();
        void Add(string path){if(Directory.Exists(path))dirs.Add(path);}
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"npm"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"pnpm"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Yarn","bin"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"scoop","shims"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".bun","bin"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".kimi-code","bin"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".local","bin"));
        var kimiDir=Environment.GetEnvironmentVariable("KIMI_INSTALL_DIR");if(!string.IsNullOrWhiteSpace(kimiDir))Add(Path.Combine(kimiDir,"bin"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".qoder","entry"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".pi","agent","bin"));
        return dirs;
    }
    private static IEnumerable<string> SearchDirs(IReadOnlyList<string>? extra)=>((Environment.GetEnvironmentVariable("PATH")??"").Split(';').Where(Path.IsPathFullyQualified)).Concat(extra??InstallSearchDirs());
    // Every existing match for a bare command name across PATH and install dirs, in search order.
    public static IReadOnlyList<string> DiscoverInstalls(string name,IReadOnlyList<string>? extraDirs=null)
    {
        var found=new List<string>();
        if(string.IsNullOrWhiteSpace(name)||name.IndexOfAny(['\\','/'])>=0)return found;
        foreach(var folder in SearchDirs(extraDirs))
            foreach(var ext in Path.HasExtension(name)?new[]{""}:new[]{".exe",".cmd"}) {var path=Path.Combine(folder.Trim('"'),name+ext);if(File.Exists(path)){var full=Path.GetFullPath(path);if(!found.Contains(full))found.Add(full);}}
        return found;
    }
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
        throw new FileNotFoundException("Terminal 执行别名未启用。请在设置中选择已安装的 wt.exe。");
    }
    public static void SubmitTerminal(string terminal,IReadOnlyList<LaunchItem> items,string layout)
    {
        var start=new ProcessStartInfo(terminal){UseShellExecute=false};start.ArgumentList.Add("-w");start.ArgumentList.Add("agent-"+items[0].BatchId.ToString("N"));
        for(var i=0;i<items.Count;i++)
        {
            if(i>0){start.ArgumentList.Add(";");if(items.Count==4&&i>=2){start.ArgumentList.Add("move-focus");start.ArgumentList.Add(i==2?"left":"right");start.ArgumentList.Add(";");}}
            start.ArgumentList.Add(i==0?"new-tab":"split-pane");
            if(i>0)start.ArgumentList.Add(i==1&&(items.Count>2||layout!="horizontal")?"-V":"-H");
            start.ArgumentList.Add("--title");start.ArgumentList.Add($"{items[i].Position} {items[i].Name}");
            start.ArgumentList.Add(Store.WorkerPath);start.ArgumentList.Add("pane");start.ArgumentList.Add(items[i].Id.ToString("D"));
        }
        using var process=Process.Start(start)??throw new IOException("无法提交给 Windows Terminal。");
    }
}
