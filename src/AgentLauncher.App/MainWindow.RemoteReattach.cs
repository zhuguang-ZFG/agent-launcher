using System.IO;
using System.Windows;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    // R33：与「核对并确认释放」分开。未确认不发协议。成功后才打开本地 SSH 窗格。
    // 远程进程已不在时由监视器拒绝，这里不自动改走释放。
    internal FormWindow CreateRemoteReattachDialog(ControlEntry entry)
    {
        var remote = entry.Plan.RemoteExecution;
        var syncMode = remote?.WorkspaceSync?.Mode;
        var syncText = syncMode switch
        {
            WorkspaceSyncMode.Mutagen => "会核对并复用原 Mutagen 会话；原会话明确不存在时才重新建立双向同步。",
            WorkspaceSyncMode.SshfsMount => "SSHFS 仅重新挂载远程目录，不会上传所选本地项目或将改动回写该项目。",
            _ => "这次启动没有启用文件同步；打开 Shell 不会建立同步。"
        };
        string? session = null;
        try
        {
            if (remote is not null && File.Exists(Store.SyncSessionPath(entry.Plan.Id)))
                session = Store.Read<SyncSessionRecord>(Store.SyncSessionPath(entry.Plan.Id))?.Name;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { session = null; }
        var sessionText = string.IsNullOrWhiteSpace(session) ? "无已保存的同步会话名。" : "已保存的同步会话：" + session;
        return new FormWindow(this, "核对并打开远程工作 Shell", Array.Empty<(string Key, string Label, string Value, bool Multiline)>(),
            $"远程主机：{remote?.Host}\n远程目录：{remote?.RemoteWorkingDirectory}\n本地 SSH：{entry.Detail}\n{syncText}\n{sessionText}\n\n{LaunchSurface.ReattachIsNewPane}\n监视器先核对原远程进程：仍在时允许打开工作 Shell，占用继续保留；已不在则拒绝，请改用「核对并确认释放」。需要继续 Agent 对话时，须先核对原任务是否已结束，再使用支持的「续接上次远程会话」或 Agent 自己的会话恢复命令。不会自动重连或释放。文件同步不是实时一致。",
            applyLabel: "打开工作 Shell");
    }

    internal async Task ConfirmRemoteReattachAsync(ControlEntry entry)
    {
        var dialog = CreateRemoteReattachDialog(entry);
        if (dialog.ShowDialog() != true) return;
        await SubmitRemoteReattachAsync(entry);
    }

    internal async Task SubmitRemoteReattachAsync(ControlEntry entry)
    {
        var remote = entry.Plan.RemoteExecution;
        var prompt = $"二次确认：打开远程主机 {remote?.Host} 的 {remote?.RemoteWorkingDirectory} 工作 Shell。\n\n{LaunchSurface.ReattachIsNewPane}\n确认后才检查原远程进程。Mutagen 会话会核对复用，SSHFS 仅挂载远程目录；文件状态以同步检查为准。此操作不释放占用，也不把条目标为已结束。远程进程已不在时会拒绝。不会自动重连。是否打开远程工作 Shell？";
#if INTEGRATION_TESTS
        var confirmed = TestConfirm != null ? TestConfirm(prompt, "确认打开远程工作 Shell", MessageBoxButton.YesNo) : MessageBox.Show(this, prompt, "确认打开远程工作 Shell", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
#else
        var confirmed = MessageBox.Show(this, prompt, "确认打开远程工作 Shell", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
#endif
        if (confirmed != MessageBoxResult.Yes) { ProgressText.Text = "已取消二次确认：不重新挂接，条目保持原状，占用保留。"; return; }
        var result = await Ipc.SendAsync(new("reattach-remote", Guid.NewGuid(), Attempt: entry.Plan.Id, Reattach: new OperatorReattach(true)));
        if (!result.Ok) { ErrorText.Text = result.Message; await RefreshAsync(); return; }
        ProgressText.Text = result.Message;
        try
        {
            await SubmitPanesAsync("",[entry.Plan],"vertical",reattach:true);
            ProgressText.Text = result.Message + " 已提交本地 SSH 窗格。";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            ErrorText.Text = "重新挂接已授权，但未能打开终端窗格：" + ex.Message + " 条目保持「正在重新挂接」，占用未释放。请稍后再次挂接。";
        }
        await RefreshAsync();
    }
}
