using System.Windows;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    // R28 操作员确认释放入口：先记录主机、远程目录、判断（仍在 / 已不在 / 无法判断）与释放原因，
    // 只有「已不在 + 原因 + 二次确认」才发送 release-remote。仍在 / 无法判断 / 未确认一律不释放，
    // 条目保持 Disconnected、预算保留；不提供会盲目再拉起可能仍存活远程进程的一键重试。
    internal FormWindow CreateRemoteReleaseDialog(ControlEntry entry)
    {
        var remote = entry.Plan.RemoteExecution;
        var fields = new (string Key, string Label, string Value, bool Multiline)[]
        {
            ("judgment", "远程进程判断", "Unknown", false),
            ("reason", "释放原因（必填）", "", true)
        };
        var choices = new Dictionary<string, (string Value, string Label)[]>
        {
            ["judgment"] = [("Running", "仍在运行（不释放）"), ("Gone", "已不在（已退出）"), ("Unknown", "无法判断（不释放）")]
        };
        return new FormWindow(this, "核对远程状态并确认释放", fields,
            $"远程主机：{remote?.Host}\n远程目录：{remote?.RemoteWorkingDirectory}\n\n结束本地 SSH 不能证明远程进程已退出。请先登录远程主机核对 agent 进程是否仍在运行，再选择判断并填写释放原因。{LaunchSurface.ReleaseEffect}只有判断「已不在」并经二次确认后才会释放该条目；判断「仍在运行」或「无法判断」时保留占用，不释放。",
            choices: choices, applyLabel: "提交判断");
    }

    internal async Task ConfirmRemoteReleaseAsync(ControlEntry entry)
    {
        var dialog = CreateRemoteReleaseDialog(entry);
        if (dialog.ShowDialog() != true) return;
        var judgment = dialog.Values.GetValueOrDefault("judgment") ?? "Unknown";
        var reason = (dialog.Values.GetValueOrDefault("reason") ?? "").Trim();
        await SubmitRemoteReleaseAsync(entry, judgment, reason);
    }

    internal async Task SubmitRemoteReleaseAsync(ControlEntry entry, string judgment, string reason)
    {
        var remote = entry.Plan.RemoteExecution;
        if (judgment != "Gone") { ProgressText.Text = "判断为仍在运行或无法判断：不释放，条目保持「连接断开」，预算保留。请继续在远程主机核对。"; return; }
        if (reason.Length == 0) { ProgressText.Text = "缺少释放原因：不释放。请填写核对结论与释放原因后重新提交。"; return; }
        if (reason.Length > 2048) { ProgressText.Text = "释放原因超过 2048 字上限：不释放。请精简后重新提交。"; return; }
        var prompt = $"二次确认：已核对远程主机 {remote?.Host} 的 {remote?.RemoteWorkingDirectory}，判断远程进程已不在。\n释放原因：{reason}\n\n确认后该条目将转为终态并释放占用，核对记录写入 operator-release.json；此操作不会结束远程主机上的任何进程，也不会重新挂接。是否确认释放？";
#if INTEGRATION_TESTS
        var confirmed = TestConfirm != null ? TestConfirm(prompt, "确认释放远程条目", MessageBoxButton.YesNo) : MessageBox.Show(this, prompt, "确认释放远程条目", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
#else
        var confirmed = MessageBox.Show(this, prompt, "确认释放远程条目", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
#endif
        if (confirmed != MessageBoxResult.Yes) { ProgressText.Text = "已取消二次确认：不释放，条目保持「连接断开」，预算保留。"; return; }
        var result = await Ipc.SendAsync(new("release-remote", Guid.NewGuid(), Attempt: entry.Plan.Id, Release: new OperatorRelease("Gone", reason, true)));
        if (result.Ok) ProgressText.Text = result.Message; else ErrorText.Text = result.Message;
        await RefreshAsync();
    }
}