using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AgentLauncher.Windows;

internal sealed record ProcessCapture(int Code,byte[] Output,string Error,bool TimedOut,bool Truncated);

internal static class ReviewProcess
{
    private static readonly UTF8Encoding Utf8=new(false,false);
    public static async Task<ProcessCapture> CaptureAsync(string exe,string cwd,IReadOnlyList<string> args,CancellationToken ct,int timeoutSeconds=120,int maxOutput=8*1024*1024,IReadOnlyDictionary<string,string>? environment=null,Stream? stdin=null,byte[]? stdinBytes=null)
    {
        ct.ThrowIfCancellationRequested();
        var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=cwd,StandardOutputEncoding=Utf8,StandardErrorEncoding=Utf8};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        if(environment!=null)foreach(var pair in environment)start.Environment[pair.Key]=pair.Value;
        using var process=Process.Start(start)??throw new IOException("无法启动受控进程。");
        Task? inputTask=null;
        if(stdinBytes!=null)inputTask=WriteInputAsync(process,stdinBytes,null,ct);
        else if(stdin!=null)inputTask=WriteInputAsync(process,null,stdin,ct);
        else process.StandardInput.Close();
        var outputSink=new OutputSink();var errorSink=new OutputSink();
        var outputTask=ReadBoundedAsync(process.StandardOutput.BaseStream,maxOutput,process,outputSink);
        var errorTask=ReadBoundedAsync(process.StandardError.BaseStream,256*1024,process,errorSink);
        var timedOut=false;
        using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds,1,3600)));
            try{await process.WaitForExitAsync(deadline.Token);}
            catch(OperationCanceledException)when(!ct.IsCancellationRequested)
            {
                timedOut=true;Kill(process);await WaitQuietAsync(process);
            }
            catch(OperationCanceledException)
            {
                Kill(process);await WaitQuietAsync(process);throw new OperationCanceledException("操作已取消。",ct);
            }
        }
        if(!process.HasExited)Kill(process);
        if(inputTask!=null)await inputTask;
        // A surviving descendant (e.g. an auto-spawned mutagen daemon) can inherit the pipes and
        // withhold EOF after the captured process exits; drain briefly, then return what arrived.
        var drain=Task.WhenAll(outputTask,errorTask);
        await Task.WhenAny(drain,Task.Delay(2000));
        byte[] output;bool truncated;string error;bool errorTruncated;
        lock(outputSink.Gate){output=outputSink.Bytes.ToArray();truncated=outputSink.Truncated;}
        lock(errorSink.Gate){error=Utf8.GetString(errorSink.Bytes.ToArray());errorTruncated=errorSink.Truncated;}
        if(errorTruncated)error+="\n…（错误输出已截断）";
        return new(process.HasExited?process.ExitCode:-1,output,error,timedOut,truncated);
    }
    private sealed class OutputSink
    {
        public readonly object Gate=new();
        public readonly MemoryStream Bytes=new();
        public bool Truncated;
    }
    private static async Task WriteInputAsync(Process process,byte[]? bytes,Stream? stream,CancellationToken ct)
    {
        try
        {
            if(bytes!=null)await process.StandardInput.BaseStream.WriteAsync(bytes,ct);
            else if(stream!=null)await stream.CopyToAsync(process.StandardInput.BaseStream,ct);
            await process.StandardInput.BaseStream.FlushAsync(ct);
        }
        catch(Exception e)when(e is IOException or ObjectDisposedException){}
        finally{try{process.StandardInput.Close();}catch(IOException){}catch(ObjectDisposedException){}}
    }
    private static async Task ReadBoundedAsync(Stream stream,int max,Process process,OutputSink sink)
    {
        var buffer=new byte[64*1024];
        try
        {
            int count;
            while((count=await stream.ReadAsync(buffer))>0)
            {
                lock(sink.Gate)
                {
                    if(sink.Bytes.Length+count<=max)sink.Bytes.Write(buffer,0,count);
                    else{sink.Truncated=true;Kill(process);break;}
                }
            }
        }
        catch(Exception e)when(e is IOException or ObjectDisposedException){}
    }
    private static void Kill(Process process)
    {
        try{if(!process.HasExited)process.Kill(true);}catch(Exception e)when(e is not OutOfMemoryException){}
    }
    private static async Task WaitQuietAsync(Process process)
    {
        try{await process.WaitForExitAsync();}catch(Exception e)when(e is IOException or InvalidOperationException){}
    }
}

