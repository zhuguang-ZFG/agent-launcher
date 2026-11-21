using System.Text.Json;
using AgentLauncher.ManualRelay;

if (args.Any(arg => arg is "--listen" or "--poll" or "--webhook"))
{
    Console.Error.WriteLine("本原型不监听、不轮询、不连接 webhook。");
    return 2;
}

string? store = null, workspace = null, holder = null, task = null;
var ttlSeconds = 300;
try
{
    for (var i = 0; i < args.Length; i++)
    {
        var flag = args[i];
        if (i + 1 >= args.Length) throw new InvalidDataException("缺少参数值。");
        var value = args[++i];
        switch (flag)
        {
            case "--store": store = value; break;
            case "--workspace": workspace = value; break;
            case "--holder": holder = value; break;
            case "--task": task = value; break;
            case "--ttl-seconds": ttlSeconds = int.Parse(value); break;
            default: Console.Error.WriteLine("无法识别的参数。"); return 2;
        }
    }
}
catch (Exception e) when (e is not OutOfMemoryException)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

if (store is null || workspace is null || holder is null)
{
    Console.Error.WriteLine("需要 --store、--workspace 和 --holder。");
    return 2;
}

var report = new ManualSession().RunOnce(new LocalRelay(store, () => DateTimeOffset.UtcNow), workspace, holder, TimeSpan.FromSeconds(ttlSeconds), task);
Console.WriteLine(JsonSerializer.Serialize(report, RelayJson.Options));
return report.Code is "submitted" or "duplicate" or "needs_agent" or "idle" ? 0 : 1;
