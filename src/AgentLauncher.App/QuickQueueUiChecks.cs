#if INTEGRATION_TESTS
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task RunQuickQueueChecksAsync(Action<string, bool> check)
    {
        var priorConfig = config;
        var priorBook = taskStore.Load();
        var priorSample = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE");
        var project = Path.Combine(Store.Root, "quick queue snapshot");
        Directory.CreateDirectory(project);
        var marker = Path.Combine(project, "unexpected-agent.txt");
        var node = Commands.FindExe("node.exe") ?? throw new IOException("Node fixture is required.");
        var args = new[] { "-e", $"require('node:fs').writeFileSync({JsonSerializer.Serialize(marker)}, 'started')", "", "中文; & literal" };
        var resource = new ResourceSettings { Mode = "custom", JobMemoryLimit = 8 * ResourcePolicy.GiB, PlanningBytes = 8 * ResourcePolicy.GiB,
            PhysicalFloorOverride = 8 * ResourcePolicy.GiB, Pool = ResourcePolicy.PoolShared, PoolMemoryLimit = 8 * ResourcePolicy.GiB };
        config = new Configuration
        {
            Draft = new Draft { Project = project, DirectoryMode = "shared", Resource = resource, Slots =
            [new Slot { Enabled = true, AgentId = "custom", Note = "原始说明", Profiles = new() { ["custom"] = new() { Executable = node, Arguments = args } } },
             new Slot { Enabled = false, AgentId = "custom" },
             new Slot { Enabled = true, AgentId = "custom", Directory = project, Profiles = new() { ["custom"] = new() { Executable = node, Arguments = args } } }] }
        };
        queueEnabled = false;
        taskTimer.Stop();
        ReviewKillMonitor();
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", "1");
        Store.Save(taskStore.FilePath, new TaskBook());
        Store.Save(configPath, config);
        try
        {
            await LoadTasksAsync();
            Render();
            await CheckFreshCommandsAsync();
            await RefreshAsync();
            var original = JsonSerializer.Serialize(config.Draft, Store.Json);
            var before = File.ReadAllText(taskStore.FilePath);
            var preparations = Directory.Exists(Store.FilePath("preparations")) ? Directory.GetFiles(Store.FilePath("preparations")).Order().ToArray() : [];

            async Task AnswerAsync(bool apply, bool duplicateClick = false)
            {
                QueueQuickButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (duplicateClick) QueueQuickButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                FormWindow? dialog = null;
                for (var i = 0; i < 100 && dialog is null; i++)
                {
                    dialog = OwnedWindows.OfType<FormWindow>().FirstOrDefault(window => window.IsVisible && window.Title == "按资源依次启动");
                    if (dialog is null) await Task.Delay(25);
                }
                if (dialog is null) throw new IOException("Quick queue confirmation did not open: " + ErrorText.Text);
                ReviewFind<Button>(dialog).Single(button => button.Content as string == (apply ? "应用" : "取消")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!apply && dialog.IsVisible) dialog.Close();
                await quickQueueTask;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }

            check("QQ resource pressure offers queueing while immediate launch stays blocked", QueueQuickButton.IsEnabled && !LaunchButton.IsEnabled);
            await AnswerAsync(false);
            check("QQ cancelling import leaves tasks and quick configuration unchanged", File.ReadAllText(taskStore.FilePath) == before && JsonSerializer.Serialize(config.Draft, Store.Json) == original);
            await AnswerAsync(true, duplicateClick: true);
            var queued = taskStore.Load();
            check("QQ double click imports each enabled slot exactly once", queued.Tasks.Count == 2 && queued.Queue.Count == 2 && queued.MaxRunning == 1
                && queued.Queue.Select(entry => queued.Tasks.Single(task => task.Id == entry.TaskId).QuickLaunch!.Position).SequenceEqual([1, 3]));
            check("QQ queued profiles freeze raw arguments and resources without changing quick settings", queued.Tasks.All(task => task.Profile.Arguments.SequenceEqual(args)
                && task.Resource is { Pool: ResourcePolicy.PoolIndependent } budget && budget.PlanningBytes == 8 * ResourcePolicy.GiB && budget.JobMemoryLimit == 8 * ResourcePolicy.GiB)
                && JsonSerializer.Serialize(config.Draft, Store.Json) == original);
            check("QQ denied preflight leaves every task waiting without preparation or Agent creation", queued.Attempts.Count == 0 && !File.Exists(marker)
                && preparations.SequenceEqual(Directory.Exists(Store.FilePath("preparations")) ? Directory.GetFiles(Store.FilePath("preparations")).Order().ToArray() : []));
            var accepted = File.ReadAllText(taskStore.FilePath);
            await AnswerAsync(true);
            check("QQ repeated confirmed import rejects an already waiting snapshot without mutation", File.ReadAllText(taskStore.FilePath) == accepted);
            QueueToggleClick(this, new RoutedEventArgs());
            await queuePumpTask;
            var first = queued.Queue[0].TaskId;
            await UpdateTasksAsync(book => TaskBookOps.Dequeue(book, first), "测试移出失败");
            var removed = taskStore.Load();
            check("QQ removing waiting work keeps other intent and creates no attempt", !queueEnabled && removed.Queue.Count == 1
                && removed.Queue[0].TaskId == queued.Queue[1].TaskId && removed.Tasks.Count == 2 && removed.Attempts.Count == 0 && !File.Exists(marker));
            await UpdateTasksAsync(book => TaskBookOps.Dequeue(book, queued.Queue[1].TaskId), "测试移出失败");
            ReviewKillMonitor();
            queueEnabled = true;
            taskTimer.Start();
            await PumpQueueAsync();
            check("QQ an empty queue stops polling without recreating an idle monitor", !queueEnabled && !taskTimer.IsEnabled && !Ipc.MonitorRunning());
        }
        finally
        {
            queueEnabled = false;
            taskTimer.Stop();
            queueCancellation?.Cancel();
            await queuePumpTask;
            config = priorConfig;
            Store.Save(configPath, config);
            Store.Save(taskStore.FilePath, priorBook);
            Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", priorSample);
            ReviewKillMonitor();
            await LoadTasksAsync();
            Render();
            await CheckFreshCommandsAsync();
            ErrorText.Text = "";
        }
    }
}
#endif
