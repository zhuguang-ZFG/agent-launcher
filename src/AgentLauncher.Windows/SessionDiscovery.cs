using System.Text;
using System.Text.Json;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

public sealed record LocalSession(string Reference,DateTimeOffset Modified);
public sealed record SessionSearch(IReadOnlyList<LocalSession> Sessions,string Note);

/// <summary>Read bounded metadata only. Conversation text never enters the picker or diagnostics.</summary>
public static class SessionDiscovery
{
    public static SessionSearch Find(string agentId,string workspace,string? searchRoot=null)
    {
        if(!Agents.IsSupported(agentId))return new([],"此 Agent 类型已移除，不能新开会话或续接。");
        var user=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root=searchRoot??agentId switch
        {
            "codex"=>Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(user,".codex"),"sessions"),
            "claude"=>Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")??Path.Combine(user,".claude"),"projects"),
            "omp"=>Path.Combine(Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR")??Path.Combine(user,".omp","agent"),"sessions"),
            _=>""
        };
        if(root.Length==0)return new([],"此 Agent 尚无本地会话索引适配，可手动填写会话标识。");
        if(!Directory.Exists(root))return new([],"尚未找到本地会话目录，可手动填写或选择会话文件。");
        var results=new List<LocalSession>();var visited=0;var skipped=0;var watch=System.Diagnostics.Stopwatch.StartNew();
        var key=PhysicalDirectory.Workspace(workspace).Key;
        foreach(var file in Directory.EnumerateFiles(root,"*.jsonl",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint,MaxRecursionDepth=5}))
        {
            if(++visited>2000||watch.Elapsed>TimeSpan.FromSeconds(2))return new(results.OrderByDescending(s=>s.Modified).Take(100).ToArray(),"会话较多，本次只显示已核对的部分；也可手动填写。");
            try
            {
                using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                var bytes=new byte[Math.Min(64*1024,stream.Length)];var count=stream.Read(bytes);var text=Encoding.UTF8.GetString(bytes,0,count);
                foreach(var line in text.Split('\n').Take(12))
                {
                    if(line.Length==0)continue;
                    using var json=JsonDocument.Parse(line);var data=json.RootElement;
                    if(data.ValueKind!=JsonValueKind.Object)continue;
                    string? directory;
                    if(agentId=="codex")
                    {
                        if(!data.TryGetProperty("type",out var type)||type.ValueKind!=JsonValueKind.String||type.GetString()!="session_meta"||!data.TryGetProperty("payload",out data)||data.ValueKind!=JsonValueKind.Object)continue;
                        if(!data.TryGetProperty("cwd",out var cwd)||cwd.ValueKind!=JsonValueKind.String)continue;
                        directory=cwd.GetString();
                    }
                    else{if(!data.TryGetProperty("cwd",out var cwd)||cwd.ValueKind!=JsonValueKind.String)continue;directory=cwd.GetString();}
                    if(string.IsNullOrWhiteSpace(directory)||!Path.IsPathFullyQualified(directory)||!Directory.Exists(directory))continue;
                    if(PhysicalDirectory.Workspace(directory).Key!=key)continue;
                    string reference;
                    if(agentId=="omp")reference=Path.GetFullPath(file);
                    else
                    {if(!data.TryGetProperty(agentId=="claude"?"sessionId":"id",out var id)||id.ValueKind!=JsonValueKind.String||!Guid.TryParse(id.GetString(),out var guid))continue;reference=guid.ToString();}
                    if(reference.Length<=200&&!results.Any(s=>s.Reference==reference))results.Add(new(reference,File.GetLastWriteTimeUtc(file)));
                    break;
                }
            }
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception){skipped++;}
        }
        return new(results.OrderByDescending(s=>s.Modified).Take(100).ToArray(),$"仅列出工作目录身份匹配的会话；请按时间核对本次尝试。无法读取或未完整写入的文件：{skipped}。");
    }
}
