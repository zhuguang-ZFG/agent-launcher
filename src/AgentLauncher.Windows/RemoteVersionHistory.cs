using System.Text.Json;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

// C2 远程版本历史（remote-versions.json）：每主机每 agent 的版本记录，CheckVersion/Upgrade 成功后追加。
// 历史是回滚推导的辅助证据而非授权：文件缺失/损坏/未来 schema 时按空历史重建，不阻断维护操作。
public sealed record RemoteVersionBook(int SchemaVersion,Dictionary<string,Dictionary<string,RemoteVersionEntry[]>> Hosts);

public static class RemoteVersionHistory
{
    public static string DefaultPath=>Store.FilePath("remote-versions.json");

    public static RemoteVersionEntry[] For(string path,string hostId,string agent)=>
        ReadBook(path)?.Hosts.GetValueOrDefault(hostId)?.GetValueOrDefault(agent)??[];

    public static void Record(string path,string hostId,string agent,string version,string command)
    {
        var book=ReadBook(path)??new RemoteVersionBook(1,[]);
        if(!book.Hosts.TryGetValue(hostId,out var agents))book.Hosts[hostId]=agents=new Dictionary<string,RemoteVersionEntry[]>(StringComparer.OrdinalIgnoreCase);
        agents[agent]=RemoteAgentMaintenance.AppendVersion(agents.GetValueOrDefault(agent)??[],new RemoteVersionEntry(agent,version,DateTimeOffset.UtcNow,command));
        Store.Save(path,book);
    }

    // 版本解析自 --version 输出首行语义版本；解析失败不记录（fail-closed，不写猜测值）。
    public static void RecordFromVersionOutput(string path,RemoteHost host,string agent,string versionOutput,string command)
    {
        if(RemoteAgentMaintenance.ParseVersion(versionOutput) is {} version)Record(path,host.Id.ToString(),agent,version,command);
    }

    private static RemoteVersionBook? ReadBook(string path)
    {
        try{return Store.Read<RemoteVersionBook>(path) is {SchemaVersion:1} book?book:null;}
        catch(Exception e)when(e is JsonException or InvalidDataException){return null;}
    }
}