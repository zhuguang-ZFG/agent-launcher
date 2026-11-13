using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace SoakRunner;

internal static class EvidenceChecks
{
    public static void RequirePlainPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0)
            throw new UsageException("验收只支持本机普通绝对路径。");
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
        {
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new UsageException("验收路径不能经过重解析点: " + current.FullName);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static void ValidateRuntime(string runtime)
    {
        RequirePlainPath(runtime);
        foreach (var assembly in new[] { typeof(Store).Assembly, typeof(ResourcePolicy).Assembly })
        {
            var path = Path.Combine(runtime, Path.GetFileName(assembly.Location));
            if (!File.Exists(path) || JsonIo.Sha256File(path) != JsonIo.Sha256File(assembly.Location))
                throw new ExecutionFailureException("验收器加载的程序集与 Worker 目录不匹配: " + Path.GetFileName(path));
        }
    }

    public static void ValidateDriverRoot(RunnerOptions options, string workspace)
    {
        var root = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) ||
            !Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).StartsWith("AgentLauncher-tests-", StringComparison.OrdinalIgnoreCase) ||
            !JsonIo.IsUnder(root, options.StateRoot) || !JsonIo.IsUnder(root, workspace))
            throw new UsageException("driver 必须由 observer 在本次独立测试根内启动。");
        foreach (var path in new[] { workspace, options.StateRoot, options.AgentDirectory, options.HomeDirectory }) RequirePlainPath(path);
        if (File.Exists(options.ResultFile)) throw new UsageException("driver 结果已存在，拒绝复用旧状态。");
    }

    public static void Finalize(SoakResult result, bool calibration, bool admissionOnly)
    {
        var completed = result.Rounds.Count(round => round.Passed && round.Kind == "stability" &&
            round.RequiredSeconds == 1800 && round.ObservedOverlapSeconds >= 1800 && round.Drivers.Count == 2);
        var cleanupOk = result.Cleanup.Verified && result.Cleanup.Errors.Count == 0 && !result.Cleanup.MonitorLeftRunning;
        var allPassed = result.Rounds.Count == 3 && completed == 3;
        result.Stability.CompletedRounds = completed;
        result.Stability.RequiredRounds = 3;
        result.Stability.RequiredSeconds = 1800;
        result.Stability.Consecutive = allPassed;
        result.Stability.Eligible = !calibration && !admissionOnly && allPassed && cleanupOk &&
            result.Status == "succeeded" && result.Failure is null;
        result.Stability.Reasons.Clear();
        if (calibration) result.Stability.Reasons.Add("校准模式明确排除长期稳定性结论。");
        if (admissionOnly) result.Stability.Reasons.Add("仅执行准入检查。");
        if (completed != 3) result.Stability.Reasons.Add($"已完成 {completed}/3 个 1800 秒双任务轮次。");
        if (!cleanupOk) result.Stability.Reasons.Add("清理尚未全部核实。");
        foreach (var round in result.Rounds.Where(round => !round.Passed))
            result.Stability.Reasons.Add($"第 {round.Index} 轮未通过：{round.Failure ?? "未知原因"}。");
        result.Stability.Claim = result.Stability.Eligible
            ? "3 个连续 1800 秒的 model-free RPC 工具轮次及清理已完成；不代表交互 CLI、模型质量或生产验收。"
            : "未建立长期稳定性结论。";
    }
}

internal sealed class ActivityWatch
{
    private long count = -1;
    private TimeSpan lastActivity;

    public void Observe(long value, TimeSpan elapsed, TimeSpan maximumSilence)
    {
        if (value < 0 || (count >= 0 && (value < count || elapsed < lastActivity)))
            throw new ExecutionFailureException("driver 活动计数或单调时间倒退。");
        if (count >= 0 && elapsed - lastActivity > maximumSilence)
            throw new ExecutionFailureException("driver 工具活动超过允许间隔；不能据旧进度认定持续运行。");
        if (count < 0 || value > count) lastActivity = elapsed;
        count = value;
    }
}
