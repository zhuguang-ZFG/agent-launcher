#if INTEGRATION_TESTS
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task RunTaskChecksAsync(Action<string, bool> check)
    {
        IEnumerable<T> FindLogical<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is T target) yield return target;
                foreach (var nested in FindLogical<T>(child)) yield return nested;
            }
        }
        var tasksPath = Store.FilePath("tasks.json");
        File.Delete(tasksPath);
        var fixture = Path.Combine(AppContext.BaseDirectory, "AgentLauncher.App.exe");
        check("ACC-B05 task fixture program exists", File.Exists(fixture));
        var book = new TaskBook();
        var waiting = TaskBookOps.Create(book, "任务甲", Store.Root, "custom", new AgentProfile { Executable = fixture }, "", ["通过"], "shared", null, 4);
        TaskBookOps.Enqueue(book, waiting.Id, new CommandSpec(fixture, ["--fixture"], "exe"), new string('A', 64));
        var reviewed = TaskBookOps.Create(book, "任务乙", Store.Root, "custom", new AgentProfile { Executable = fixture }, "", [], "shared", null, 1);
        var reviewedRun = TaskBookOps.StartRun(book, TaskBookOps.Enqueue(book, reviewed.Id, new CommandSpec(fixture, [], "exe"), AgentProbe.Fingerprint(fixture)), Guid.NewGuid(), Guid.NewGuid(), "volume:task-check");
        reviewedRun.SessionRef = "session-check";
        TaskBookOps.FinishRun(book, reviewedRun.AttemptId, "Review", "进程已结束，等待人工验收");
        Store.Save(tasksPath, book);
        await LoadTasksAsync();
        RenderTasks();
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        check("ACC-B05 task cards render with queue position and review state", TaskList.Children.Count == 2 && TaskHeading.Text.Contains("2 张") && TaskHeading.Text.Contains("待执行 1"));
        var acceptance = FindLogical<CheckBox>(TaskList).First(box => AutomationProperties.GetName(box).StartsWith("验收 ", StringComparison.Ordinal));
        acceptance.IsChecked = true;
        acceptance.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        for (var i = 0; i < 50 && !taskStore.Load().Tasks.Single(task => task.Id == waiting.Id).Acceptance[0].Checked; i++) await Task.Delay(100);
        check("ACC-B05 accepting a checkbox persists without dropping the queue", taskStore.Load().Tasks.Single(task => task.Id == waiting.Id).Acceptance[0].Checked && taskStore.Load().Queue.Count == 1);
        check("ACC-B05 resume action appears only for a settled session", FindLogical<Button>(TaskList).Any(button => AutomationProperties.GetName(button) == "续接会话 任务乙")
            && !FindLogical<Button>(TaskList).Any(button => AutomationProperties.GetName(button) == "续接会话 任务甲"));
        QueueToggleClick(this, new RoutedEventArgs());
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        check("ACC-B05 queue starts paused and only runs when asked", queueEnabled && QueueToggleButton.Content as string == "暂停队列");
        var attemptsBefore = taskStore.Load().Attempts.Count;
        var detail = "";
        for (var i = 0; i < 300; i++)
        {
            detail = taskStore.Load().Queue.Single().Detail;
            if (detail.Contains("程序或参数已变化")) break;
            if (!queuePumping) await PumpQueueAsync();
            await Task.Delay(100);
        }
        check("ACC-B06 dispatch holds a changed program instead of launching", detail.Contains("程序或参数已变化") && taskStore.Load().Attempts.Count == attemptsBefore && QueueStatusText.Text.Contains("程序或参数已变化"));
        QueueToggleClick(this, new RoutedEventArgs());
        check("ACC-B05 queue toggles back to paused", !queueEnabled && QueueToggleButton.Content as string == "执行队列");
        TaskConcurrency.SelectedIndex = 1;
        for (var i = 0; i < 50 && taskBook?.MaxRunning != 2; i++) await Task.Delay(100);
        check("ACC-B05 queue concurrency is adjustable within 1-4", taskStore.Load().MaxRunning == 2 && TaskConcurrency.SelectedIndex == 1);
        TaskConcurrency.SelectedIndex = 0;
        for (var i = 0; i < 50 && taskBook?.MaxRunning != 1; i++) await Task.Delay(100);
        check("ACC-B05 queue concurrency restores to one", taskStore.Load().MaxRunning == 1 && taskBook?.MaxRunning == 1);
        RenderTasks();
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        var attemptsBeforeDequeue = taskStore.Load().Attempts.Count;
        var dequeueButton = FindLogical<Button>(TaskList).FirstOrDefault(button => AutomationProperties.GetName(button) == "移出队列 任务甲");
        check("ACC-B07 queued task exposes queue-only removal", dequeueButton != null);
        dequeueButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 50 && taskStore.Load().Queue.Count > 0; i++) await Task.Delay(100);
        check("ACC-B07 removing a queued item keeps attempts and the task record",
            taskStore.Load().Queue.Count == 0 && taskStore.Load().Attempts.Count == attemptsBeforeDequeue && taskStore.Load().Tasks.Any(task => task.Id == waiting.Id));
        File.WriteAllText(tasksPath, "{\"SchemaVersion\":1,\"Revision\":3,\"Tasks\":\"bad\"}");
        File.WriteAllText(tasksPath + ".bak", JsonSerializer.Serialize(book, Store.Json));
        await LoadTasksAsync();
        RenderTasks();
        check("ACC-B06 corrupt task book fails closed and keeps the file", taskBook is null && QueueToggleButton.IsEnabled == false && File.ReadAllText(tasksPath).Contains("\"bad\"") && TaskEmptyText.Text.Contains("无法读取"));
        File.Delete(tasksPath);
        File.Delete(tasksPath + ".bak");
        await LoadTasksAsync();
        ErrorText.Text = "";
    }
}
#endif