internal static class ReviewGit
{
    private static readonly string[] Config=["-c","core.quotepath=false","-c","core.pager=cat","-c","core.fsmonitor=false","-c","advice.detachedHead=false"];
    private static readonly Dictionary<string,string> GitEnvironment=new(StringComparer.OrdinalIgnoreCase){{"GIT_TERMINAL_PROMPT","0"},{"GIT_PAGER","cat"},{"GCM_INTERACTIVE","never"},{"LC_ALL","C"},{"GIT_OPTIONAL_LOCKS","0"},{"GIT_LITERAL_PATHSPECS","1"}};
    public static Task<ProcessCapture> CaptureAsync(string git,string cwd,IReadOnlyList<string> args,CancellationToken ct,int timeoutSeconds=120,int maxOutput=8*1024*1024,IReadOnlyDictionary<string,string>? environment=null,Stream? stdin=null,byte[]? stdinBytes=null)
    {
        var full=new List<string>(Config.Length+args.Count);full.AddRange(Config);full.AddRange(args);
        var merged=new Dictionary<string,string>(GitEnvironment,StringComparer.OrdinalIgnoreCase);
        if(environment!=null)foreach(var pair in environment)merged[pair.Key]=pair.Value;
        return ReviewProcess.CaptureAsync(git,cwd,full,ct,timeoutSeconds,maxOutput,merged,stdin,stdinBytes);
    }
    /// <summary>Hashes the exact bytes piped into <c>git hash-object -w --stdin</c>, so a commit can only reference reviewed content.</summary>
    public static async Task<(string Blob,string Hash)> HashObjectAsync(string git,string cwd,string fullPath,CancellationToken ct)
    {
        using var file=new FileStream(fullPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,128*1024,FileOptions.SequentialScan);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string blob;
        using(var hashing=new HashingStream(file,hash))
        {
            var result=await CaptureAsync(git,cwd,["hash-object","-w","--stdin"],ct,120,4096,stdin:hashing);
            if(result.TimedOut)throw new TimeoutException("写入 Git 对象超时。");
            if(result.Code!=0)throw new IOException("无法写入 Git 对象："+Clean(result.Error));
            blob=Encoding.ASCII.GetString(result.Output).Trim();
        }
        if(blob.Length is not (40 or 64)||!blob.All(Uri.IsHexDigit))throw new IOException("Git 返回的对象身份无效。");
        return (blob,Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
    public static (string Hash,long Size,bool Binary) HashFile(string fullPath,long maxBytes)
    {
        using var file=new FileStream(fullPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,128*1024,FileOptions.SequentialScan);
        if(file.Length>maxBytes)throw new IOException("工作区变更内容超过审查边界（1 GiB），已停止。");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer=new byte[128*1024];var prefix=new byte[8192];var prefixLength=0;var binary=false;long total=0;int count;
        while((count=file.Read(buffer,0,buffer.Length))>0)
        {
            hash.AppendData(buffer,0,count);
            if(prefixLength<prefix.Length){var copy=Math.Min(count,prefix.Length-prefixLength);Array.Copy(buffer,0,prefix,prefixLength,copy);prefixLength+=copy;}
            total+=count;
            if(total>maxBytes)throw new IOException("工作区变更内容超过审查边界（1 GiB），已停止。");
        }
        for(var index=0;index<prefixLength;index++)if(prefix[index]==0){binary=true;break;}
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),total,binary);
    }
    public static string Clean(string error)
    {
        var text=error.Replace('\r',' ').Replace('\n',' ').Trim();
        if(text.Length>800)text=text[..800]+"…";
        return text;
    }
    private sealed class HashingStream(Stream inner,IncrementalHash hash):Stream
    {
        public override bool CanRead=>true;
        public override bool CanSeek=>false;
        public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();
        public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count){var read=inner.Read(buffer,offset,count);if(read>0)hash.AppendData(buffer,offset,read);return read;}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default){var read=await inner.ReadAsync(buffer,ct);if(read>0)hash.AppendData(buffer.Span[..read]);return read;}
        public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken ct)=>ReadAsync(buffer.AsMemory(offset,count),ct).AsTask();
        public override void Flush(){}
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
    }
}
