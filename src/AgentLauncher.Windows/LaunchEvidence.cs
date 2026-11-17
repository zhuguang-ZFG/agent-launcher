using AgentLauncher.Core;

namespace AgentLauncher.Windows;

public sealed record LaunchBaseline(Guid AttemptId,string WorkspaceKey,string? Commit);

public static class LaunchEvidence
{
    public static void CaptureBaseline(LaunchItem plan)
    {
        if(plan.Kind!="agent")return;
        var path=Store.FilePath($"attempts/{plan.Id:N}/baseline.json");
        if(File.Exists(path))throw new IOException("启动基线已存在，请核对原尝试，不能覆盖。");
        Store.Save(path,new LaunchBaseline(plan.Id,plan.Workspace.Key,GitChanges.ReadHead(plan.Cwd)));
    }

    public static string? ReadBaseline(LaunchItem plan)
    {
        if(PhysicalDirectory.Workspace(plan.Cwd).Key!=plan.Workspace.Key)throw new IOException("任务工作目录身份已经变化，不能将当前目录当成原任务结果。");
        var baseline=Store.Read<LaunchBaseline>(Store.FilePath($"attempts/{plan.Id:N}/baseline.json"));
        if(baseline is null)return null;
        if(baseline.AttemptId!=plan.Id||baseline.WorkspaceKey!=plan.Workspace.Key)throw new IOException("启动基线与该任务身份不一致。");
        return baseline.Commit;
    }
}
