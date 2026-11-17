namespace AgentLauncher.Core;

public static class TaskOutcome
{
    public static (string State, string Detail)? From(ControlEntry entry)
    {
        if (entry.State is "FailedBeforeStart" or "Revoked") return ("Failed", "未启动：" + entry.Detail);
        if (entry.State != "Settled") return null;
        var exit=entry.ExitCode;
        if(exit is null)
        {
            var match=System.Text.RegularExpressions.Regex.Match(entry.Detail??"",@"退出码\s+(-?\d+)",System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
            if(match.Success&&int.TryParse(match.Groups[1].Value,out var legacy))exit=legacy;
        }
        if (exit is { } code && code != 0) return ("Failed", $"异常退出（{code}）：{entry.Detail}");
        return ("Review", (exit == 0 ? "正常退出，等待人工验收。" : "进程已结束，退出码未确认，请核对后验收。") + entry.Detail);
    }
}
