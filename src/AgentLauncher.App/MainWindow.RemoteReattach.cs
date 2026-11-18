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
        var syncText = RemoteReattach.RestoresSync(syncMode)
            ? $"原来启用了{syncMode}，重新挂接时会重新建立双向同步。"
            : "这次启动没有启用双向同步（不同步）。重新挂接不会把它说成已同步。";
        string? session = null;
        try
        {
            if (remote is not null && File.Exists(Store.SyncSessionPath(entry.Plan.Id)))
                session = Store.Read<SyncSessionRecord>(Store.SyncSessionPath(entry.Plan.Id))?.Name;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { session = null; }
        var sessionText = string.IsNullOrWhiteSpace(session) ? "无已保存的同步会话名。" : "已保存的同步会话：" + session;
        return new FormWindow(this, "核对远程工作并确认重新挂接", Array.Empty<(string Key, string Label, string Value, bool Multiline)>(),
            $"远程主机：{remote?.Host}\n远程目录：{remote?.RemoteWorkingDirectory}\n本地 SSH：{entry.Detail}\n{syncText}\n{sessionText}\n\n只有确认后才会重新挂接。监视器会先核对远程进程是否仍在：仍在则恢复到同一主机、同一目录的本地 SSH 窗格，占用继续保留，条目不会被标成已结束。已不在则拒绝，账本不变，请改用「核对并确认释放」。不会自动重连，也不会自动释放。",
            applyLabel: "提交挂接");
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
        var prompt = $"二次确认：重新挂接远程主机 {remote?.Host} 的 {remote?.RemoteWorkingDirectory}。\n\n确认后才检查远程进程并恢复本地 SSH。若原来启用了同步，会重新建立；没有启用同步则不会假装已同步。此操作不释放占用，也不把条目标为已结束。远程进程已不在时会拒绝。不会自动重连。是否确认重新挂接？";
#if INTEGRATION_TESTS
        var confirmed = TestConfirm != null ? TestConfirm(prompt, "确认重新挂接", MessageBoxButton.YesNo) : MessageBox.Show(this, prompt, "确认重新挂接", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
#else
        var confirmed = MessageBox.Show(this, prompt, "确认重新挂接", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
#endif
        if (confirmed != MessageBoxResult.Yes) { ProgressText.Text = "已取消二次确认：不重新挂接，条目保持原状，占用保留。"; return; }
        var result = await Ipc.SendAsync(new("reattach-remote", Guid.NewGuid(), Attempt: entry.Plan.Id, Reattach: new OperatorReattach(true)));
        if (!result.Ok) { ErrorText.Text = result.Message; await RefreshAsync(); return; }
        ProgressText.Text = result.Message;
        try
        {
            var terminal = await ResolveLaunchTerminalAsync();
            await SubmitPanesAsync(terminal,[entry.Plan],"vertical",reattach:true);
            ProgressText.Text = result.Message + " 已提交本地 SSH 窗格。";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            ErrorText.Text = "重新挂接已授权，但未能打开终端窗格：" + ex.Message + " 条目保持「正在重新挂接」，占用未释放。请确认 Windows Terminal 后再次挂接。";
        }
        await RefreshAsync();
    }
}
