using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

/// <summary>Independent, versioned review book. Corrupt or future files are never overwritten.</summary>
public sealed class ReviewStore
{
    private const int CurrentSchema=1;
    private const int MaxProfiles=100;
    private const int MaxResults=100;
    private const long MaxFileBytes=8L*1024*1024;
    private const int MaxStoredLog=64*1024;
    private static readonly string[] ResultStates=["Passed","Failed","Cancelled","TimedOut","Stale"];
    private readonly string path;
    public ReviewStore(string? path=null)=>this.path=Path.GetFullPath(path??Path.Combine(Store.Root,"review.json"));
    public string FilePath=>path;
    public ReviewBook Load()
    {
        EnsureBounded();
        JsonElement root;
        try{root=Store.Read<JsonElement>(path);}
        catch(InvalidDataException){throw;}
        catch(Exception e)when(e is JsonException or NotSupportedException){throw new InvalidDataException("审查记录损坏，已拒绝读取；原文件未被覆盖。",e);}
        if(root.ValueKind==JsonValueKind.Undefined)return ValidateBook(new ReviewBook());
        if(root.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("审查记录结构不完整，已拒绝读取；原文件未被覆盖。");
        RequireProperty(root,"SchemaVersion",JsonValueKind.Number);
        RequireProperty(root,"Profiles",JsonValueKind.Array);
        RequireProperty(root,"Results",JsonValueKind.Array);
        ReviewBook book;
        try{book=root.Deserialize<ReviewBook>(Store.Json)??throw new InvalidDataException("审查记录为空，已拒绝读取；原文件未被覆盖。");}
        catch(InvalidDataException){throw;}
        catch(Exception e)when(e is JsonException or NotSupportedException){throw new InvalidDataException("审查记录损坏，已拒绝读取；原文件未被覆盖。",e);}
        return ValidateBook(book);
    }
    public void SaveProfile(VerificationProfile profile)
    {
        ReviewPolicy.ValidateProfile(profile);
        Mutate(book=>
        {
            var existing=book.Profiles.FindIndex(candidate=>candidate.Id==profile.Id);
            if(existing>=0)book.Profiles[existing]=profile;
            else
            {
                if(book.Profiles.Count>=MaxProfiles)throw new IOException($"命名检查数量已达上限（{MaxProfiles}）。");
                book.Profiles.Add(profile);
            }
        });
    }
    public void SaveResult(VerificationResult result)
    {
        ValidateResult(result);
        var bounded=result with{Output=BoundLog(result.Output),Error=BoundLog(result.Error),At=result.At==default?DateTimeOffset.UtcNow:result.At};
        Mutate(book=>
        {
            var existing=book.Results.FindIndex(candidate=>candidate.Id==bounded.Id);
            if(existing>=0)book.Results[existing]=bounded;
            else book.Results.Add(bounded);
            if(book.Results.Count>MaxResults)book.Results.RemoveRange(0,book.Results.Count-MaxResults);
        });
    }
    private void Mutate(Action<ReviewBook> change)
    {
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];
        using var mutex=new Mutex(false,@"Local\AgentLauncher-review-"+Store.Scope+"-"+key);
        bool owned;
        try{owned=mutex.WaitOne(5000);}catch(AbandonedMutexException){owned=true;}
        if(!owned)throw new IOException("审查记录正在被另一个窗口修改，请稍后重试。");
        try
        {
            var book=Load();
            change(book);
            Store.Save(path,ValidateBook(book));
        }
        finally{mutex.ReleaseMutex();}
    }
    private static void RequireProperty(JsonElement root,string name,JsonValueKind kind)
    {
        if(!root.TryGetProperty(name,out var value)||value.ValueKind!=kind)throw new InvalidDataException($"审查记录缺少必需的 {name} 字段或类型无效，已拒绝读取；原文件未被覆盖。");
    }
    private void EnsureBounded()
    {
        try
        {
            var info=new FileInfo(path);
            if(info.Exists&&info.Length>MaxFileBytes)throw new InvalidDataException($"审查记录超出大小上限（{MaxFileBytes} 字节），已拒绝读取；原文件未被覆盖。");
        }
        catch(InvalidDataException){throw;}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){throw new InvalidDataException("审查记录无法读取，已拒绝读取；原文件未被覆盖。",e);}
    }
    private static ReviewBook ValidateBook(ReviewBook book)
    {
        if(book is null)throw new InvalidDataException("审查记录为空，已拒绝读取；原文件未被覆盖。");
        if(book.SchemaVersion!=CurrentSchema)throw new InvalidDataException($"审查记录版本 {book.SchemaVersion} 不受支持，已拒绝读取；原文件未被覆盖。");
        if(book.Profiles is null||book.Results is null)throw new InvalidDataException("审查记录结构不完整，已拒绝读取；原文件未被覆盖。");
        if(book.Profiles.Count>MaxProfiles)throw new InvalidDataException($"命名检查数量超出上限（{MaxProfiles}），已拒绝读取；原文件未被覆盖。");
        if(book.Results.Count>MaxResults)throw new InvalidDataException($"检查结果数量超出上限（{MaxResults}），已拒绝读取；原文件未被覆盖。");
        var profiles=new HashSet<Guid>();
        foreach(var profile in book.Profiles)
        {
            if(profile is null)throw new InvalidDataException("审查记录包含空的命名检查，已拒绝读取；原文件未被覆盖。");
            if(!profiles.Add(profile.Id))throw new InvalidDataException("审查记录包含重复的命名检查身份，已拒绝读取；原文件未被覆盖。");
            if(profile.Command is null||profile.Command.Arguments is null)throw new InvalidDataException("审查记录包含无效的检查命令，已拒绝读取；原文件未被覆盖。");
            ReviewPolicy.ValidateProfile(profile);
        }
        var results=new HashSet<Guid>();
        foreach(var result in book.Results)
        {
            if(result is null)throw new InvalidDataException("审查记录包含空的检查结果，已拒绝读取；原文件未被覆盖。");
            if(!results.Add(result.Id))throw new InvalidDataException("审查记录包含重复的检查结果身份，已拒绝读取；原文件未被覆盖。");
            ValidateResult(result);
        }
        return book;
    }
    private static void ValidateResult(VerificationResult result)
    {
        if(result is null)throw new InvalidDataException("检查结果为空。");
        if(result.Id==Guid.Empty||result.TaskRunId==Guid.Empty)throw new InvalidDataException("检查结果缺少身份。");
        if(string.IsNullOrEmpty(result.WorkspaceKey))throw new InvalidDataException("检查结果缺少工作区物理身份。");
        if(result.HeadSha is not{Length:40 or 64}||!result.HeadSha.All(Uri.IsHexDigit))throw new InvalidDataException("检查结果缺少完整提交 SHA。");
        if(string.IsNullOrEmpty(result.WorktreeFingerprint)||string.IsNullOrEmpty(result.CommandFingerprint))throw new InvalidDataException("检查结果缺少内容/命令指纹。");
        if(!ResultStates.Contains(result.State))throw new InvalidDataException("检查结果状态不受支持。");
        if(result.DurationMilliseconds<0)throw new InvalidDataException("检查结果时长无效。");
        if(result.Output is null||result.Error is null)throw new InvalidDataException("检查结果日志缺失。");
    }
    private static string BoundLog(string value)
    {
        if(value.Length<=MaxStoredLog)return value;
        const string marker="…（日志已截断）";
        return marker+value[^(MaxStoredLog-marker.Length)..];
    }
}
