using System.Text.Json;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

internal static class ReviewCi
{
    internal static CiRun[] Checks(byte[] bytes,string sha)
    {
        using var document=JsonDocument.Parse(bytes);
        var root=document.RootElement;
        if(!root.TryGetProperty("total_count",out var total)||!total.TryGetInt32(out var count)||count>100
            ||!root.TryGetProperty("check_runs",out var checks)||checks.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("GitHub check runs 超出边界或不完整，不能判为通过。");
        if(count<0||checks.GetArrayLength()!=count)throw new InvalidDataException("GitHub check runs 列表不完整。");
        return checks.EnumerateArray().Where(check=>String(check,"head_sha")==sha)
            .Select(check=>(App:check.TryGetProperty("app",out var app)?Number(app,"id"):0,
                Run:new CiRun(Number(check,"id"),String(check,"name"),sha,String(check,"status"),Optional(check,"conclusion"),Optional(check,"html_url")??"")))
            .OrderByDescending(check=>check.Run.Id).GroupBy(check=>(check.App,check.Run.Name)).Select(group=>group.First().Run).ToArray();
    }

    internal static CiRun[] Statuses(byte[] bytes,string sha)
    {
        using var document=JsonDocument.Parse(bytes);
        var root=document.RootElement;
        if(String(root,"sha")!=sha||!root.TryGetProperty("total_count",out var total)||!total.TryGetInt32(out var count)||count>100
            ||!root.TryGetProperty("statuses",out var statuses)||statuses.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("GitHub commit statuses 的提交不匹配或数据不完整。");
        if(count<0||statuses.GetArrayLength()!=count)throw new InvalidDataException("GitHub commit statuses 列表不完整。");
        return statuses.EnumerateArray().Select(status=>
        {
            var state=String(status,"state");
            return new CiRun(Number(status,"id"),String(status,"context"),sha,state=="pending"?"in_progress":"completed",
                state switch{"success"=>"success","failure" or "error"=>"failure",_=>null},Optional(status,"target_url")??"");
        }).OrderByDescending(status=>status.Id).GroupBy(status=>status.Name,StringComparer.Ordinal).Select(group=>group.First()).ToArray();
    }

    private static string String(JsonElement element,string key)=>Optional(element,key)??throw new InvalidDataException("GitHub 数据缺少字段："+key);
    private static string? Optional(JsonElement element,string key)=>element.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
    private static long Number(JsonElement element,string key)=>element.TryGetProperty(key,out var value)&&value.TryGetInt64(out var number)?number:throw new InvalidDataException("GitHub 数据标识无效："+key);
}
