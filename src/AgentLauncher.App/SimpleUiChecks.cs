#if INTEGRATION_TESTS
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task RunSimpleUiChecksAsync(Action<string, bool> check, string? screenshotFolder = null)
    {
        await saveTask;await commandCheckTask;
        var previous = config;var previousStatus = status;var previousReadOnly = readOnly;
        var previousTimer = timer.IsEnabled;timer.Stop();var previousExpanded = SessionExpander.IsExpanded;
        var tasksBefore = JsonSerializer.Serialize(taskBook, Store.Json);
        try
        {
            config = new() { Draft = new() { Project = Store.Root, Slots = [new() { AgentId = "omp", Enabled = true }, new() { AgentId = "codex", Enabled = true }] } };
            var sample = new MemorySnapshot(16 * ResourcePolicy.GiB, 12 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB, 24 * ResourcePolicy.GiB, 5, DateTimeOffset.UtcNow);
            status = new(true, "simple-ui", [], sample);readOnly = false;Render();
            await commandCheckTask;
            check("ACC-U04 advanced configuration starts collapsed", (LaunchOptionsExpander.Visibility == Visibility.Collapsed || !LaunchOptionsExpander.IsVisible) && (ConfigurationToolsExpander.Visibility == Visibility.Collapsed || !ConfigurationToolsExpander.IsVisible) && (SessionExpander.Visibility == Visibility.Collapsed || !SessionExpander.IsVisible));
            var more = ReviewFind<Button>(this).First(button => Equals(button.Content, "更多") && button.IsVisible);
            var moreHeaders = (more.ContextMenu as System.Windows.Controls.ContextMenu)!.Items.OfType<System.Windows.Controls.MenuItem>().Select(item => item.Header as string).ToArray();
            check("ACC-U04 low Omp and Qoder budgets have a direct repair action", moreHeaders.Contains("长任务资源") && LongTaskButton.IsEnabled);
            var oldResource = config.Draft.Resource;var oldRevision = config.Draft.Revision;
            readOnly = true;UpdateSimpleStatus();
            check("ACC-U04 read-only cannot apply long-task settings", !LongTaskButton.IsEnabled && !ApplyLongTaskResources(sample) && config.Draft.Revision == oldRevision);
            readOnly = false;
            check("ACC-U04 one-click profile changes only future draft budgets", ApplyLongTaskResources(sample) && config.Draft.Resource.JobMemoryLimit == 4 * ResourcePolicy.GiB && oldResource.JobMemoryLimit == 2 * ResourcePolicy.GiB && JsonSerializer.Serialize(taskBook, Store.Json) == tasksBefore);
            await saveTask;await commandCheckTask;
            check("ACC-U04 applied profile persists and hides low-limit advice", SaveHealthy && Store.Read<Configuration>(configPath)!.Draft.Resource.JobMemoryLimit == 4 * ResourcePolicy.GiB && LongTaskNotice.Visibility == Visibility.Collapsed);
            var firstSlot = config.Draft.Slots[0];var secondBefore = JsonSerializer.Serialize(config.Draft.Slots[1], Store.Json);
            var projectBefore = config.Draft.Project;
            firstSlot.RequiresDirectory = true;
            check("F04 direct folder selection preserves other agents and the default project", SetSlotDirectory(firstSlot.Id, Store.Root) && !config.Draft.Slots[0].RequiresDirectory && config.Draft.Project == projectBefore && JsonSerializer.Serialize(config.Draft.Slots[1], Store.Json) == secondBefore);
            await saveTask;await commandCheckTask;
            var workspacesOpened=false;var workspacesVisible=false;
            var workspacesTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};
            workspacesTimer.Tick+=(_,_)=>
            {
                var window=OwnedWindows.OfType<Window>().FirstOrDefault(item=>item.Title=="独立工作目录与人工整合");
                if(window is null)return;
                workspacesTimer.Stop();workspacesOpened=true;workspacesVisible=window.IsVisible;window.Close();
            };
            workspacesTimer.Start();
            more.ContextMenu!.Items.OfType<MenuItem>().Single(item=>item.Header as string=="工作目录").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            for(var i=0;i<80&&workspacesTimer.IsEnabled;i++)await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            workspacesTimer.Stop();
            check("F04 direct folder is saved and visible without probing CLI presets", Store.Read<Configuration>(configPath)!.Draft.Slots[0].Directory == Store.Root && workspacesOpened && workspacesVisible);
            readOnly = true;
            check("F04 direct folder action respects read-only configuration", !SetSlotDirectory(firstSlot.Id, "") && config.Draft.Slots[0].Directory == Store.Root);
            readOnly = false;
            check("F04 explicit inherit action clears the per-agent folder", SetSlotDirectory(firstSlot.Id, "") && config.Draft.Slots[0].Directory == "");
            await saveTask;await commandCheckTask;
            Window? OpenNamed(string title, Action open)
            {
                Window? seen=null;
                Dispatcher.BeginInvoke(new Action(()=>
                {
                    seen=OwnedWindows.OfType<Window>().FirstOrDefault(window=>window.Title==title);
                    seen?.Close();
                }),DispatcherPriority.ApplicationIdle);
                open();
                return seen;
            }
            var templatesWindow=OpenNamed("管理组合",()=>more.ContextMenu!.Items.OfType<MenuItem>().Single(item=>item.Header as string=="管理组合").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            var remotesWindow=OpenNamed("管理远程主机（VPS）",()=>more.ContextMenu!.Items.OfType<MenuItem>().Single(item=>item.Header as string=="远程主机（VPS）").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            var settingsWindow=OpenNamed("设置",()=>ReviewFind<Button>(this).First(button=>Equals(button.Content,"设置")&&button.IsVisible).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            check("ACC-U04 advanced controls remain reachable", moreHeaders.Contains("工作目录") && moreHeaders.Contains("资源设置") && moreHeaders.Contains("远程主机（VPS）") && moreHeaders.Contains("恢复与诊断") && moreHeaders.Contains("运行历史与导出") && moreHeaders.Contains("恢复备份") && moreHeaders.Contains("目录方式") && moreHeaders.Contains("管理组合") && workspacesOpened && templatesWindow is { } && remotesWindow is { } && settingsWindow is { });
            var plan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "恢复检查", Store.Root, new(Store.Root, "ui-test"), false, new("qoder.exe", [], "exe"), 4 * ResourcePolicy.GiB, AgentId: "qoder");
            var entry = new ControlEntry { Plan = plan, State = "Started", Detail = "已启动进程；" + ToolRecovery.BlockedMarker + "整机余量不足" };
            status = new(true, "guard-ui", [entry], sample);RenderSessionsNow();
            check("ACC-U05 a denied live tool offers a copy-only recovery action", ReviewFind<System.Windows.Controls.Button>(ActiveSessions).Any(button => Equals(button.Content, "复制继续提示")));
            if (screenshotFolder is not null)
            {
                LaunchOptionsExpander.IsExpanded = false;ConfigurationToolsExpander.IsExpanded = false;
                SessionExpander.IsExpanded = false;UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);UpdateLayout();
                var capture = StartPagePanel.IsVisible ? (FrameworkElement)this : Sessions;
                if (capture.ActualWidth < 1 || capture.ActualHeight < 1) capture = this;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(capture);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, (int)(capture.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)(capture.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(capture);var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = System.IO.File.Create(System.IO.Path.Combine(screenshotFolder, "memory-recovery.png"));encoder.Save(output);
            }
            entry.Detail = "已启动进程；最近一次工具已通过内存检查";RenderSessionsNow();
            check("ACC-U05 recovered tools remove the stale retry action", !ReviewFind<System.Windows.Controls.Button>(ActiveSessions).Any(button => Equals(button.Content, "复制继续提示")));
            entry.Detail = ToolRecovery.BlockedMarker + "余量不足";entry.State = "Settled";RenderSessionsNow();
            check("ACC-U05 exited sessions never suggest retrying in a live terminal", !ReviewFind<System.Windows.Controls.Button>(Sessions).Any(button => Equals(button.Content, "复制继续提示")) && !ReviewFind<System.Windows.Controls.Button>(ActiveSessions).Any(button => Equals(button.Content, "复制继续提示")));
            config = new() { Draft = new() { Project = Store.Root, Slots = [new() { AgentId = "codex", Enabled = true, Profiles = new() { ["codex"] = new() { Executable = System.IO.Path.Combine(AppContext.BaseDirectory, "AgentLauncher.App.exe") } } }] } };
            status = new(true, "low-memory-ui", [], sample with { Available = ResourcePolicy.GiB, At = DateTimeOffset.UtcNow });
            previewOnly = false;
            Render();QueueSave();await saveTask;await commandCheckTask;UpdateLaunch();
            check("ACC-U06 practical low memory keeps launch available with an advisory hint", LaunchButton.IsEnabled && LaunchButton.IsVisible && QueueQuickButton.Visibility == Visibility.Collapsed && LaunchHint.Text.Contains("可用内存") && LaunchHint.Text.Contains("实用模式不拦截") && !LaunchHint.Text.Contains("等待资源后启动"));
            config.Draft.Resource = new() { Mode = "conservative" };UpdateLaunch();
            check("ACC-U06 conservative low memory still blocks launch and offers the waiting queue", !LaunchButton.IsEnabled && LaunchButton.IsVisible && QueueQuickButton.Visibility == Visibility.Collapsed && moreHeaders.Contains("按资源依次启动") && LaunchHint.Text.Contains("可用内存") && LaunchHint.Text.Contains("等待资源"));
            config.Draft.Resource = new ResourceSettings();
            status = new(true, "recovered-memory-ui", [], sample with { At = DateTimeOffset.UtcNow });UpdateLaunch();
            check("ACC-U06 recovered memory restores immediate launch", LaunchButton.IsEnabled && LaunchButton.IsVisible && QueueQuickButton.Visibility == Visibility.Collapsed);
            if (screenshotFolder is not null)
            {
                SessionExpander.IsExpanded = false;TaskExpander.IsExpanded = false;ContentScroll.ScrollToTop();UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(this);var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = System.IO.File.Create(System.IO.Path.Combine(screenshotFolder, "simple-start.png"));encoder.Save(output);
            }
            await RunDirectStartCheckAsync(check,screenshotFolder);
        }
        finally
        {
            await saveTask;await commandCheckTask;
            LaunchOptionsExpander.IsExpanded = false;ConfigurationToolsExpander.IsExpanded = false;
            SessionExpander.IsExpanded = previousExpanded;
            config = previous;status = previousStatus;readOnly = previousReadOnly;Render();QueueSave();await saveTask;await commandCheckTask;
            if (previousTimer) timer.Start();
        }
    }

    private async Task RunDirectStartCheckAsync(Action<string, bool> check,string? screenshotFolder)
    {
        var previousSample = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE");
        var queueBefore = JsonSerializer.Serialize(taskBook, Store.Json);
        var output = System.IO.Path.Combine(Store.Root, "direct-start.json");
        var directory = System.IO.Path.Combine(Store.Root, "direct-project-中文");System.IO.Directory.CreateDirectory(directory);
        ReviewKillMonitor();
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", "1");
        try
        {
            config = new() { Draft = new() { Project = Store.Root, Slots = [new() { AgentId = "codex", Enabled = true, Directory = directory, Profiles = new() { ["codex"] = new() { Executable = System.IO.Path.Combine(AppContext.BaseDirectory, "AgentLauncher.Tests.exe"), Arguments = ["--echo", output, "中文 with spaces"] } } }] } };
            Render();QueueSave();await saveTask;await commandCheckTask;await RefreshAsync();UpdateLaunch();
            check("ACC-U07 direct launch is ready with an explicit folder", LaunchButton.IsEnabled);
            var launchStartedAt=DateTimeOffset.UtcNow;
            LaunchButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await launchTask;
            var launchObservedAt=DateTimeOffset.UtcNow;
            ControlEntry? started = null;
            for (var i = 0; i < 100; i++)
            {
                await RefreshAsync();started = status?.Entries?.SingleOrDefault(entry => entry.Plan.SourceSlotId == config.Draft.Slots[0].Id);
                if (started is not null && Store.IsTerminal(started)) break;
                await Task.Delay(100);
            }
            var receipt=started is null?null:ReadStoreSettled<Receipt>(Store.ReceiptPath(started.Plan.Id));
            check("ACC-U07 launch button reaches a real Terminal child and exit receipt", started is not null && Store.IsTerminal(started) && receipt is { Phase: "Finished", ExitCode: 0 } && System.IO.File.Exists(output));
            var terminalObservationDelay=launchObservedAt-receipt!.At;
            if(screenshotFolder is not null)Store.Save(System.IO.Path.Combine(screenshotFolder,"direct-start-timing.json"),new{Attempt=started!.Plan.Id,LaunchStartedAt=launchStartedAt,ReceiptAt=receipt.At,LaunchObservedAt=launchObservedAt,LaunchMs=(launchObservedAt-launchStartedAt).TotalMilliseconds,TerminalObservationDelayMs=Math.Max(0,terminalObservationDelay.TotalMilliseconds)});
            check("ACC-U07 completed short command releases launch observation promptly",terminalObservationDelay<TimeSpan.FromSeconds(3));
            using var result = JsonDocument.Parse(System.IO.File.ReadAllText(output));
            check("ACC-U07 child uses the visible per-agent folder and exact arguments", PhysicalDirectory.Resolve(result.RootElement.GetProperty("Cwd").GetString()!).Key == PhysicalDirectory.Resolve(directory).Key && result.RootElement.GetProperty("Args").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["中文 with spaces"]));
            check("ACC-U07 direct launch leaves old queued tasks untouched", JsonSerializer.Serialize(taskBook, Store.Json) == queueBefore);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", previousSample);
            ReviewKillMonitor();
        }
    }
}
#endif
