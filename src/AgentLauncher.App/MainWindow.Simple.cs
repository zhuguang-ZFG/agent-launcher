using System.IO;
using System.Windows;
using System.Windows.Controls;
using AgentLauncher.Core;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private bool checkingLaunch;

    private void ChooseSlotDirectory(Guid id)
    {
        if (busy || readOnly || loading || closeRequested) return;
        var slot = config.Draft.Slots.Single(s => s.Id == id);
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择此 Agent 的工作目录" };
        var current = string.IsNullOrWhiteSpace(slot.Directory) ? config.Draft.Project : slot.Directory;
        if (Directory.Exists(current)) picker.InitialDirectory = current;
        if (picker.ShowDialog(this) == true) SetSlotDirectory(id, picker.FolderName);
    }

    private bool SetSlotDirectory(Guid id, string directory)
    {
        if (busy || readOnly || loading || closeRequested) return false;
        return Edit(draft => { var slot = draft.Slots.Single(s => s.Id == id); slot.Directory = directory; slot.RequiresDirectory = false; });
    }

    private async void RefreshLaunchClick(object sender, RoutedEventArgs e) => await GuardAsync(RefreshLaunchChecksAsync);

    private async Task RefreshLaunchChecksAsync()
    {
        if (checkingLaunch || busy || loading || closeRequested) return;
        checkingLaunch = true;RefreshLaunchButton.IsEnabled = false;
        try
        {
            await ApplyProjectInputAsync();
            commandLabelCache.Clear();
            await CheckCommandsAsync();
            await LoadResourceViewAsync();
        }
        finally { checkingLaunch = false;UpdateSimpleStatus(); }
    }

    private void MoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button) { menu.PlacementTarget = button; menu.IsOpen = true; }
    }

    private void UpdateSimpleStatus()
    {
        RefreshLaunchButton.IsEnabled = !checkingLaunch && !busy && !loading && !closeRequested;
        LongTaskNotice.Visibility = LongTaskResources.Suggested(config.Draft) ? Visibility.Visible : Visibility.Collapsed;
        LongTaskButton.IsEnabled = !busy && !readOnly && !loading && !closeRequested && !launchFinalizing && launchCancellation is null;
        LongTaskText.Text = $"Omp / Qoder 当前上限 {config.Draft.Resource.JobMemoryLimit / (double)ResourcePolicy.GiB:0.#} GiB，触顶可能中断。扩容可增加余量，仍需限制并发。";
        var toolProfile = config.Draft.Resource.JobMemoryLimit >= LongTaskResources.MemoryLimit;
        LaunchOptionsExpander.Header = toolProfile ? $"启动选项 · 上限 {config.Draft.Resource.JobMemoryLimit / (double)ResourcePolicy.GiB:0.#} GiB" : "启动选项 · 目录与分屏";
    }

    private async void LongTaskClick(object sender, RoutedEventArgs e) => await GuardAsync(ApplyLongTaskResourcesAsync);

    private async Task ApplyLongTaskResourcesAsync()
    {
        if (busy || readOnly || loading || closeRequested || launchFinalizing || launchCancellation is not null) return;
        var revision = config.Draft.Revision;
        await RefreshAsync();
        if (revision != config.Draft.Revision) throw new IOException("配置已改变，请重新应用长任务配置。");
        if (status?.Ok != true || status.Memory is not { } sample || !ResourcePolicy.IsSampleValid(sample))
            throw new IOException("暂时无法核对电脑资源，请稍后重试。");
        if (!ApplyLongTaskResources(sample)) return;
        await saveTask;
        ErrorText.Text = "";
        SaveStatus.Text = SaveHealthy ? $"已保存 · 新任务上限 {config.Draft.Resource.JobMemoryLimit / ResourcePolicy.GiB} GiB；已有任务保持原配置" : SaveStatus.Text;
        await RefreshAsync();
    }

    private bool ApplyLongTaskResources(MemorySnapshot sample)
    {
        if (busy || readOnly || loading || closeRequested || launchFinalizing || launchCancellation is not null) return false;
        if (!ResourcePolicy.IsSampleValid(sample)) throw new IOException("资源读数已过期，请稍后重试。");
        var next = LongTaskResources.Create(config.Draft.Resource);
        ResourcePolicy.ValidateSettings(next, sample.Total, sample.CommitLimit);
        return Edit(draft => draft.Resource = next);
    }
}
