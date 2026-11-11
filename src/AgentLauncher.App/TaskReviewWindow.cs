using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

/// <summary>
/// S4 review center for one task attempt. Every service call is explicit: capture is reproducible from the
/// displayed base/HEAD, checks run only after the user picks a saved named command and clicks run, and push/PR
/// need a second confirmation. Control values are always read on the UI thread before any background call.
/// </summary>
public sealed class TaskReviewWindow : Window
{

    private readonly TaskCard task;
    private readonly TaskRun run;
    private readonly IReadOnlyList<ControlEntry>? monitorEntries;
    private readonly string? capturedBaseSha;
    private readonly ReviewService service = new();
    private readonly ReviewStore store = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, CheckBox> fileChecks = new(StringComparer.Ordinal);
    private string? workspacePath;
    private FileSystemWatcher? watcher;
    private string bookError = "";
    private int captureEpoch;
    private int validatedEpoch;
    private int changeNotificationPending;
    private bool freshnessDirty;

    private readonly TextBlock statusText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };
    private readonly TextBlock runText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock workspaceText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock baseText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, FontFamily = new FontFamily("Consolas, Courier New") };
    private readonly TextBlock headText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, FontFamily = new FontFamily("Consolas, Courier New") };
    private readonly TextBlock filesSummary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel filePanel = new();
    private readonly TextBlock diffTitle = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
    private readonly TextBox diffBox = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas, Courier New"), FontSize = 12, Height = 220 };
    private readonly ComboBox profilesCombo = new() { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 8, 4) };
    private readonly TextBlock commandPreview = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, FontFamily = new FontFamily("Consolas, Courier New"), Margin = new Thickness(0, 4, 0, 8), Foreground = Brushes.DimGray };
    private readonly StackPanel resultPanel = new();
    private readonly TextBox resultOutput = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas, Courier New"), FontSize = 12, Height = 140, Visibility = Visibility.Collapsed };
    private readonly TextBox commitMessage = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock commitPreview = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 8), Foreground = Brushes.DimGray };
    private readonly TextBox prTitle = new() { MinHeight = 32 };
    private readonly TextBox prBody = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel ciPanel = new();
    private readonly StackPanel portPanel = new();
    private readonly Button refreshButton = new() { Content = "刷新改动", MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button stopButton = new() { Content = "停止检查", MinWidth = 96, IsEnabled = false };
    private readonly Button addProfileButton = new() { Content = "新增检查…", MinWidth = 96, Margin = new Thickness(0, 4, 8, 4) };
    private readonly Button runButton = new() { Content = "运行检查", MinWidth = 96, Margin = new Thickness(0, 4, 8, 4), IsEnabled = false };
    private readonly Button commitButton = new() { Content = "提交选中文件", MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };
    private readonly Button pushButton = new() { Content = "推送…", MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };
    private readonly Button prButton = new() { Content = "创建 PR…", MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };
    private readonly Button ciButton = new() { Content = "查询此提交的 CI", MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };
    private readonly Button portsButton = new() { Content = "刷新预览端口", MinWidth = 130, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };

    private ReviewSnapshot? snapshot;
    private ReviewBook? book;
    private CancellationTokenSource? verifyCancellation;
    private VerificationResult? transientResult;
    private CiReport? ciReport;
    private bool busy;
    private volatile bool closed;

    public TaskReviewWindow(Window owner, TaskCard task, TaskRun run) : this(owner, task, run, null) { }

    internal TaskReviewWindow(Window owner, TaskCard task, TaskRun run, IReadOnlyList<ControlEntry>? monitorEntries)
    {
        if (task is null) throw new ArgumentNullException(nameof(task));
        if (run is null) throw new ArgumentNullException(nameof(run));
        if (run.TaskId != task.Id) throw new InvalidDataException("该尝试不属于此任务，拒绝审阅。");
        if (run.AttemptId == Guid.Empty) throw new InvalidDataException("该尝试缺少身份，拒绝审阅。");
        this.task = task;
        this.run = run;
        this.monitorEntries = monitorEntries;
        // The card may have been edited after the attempt; its base commit only applies to the revision the run was dispatched with.
        capturedBaseSha = task.Revision == run.TaskRevision && !string.IsNullOrWhiteSpace(task.BaseCommit) ? task.BaseCommit : null;
        Owner = owner;
        Title = "审查改动 · " + task.Name;
        Width = Math.Min(940, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(900, SystemParameters.WorkArea.Height - 32);
        MinWidth = Math.Min(520, SystemParameters.WorkArea.Width - 32);
        MinHeight = Math.Min(480, SystemParameters.WorkArea.Height - 32);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)Application.Current.Resources[typeof(Window)];
        AutomationProperties.SetAutomationId(this, "TaskReviewWindow");
        AutomationProperties.SetName(this, "审查改动 " + task.Name);
        statusText.Foreground = (Brush)FindResource("Muted");
        prTitle.Text = task.Name;
        prBody.Text = (task.Description.Length > 0 ? task.Description + "\n\n" : "") + "任务尝试：" + run.AttemptId;
        Content = BuildContent();
        UiScale.Apply(this);
        Loaded += async (_, _) => await RunGuardedAsync(RefreshCaptureAsync);
        Closed += (_, _) =>
        {
            closed = true;
            watcher?.Dispose();
            watcher = null;
            verifyCancellation?.Cancel();
            lifetime.Cancel();
        };
        UpdateActions();
        SetStatus("正在读取改动；基准 SHA 与 HEAD 会显示在下方。");
    }

    private UIElement BuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(16) };

        var toolbarActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        refreshButton.Click += async (_, _) => await RunGuardedAsync(RefreshCaptureAsync);
        stopButton.Click += (_, _) =>
        {
            verifyCancellation?.Cancel();
            SetStatus("正在取消检查；只停止本次检查自己启动的进程树。");
        };
        AutomationProperties.SetName(refreshButton, "刷新改动");
        AutomationProperties.SetAutomationId(refreshButton, "ReviewRefresh");
        AutomationProperties.SetName(stopButton, "停止检查");
        AutomationProperties.SetAutomationId(stopButton, "ReviewStop");
        AutomationProperties.SetAutomationId(statusText, "ReviewStatus");
        AutomationProperties.SetName(statusText, "审阅状态");
        toolbarActions.Children.Add(refreshButton);
        toolbarActions.Children.Add(stopButton);
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(toolbarActions, Dock.Right);
        toolbar.Children.Add(toolbarActions);
        toolbar.Children.Add(statusText);
        root.Children.Add(toolbar);

        var panel = new StackPanel();

        runText.Text = $"任务：{task.Name} · 修订 {task.Revision} · TaskRunId：{run.AttemptId}";
        workspaceText.Text = "工作区：正在按该尝试计划锁定（不会打开其它目录）";
        baseText.Text = "基准 SHA：读取中";
        headText.Text = "HEAD：读取中";
        AutomationProperties.SetAutomationId(runText, "ReviewRunIdentity");
        AutomationProperties.SetName(runText, "审阅尝试");
        AutomationProperties.SetAutomationId(workspaceText, "ReviewWorkspace");
        AutomationProperties.SetName(workspaceText, "审阅工作区");
        AutomationProperties.SetAutomationId(baseText, "ReviewBaseSha");
        AutomationProperties.SetName(baseText, "基准提交");
        AutomationProperties.SetAutomationId(headText, "ReviewHeadSha");
        AutomationProperties.SetName(headText, "当前 HEAD");
        AutomationProperties.SetAutomationId(filesSummary, "ReviewFilesSummary");
        AutomationProperties.SetName(filesSummary, "改动文件摘要");
        AutomationProperties.SetAutomationId(filePanel, "ReviewFileList");
        var identity = new StackPanel();
        identity.Children.Add(new TextBlock { Text = "审阅范围", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        identity.Children.Add(runText);
        identity.Children.Add(workspaceText);
        identity.Children.Add(baseText);
        identity.Children.Add(headText);
        if (task.Revision != run.TaskRevision)
            identity.Children.Add(new TextBlock { Text = $"任务卡片已在尝试后编辑（当前修订 {task.Revision}，尝试修订 {run.TaskRevision}）；本次审阅不使用编辑后的基准提交。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 4, 0, 0) });
        identity.Children.Add(new TextBlock { Text = "基准与 HEAD 均为完整 SHA；未跟踪文件默认不勾选，也不会被自动提交。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(Section("改动文件", identity, filesSummary, filePanel));

        diffTitle.Text = "差异 · 选择文件后查看";
        AutomationProperties.SetAutomationId(diffBox, "ReviewDiff");
        AutomationProperties.SetName(diffBox, "差异内容");
        panel.Children.Add(Section("有界差异", diffTitle, diffBox));

        profilesCombo.SelectionChanged += (_, _) => { UpdateCommandPreview(); UpdateActions(); };
        addProfileButton.Click += async (_, _) => await RunGuardedAsync(AddProfileAsync);
        runButton.Click += async (_, _) => await RunGuardedAsync(RunCheckAsync);
        AutomationProperties.SetName(addProfileButton, "新增检查配置");
        AutomationProperties.SetAutomationId(addProfileButton, "ReviewAddProfile");
        AutomationProperties.SetName(runButton, "运行检查");
        AutomationProperties.SetAutomationId(runButton, "ReviewRun");
        AutomationProperties.SetName(profilesCombo, "命名检查");
        AutomationProperties.SetAutomationId(profilesCombo, "ReviewProfile");
        AutomationProperties.SetName(resultOutput, "检查输出");
        AutomationProperties.SetAutomationId(resultOutput, "ReviewCheckOutput");
        AutomationProperties.SetName(resultPanel, "检查结果");
        AutomationProperties.SetAutomationId(resultPanel, "ReviewResults");
        var profileRow = new WrapPanel();
        profileRow.Children.Add(profilesCombo);
        profileRow.Children.Add(addProfileButton);
        profileRow.Children.Add(runButton);
        commandPreview.Text = "未选择检查；「运行检查」只在选择已保存的命名命令后可用。";
        resultPanel.Children.Add(new TextBlock { Text = "本任务还没有检查结果。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Muted") });
        panel.Children.Add(Section("项目检查",
            new TextBlock { Text = "检查来自你保存的命名命令（EXE + 逐项 argv + 超时），不会自动执行仓库脚本。运行受资源与 Job 保护，超时或取消只结束本次检查。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 4) },
            profileRow, commandPreview, resultPanel, resultOutput));

        commitMessage.TextChanged += (_, _) => UpdateCommitState();
        commitButton.Click += async (_, _) => await RunGuardedAsync(CommitAsync);
        AutomationProperties.SetName(commitMessage, "提交消息");
        AutomationProperties.SetAutomationId(commitMessage, "ReviewCommitMessage");
        AutomationProperties.SetName(commitButton, "提交选中文件");
        AutomationProperties.SetAutomationId(commitButton, "ReviewCommit");
        AutomationProperties.SetAutomationId(commitPreview, "ReviewCommitPreview");
        panel.Children.Add(Section("提交预览",
            new TextBlock { Text = "只提交下方勾选的文件；提交前服务会重核 HEAD、索引与已审阅内容，期间发生变化会拒绝而不是覆盖。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Muted") },
            commitMessage, commitPreview, commitButton));

        pushButton.Click += async (_, _) => await RunGuardedAsync(PushAsync);
        prButton.Click += async (_, _) => await RunGuardedAsync(CreatePullRequestAsync);
        AutomationProperties.SetName(pushButton, "推送当前提交");
        AutomationProperties.SetAutomationId(pushButton, "ReviewPush");
        AutomationProperties.SetName(prTitle, "PR 标题");
        AutomationProperties.SetAutomationId(prTitle, "ReviewPrTitle");
        AutomationProperties.SetName(prBody, "PR 说明");
        AutomationProperties.SetAutomationId(prBody, "ReviewPrBody");
        AutomationProperties.SetName(prButton, "创建拉取请求");
        AutomationProperties.SetAutomationId(prButton, "ReviewCreatePr");
        var pushRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        pushRow.Children.Add(pushButton);
        var prStack = new StackPanel();
        prStack.Children.Add(new TextBlock { Text = "PR 标题", FontSize = 12, Foreground = (Brush)FindResource("Muted") });
        prStack.Children.Add(prTitle);
        prStack.Children.Add(new TextBlock { Text = "PR 说明", FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 8, 0, 0) });
        prStack.Children.Add(prBody);
        prStack.Children.Add(new TextBlock { Text = "推送与创建 PR 都会先显示包含完整 HEAD SHA 的确认框，默认「否」；不会因任务完成自动执行，也不会自动合并。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 8, 0, 8) });
        prStack.Children.Add(prButton);
        panel.Children.Add(Section("显式外部写入", pushRow, prStack));

        ciButton.Click += async (_, _) => await RunGuardedAsync(QueryCiAsync);
        AutomationProperties.SetName(ciButton, "查询此提交的 CI");
        AutomationProperties.SetAutomationId(ciButton, "ReviewQueryCi");
        AutomationProperties.SetAutomationId(ciPanel, "ReviewCiPanel");
        ciPanel.Children.Add(new TextBlock { Text = "尚未查询。CI 只匹配当前 HEAD 的完整 SHA；运行中、失败、无权限、网络不可用与缺少 workflow 都不会显示为通过。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Muted") });
        panel.Children.Add(Section("GitHub CI", ciButton, ciPanel));

        portsButton.Click += async (_, _) => await RunGuardedAsync(RefreshPortsAsync);
        AutomationProperties.SetName(portsButton, "刷新受管预览端口");
        AutomationProperties.SetAutomationId(portsButton, "ReviewRefreshPorts");
        AutomationProperties.SetAutomationId(portPanel, "ReviewPortsPanel");
        portPanel.Children.Add(new TextBlock { Text = "尚未读取受管预览端口。只允许在系统浏览器打开服务返回的本机 loopback 地址。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Muted") });
        panel.Children.Add(Section("本机预览", portsButton, portPanel));

        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return root;
    }

    private StackPanel Section(string title, params UIElement[] children)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    private async Task RunGuardedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { SetStatus("操作已取消；没有把取消当作通过，也没有自动重试。"); }
        catch (Exception e) when (e is not OutOfMemoryException) { SetStatus("操作失败（当前选择与草稿保留）：" + e.Message, true); }
        finally { if (busy) SetBusy(false); }
    }

    private void SetBusy(bool value, string? status = null)
    {
        busy = value;
        if (status is not null) SetStatus(status);
        UpdateActions();
    }

    private void SetStatus(string text, bool error = false)
    {
        statusText.Text = text;
        statusText.Foreground = (Brush)FindResource(error ? "ErrorInk" : "Muted");
    }

    private void UpdateActions()
    {
        var profile = (profilesCombo.SelectedItem as ComboBoxItem)?.Tag as VerificationProfile;
        refreshButton.IsEnabled = !busy && !closed;
        addProfileButton.IsEnabled = !busy && !closed;
        profilesCombo.IsEnabled = !busy && !closed;
        filePanel.IsEnabled = !busy && snapshot is not null && !closed;
        runButton.IsEnabled = ReviewWindowPolicy.RunReady(busy, snapshot, profile) && !closed;
        stopButton.IsEnabled = !closed && verifyCancellation is not null;
        pushButton.IsEnabled = !busy && snapshot is not null && !closed;
        prButton.IsEnabled = !busy && snapshot is not null && !closed;
        ciButton.IsEnabled = !busy && snapshot is not null && !closed;
        portsButton.IsEnabled = !busy && !closed;
        UpdateCommitState();
    }

    private void UpdateCommitState()
    {
        var selected = SelectedPaths();
        commitButton.IsEnabled = !busy && snapshot is not null && !closed && ReviewWindowPolicy.CommitReady(selected.Length, commitMessage.Text);
        if (snapshot is null)
        {
            commitPreview.Text = "尚未读取改动，提交不可用。";
            return;
        }
        if (selected.Length == 0)
        {
            commitPreview.Text = $"未勾选任何文件；不会自动提交未勾选或未跟踪文件。当前 HEAD {ReviewWindowPolicy.ShortSha(snapshot.HeadSha)}。";
            return;
        }
        var listed = string.Join("，", selected.Take(8));
        commitPreview.Text = $"将提交 {selected.Length} 个勾选文件：{listed}{(selected.Length > 8 ? $" 等 {selected.Length} 个" : "")} · 当前 HEAD {ReviewWindowPolicy.ShortSha(snapshot.HeadSha)}。";
    }

    private string[] SelectedPaths() => [.. fileChecks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).OrderBy(path => path, StringComparer.Ordinal)];

    private async Task RefreshCaptureAsync()
    {
        if (closed || busy) return;
        SetBusy(true, workspacePath is null ? "正在按该尝试计划锁定工作区…" : "正在读取基准、HEAD 与改动…");
        try
        {
            await EnsureWorkspaceAsync();
            await CaptureAndRenderAsync();
        }
        catch (OperationCanceledException)
        {
            InvalidateSnapshot("读取改动已取消；旧快照的提交/推送/检查授权已失效，请点「刷新改动」重新核对。");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            InvalidateSnapshot("读取改动失败，旧快照的提交/推送/检查授权已失效：" + e.Message);
        }
        finally { SetBusy(false); }
    }

    /// <summary>Locks this window to exactly one workspace: the attempt's planned root, never the current card project unless its physical identity matches.</summary>
    private async Task EnsureWorkspaceAsync()
    {
        if (workspacePath is not null) return;
        var resolved = await Task.Run(ResolveWorkspaceCore, lifetime.Token);
        workspacePath = resolved;
        workspaceText.Text = "工作区：" + resolved + " · 已按该尝试计划锁定";
        EnsureWatcher(resolved);
    }

    private string ResolveWorkspaceCore()
    {
        if (string.IsNullOrEmpty(run.WorkspaceKey)) throw new InvalidDataException("该尝试缺少工作区物理身份，无法确定审阅目录；已停止。");
        var (ledger, ledgerProblem) = ReadLedgerCore();
        var planned = ReviewWindowPolicy.LockedWorkspacePath(monitorEntries, run.AttemptId, run.BatchId, run.WorkspaceKey)
            ?? (ledger is null ? null : ReviewWindowPolicy.LockedWorkspacePath(ledger.Entries, run.AttemptId, run.BatchId, run.WorkspaceKey));
        if (planned is not null)
        {
            var identity = PhysicalDirectory.Resolve(planned);
            if (!ReviewWindowPolicy.WorkspaceKeyMatches(run.WorkspaceKey, identity))
                throw new IOException("控制账本记录的工作区物理身份与尝试记录不一致；已停止，不会审阅其它目录。");
            return identity.Path;
        }
        var fallback = PhysicalDirectory.Workspace(task.Project);
        if (!ReviewWindowPolicy.WorkspaceKeyMatches(run.WorkspaceKey, fallback))
            throw new IOException("无法把该尝试锁定到唯一工作区：监视状态与控制账本都没有该尝试的计划记录，任务项目目录的物理身份也与尝试记录不一致。已停止审阅，不会打开其它目录。"
                + (ledgerProblem is null ? "" : " 账本读取失败：" + ledgerProblem));
        return fallback.Path;
    }

    private static (Ledger? Ledger, string? Problem) ReadLedgerCore()
    {
        // Read-only: the ledger exists only while the monitor owns the attempt; reading it never starts the monitor or a task.
        try { return (Store.Read<Ledger>(Store.FilePath("control.json")), null); }
        catch (Exception e) when (e is not OutOfMemoryException) { return (null, e.Message); }
    }

    private void EnsureWatcher(string root)
    {
        if (watcher is not null) return;
        try
        {
            var watch = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            watch.Changed += (_, _) => NotifyWorkspaceChanged();
            watch.Created += (_, _) => NotifyWorkspaceChanged();
            watch.Deleted += (_, _) => NotifyWorkspaceChanged();
            watch.Renamed += (_, _) => NotifyWorkspaceChanged();
            watch.Error += (_, _) => NotifyWorkspaceChanged();
            watch.EnableRaisingEvents = true;
            watcher = watch;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetStatus("无法监视工作区文件变化；检查结果只在手动「刷新改动」后重新核对：" + e.Message, true);
        }
    }

    /// <summary>FileSystemWatcher callbacks run on a pool thread: only publish a coalesced signal, never touch WPF here.</summary>
    private void NotifyWorkspaceChanged()
    {
        if (closed) return;
        Interlocked.Increment(ref captureEpoch);
        if (Interlocked.Exchange(ref changeNotificationPending, 1) != 0) return;
        try { Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { Interlocked.Exchange(ref changeNotificationPending, 0); MarkResultsStale(); })); }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }

    private void MarkResultsStale()
    {
        if (closed || snapshot is null || freshnessDirty || Volatile.Read(ref captureEpoch) == validatedEpoch) return;
        freshnessDirty = true;
        RenderResults();
        filesSummary.Text = "工作区文件已变化；改动列表与检查结果都需要「刷新改动」重新核对。";
        if (!busy) SetStatus("检测到工作区文件变化：旧检查结果已置为过期/需刷新核对，刷新前不会视为当前有效。");
    }

    /// <summary>Re-reads base/HEAD/files for the locked workspace and re-renders results against the new snapshot.</summary>
    private async Task CaptureAndRenderAsync()
    {
        var path = workspacePath ?? throw new InvalidDataException("尚未锁定该尝试的工作区。");
        var epochBeforeCapture = Volatile.Read(ref captureEpoch);
        var captured = await service.CaptureAsync(path, capturedBaseSha, lifetime.Token);
        if (!string.Equals(captured.WorkspaceKey, run.WorkspaceKey, StringComparison.Ordinal))
            throw new IOException("读取到的工作区物理身份与该尝试记录不一致；已拒绝显示其它目录的改动。");
        var previous = fileChecks.ToDictionary(pair => pair.Key, pair => pair.Value.IsChecked == true, StringComparer.Ordinal);
        var previousHead = snapshot?.HeadSha;
        var previousWorktree = snapshot?.WorktreeFingerprint;
        snapshot = captured;
        validatedEpoch = epochBeforeCapture;
        freshnessDirty = Volatile.Read(ref captureEpoch) != validatedEpoch;
        workspaceText.Text = "工作区：" + captured.WorkspacePath + " · 已按该尝试计划锁定";
        baseText.Text = "基准 SHA：" + (captured.BaseSha.Length > 0 ? captured.BaseSha : "（未确定）");
        headText.Text = "HEAD：" + (captured.HeadSha.Length > 0 ? captured.HeadSha : "（未确定，可能是尚无提交的仓库）");
        RenderFiles(previous);
        if (ciReport is not null && !string.Equals(ciReport.Sha, captured.HeadSha, StringComparison.OrdinalIgnoreCase)) ciReport = null;
        RenderCi();
        var loaded = LoadBook();
        RenderResults();
        filesSummary.Text = captured.Files.Length == 0 ? "没有可审查的改动。" : $"共 {captured.Files.Length} 个文件：已跟踪修改默认勾选，未跟踪文件需手动勾选。";
        if (!loaded)
            SetStatus("已读取改动，但检查记录无法读取，检查结果不可用（原文件未被覆盖）：" + bookError, true);
        else if (previousHead is not null && !string.Equals(previousHead, captured.HeadSha, StringComparison.Ordinal))
            SetStatus($"HEAD 已从 {ReviewWindowPolicy.ShortSha(previousHead)} 变为 {ReviewWindowPolicy.ShortSha(captured.HeadSha)}；旧检查结果已按新工作区显示是否过期，CI 需重新查询。");
        else if (previousWorktree is not null && !string.Equals(previousWorktree, captured.WorktreeFingerprint, StringComparison.Ordinal))
            SetStatus("工作区内容已变化；检查结果已按新内容重新判定是否仍然有效。");
        else
            SetStatus($"已读取改动 · {captured.Files.Length} 个文件 · 基准 {ReviewWindowPolicy.ShortSha(captured.BaseSha)} · HEAD {ReviewWindowPolicy.ShortSha(captured.HeadSha)}");
    }

    /// <summary>A failed capture keeps drafts but drops every writable authorization derived from the old snapshot.</summary>
    private void InvalidateSnapshot(string status)
    {
        snapshot = null;
        ciReport = null;
        freshnessDirty = true;
        if (workspacePath is null) workspaceText.Text = "工作区：未锁定（不会审阅任何目录）";
        baseText.Text = "基准 SHA：未确定（读取未完成）";
        headText.Text = "HEAD：未确定（读取未完成）";
        diffTitle.Text = "差异 · 需要重新读取改动";
        diffBox.Text = "";
        filesSummary.Text = "改动列表已失效：本次读取没有完成；请点「刷新改动」重新核对。";
        RenderCi();
        RenderResults();
        UpdateActions();
        SetStatus(status, true);
    }

    private void RenderFiles(Dictionary<string, bool> previous)
    {
        filePanel.Children.Clear();
        fileChecks.Clear();
        if (snapshot is null) return;
        foreach (var file in snapshot.Files.OrderBy(candidate => candidate.Path, StringComparer.Ordinal))
        {
            var path = file.Path;
            var check = new CheckBox
            {
                IsChecked = previous.TryGetValue(path, out var wasChecked) ? wasChecked : ReviewWindowPolicy.DefaultChecked(file),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = ReviewWindowPolicy.FileLabel(file), TextWrapping = TextWrapping.Wrap }
            };
            AutomationProperties.SetName(check, "选择 " + path);
            check.Click += (_, _) => UpdateCommitState();
            var view = new Button { Content = "查看差异", MinWidth = 80, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(view, "查看差异 " + path);
            var captured = file;
            view.Click += async (_, _) => await RunGuardedAsync(() => ShowDiffAsync(captured));
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(check, 0);
            Grid.SetColumn(view, 1);
            row.Children.Add(check);
            row.Children.Add(view);
            fileChecks[path] = check;
            filePanel.Children.Add(row);
        }
        UpdateCommitState();
    }

    private async Task ShowDiffAsync(ReviewFile file)
    {
        if (busy || snapshot is null || closed) return;
        var current = snapshot;
        SetBusy(true, "正在读取差异：" + file.Path);
        try
        {
            var diff = await service.DiffAsync(current, file.Path, lifetime.Token);
            diffTitle.Text = "差异 · " + file.Path + (file.Binary ? " · 二进制摘要" : "");
            diffBox.Text = ReviewWindowPolicy.DiffDisplay(diff);
            diffBox.ScrollToHome();
            SetStatus("已显示有界差异。" + (diff.Truncated ? "输出已按上限截断。" : ""));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetStatus("读取差异失败（保留当前内容）：" + e.Message, true);
        }
        finally { SetBusy(false); }
    }

    private bool LoadBook(Guid? selectProfile = null)
    {
        try { book = store.Load(); bookError = ""; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            book = null;
            bookError = e.Message;
            profilesCombo.Items.Clear();
            resultPanel.Children.Clear();
            resultOutput.Visibility = Visibility.Collapsed;
            SetStatus("检查记录无法读取，未覆盖原文件：" + e.Message, true);
            UpdateActions();
            return false;
        }
        var selectedId = selectProfile;
        if (selectedId is null && profilesCombo.SelectedItem is ComboBoxItem { Tag: VerificationProfile current }) selectedId = current.Id;
        profilesCombo.Items.Clear();
        foreach (var profile in book.Profiles.OrderBy(candidate => candidate.Name, StringComparer.CurrentCulture))
            profilesCombo.Items.Add(new ComboBoxItem { Content = $"{profile.Name} · {Path.GetFileName(profile.Command.Executable)} · {profile.TimeoutSeconds}s", Tag = profile });
        var match = selectedId is { } wanted
            ? profilesCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is VerificationProfile candidate && candidate.Id == wanted)
            : null;
        if (match is not null) profilesCombo.SelectedItem = match;
        else profilesCombo.SelectedIndex = -1;
        UpdateCommandPreview();
        return true;
    }

    private void UpdateCommandPreview()
    {
        if (profilesCombo.SelectedItem is ComboBoxItem { Tag: VerificationProfile profile })
        {
            var arguments = string.Join(" ", profile.Command.Arguments.Select(argument => argument.Length == 0 ? "\"\"" : argument.Contains(' ') ? "\"" + argument + "\"" : argument));
            var text = profile.Command.Executable + (arguments.Length > 0 ? " " + arguments : "");
            commandPreview.Text = text.Length > 1200 ? text[..1200] + " …（预览已截断，实际以保存的逐项 argv 为准）" : text;
            return;
        }
        commandPreview.Text = "未选择检查；「运行检查」只在选择已保存的命名命令后可用。";
    }

    private async Task AddProfileAsync()
    {
        if (busy || closed) return;
        var dialog = new FormWindow(this, "新增检查",
            [("name", "检查名称", "", false), ("exe", "可执行文件（完整路径）", "", false), ("args", "逐项参数（每个参数一行；可留空）", "", false), ("timeout", "超时（秒，1–3600）", "120", false)],
            "检查只运行你明确填写并保存的命名命令（EXE + 逐项参数），不会自动执行仓库脚本。首次使用必须先选择或新增并保存，再点「运行检查」。",
            argumentLists: new() { ["args"] = [] });
        if (dialog.ShowDialog() != true) return;
        // Read the dialog controls on the UI thread; the service/store work below never touches WPF.
        var name = dialog.Values["name"].Trim();
        var executable = dialog.Values["exe"].Trim();
        var arguments = dialog.ArgumentValues["args"];
        if (!int.TryParse(dialog.Values["timeout"].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout))
        {
            SetStatus("检查超时必须是 1–3600 之间的整数秒。", true);
            return;
        }
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("检查程序必须是存在的完整 .exe 路径（不自动执行仓库脚本或 .cmd）。", true);
            return;
        }
        var profile = new VerificationProfile(Guid.NewGuid(), name, new CommandSpec(executable, arguments, "exe"), timeout);
        try { ReviewPolicy.ValidateProfile(profile); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            SetStatus("检查配置无效：" + e.Message, true);
            return;
        }
        SetBusy(true, "正在保存检查配置…");
        try { await Task.Run(() => store.SaveProfile(profile)); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetStatus("检查配置保存失败（原记录未覆盖）：" + e.Message, true);
            return;
        }
        finally { SetBusy(false); }
        if (!LoadBook(profile.Id)) return;
        SetStatus($"已保存检查「{profile.Name}」；点「运行检查」后才会启动进程。");
    }

    private async Task RunCheckAsync()
    {
        if (busy || closed) return;
        if (profilesCombo.SelectedItem is not ComboBoxItem { Tag: VerificationProfile profile })
        {
            SetStatus("请先选择已保存的命名检查；首次使用请「新增检查…」。不会自动执行仓库脚本。", true);
            return;
        }
        if (snapshot is null)
        {
            SetStatus("尚未读取改动，不能运行检查。", true);
            return;
        }
        try { ReviewPolicy.ValidateProfile(profile); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            SetStatus("检查配置无效：" + e.Message, true);
            return;
        }
        var current = snapshot;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        verifyCancellation = cancellation;
        SetBusy(true, $"检查运行中：{profile.Name}。可点「停止检查」取消；只终止本次检查自己的 Job。");
        try
        {
            var result = await service.VerifyAsync(run.AttemptId, current, profile, cancellation.Token);
            transientResult = result;
            var saveProblem = "";
            try { await Task.Run(() => store.SaveResult(result)); }
            catch (Exception e) when (e is not OutOfMemoryException) { saveProblem = "；结果未能保存：" + e.Message; }
            // The check may have changed the worktree: re-read before rendering so a run-time change can never leave an old pass looking current.
            try { await CaptureAndRenderAsync(); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                InvalidateSnapshot("检查已完成，但检查后的工作区重新读取失败；旧快照与检查结果授权已失效：" + e.Message);
                return;
            }
            if (!LoadBook(profile.Id)) return;
            RenderResults();
            if (bookError.Length > 0) { SetStatus("检查已完成，但检查记录无法读取，结果不可用（原文件未被覆盖）：" + bookError, true); return; }
            var failed = result.State is "Failed" or "TimedOut" or "Cancelled" or "Stale";
            var stillCurrent = snapshot is not null && IsResultCurrent(result, profile);
            var freshness = stillCurrent ? "" : "；检查期间或之后工作区已变化，本次结果已标记为过期/需刷新核对";
            SetStatus($"检查完成：{ReviewWindowPolicy.StateLabel(result.State)} · {profile.Name} · {ReviewWindowPolicy.ShortSha(result.HeadSha)}{freshness}{saveProblem}", failed || saveProblem.Length > 0 || !stillCurrent);
        }
        catch (OperationCanceledException)
        {
            MarkCheckOutcomeUnknown();
            SetStatus("检查已取消；不会把取消当作通过，也不会自动重试。旧结果需「刷新改动」重新核对。");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            MarkCheckOutcomeUnknown();
            SetStatus("检查失败（保留当前选择）；旧结果需「刷新改动」重新核对：" + e.Message, true);
        }
        finally
        {
            verifyCancellation = null;
            cancellation.Dispose();
            SetBusy(false);
        }
    }

    private void MarkCheckOutcomeUnknown()
    {
        if (snapshot is null || freshnessDirty || closed) return;
        freshnessDirty = true;
        RenderResults();
    }

    private void RenderResults()
    {
        resultPanel.Children.Clear();
        if (book is null) return;
        var results = ReviewWindowPolicy.ResultsFor(book, run.AttemptId).ToList();
        if (transientResult is { } transient && results.All(result => result.Id != transient.Id)) results.Insert(0, transient);
        if (results.Count == 0)
        {
            resultPanel.Children.Add(new TextBlock { Text = "本任务还没有检查结果。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Muted") });
            resultOutput.Visibility = Visibility.Collapsed;
            return;
        }
        if (freshnessDirty || snapshot is null)
            resultPanel.Children.Add(new TextBlock
            {
                Text = "以下结果需刷新核对：工作区已有未重新读取的变化（或上一次读取失败），刷新前不会视为当前有效。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = (Brush)FindResource("WarningInk")
            });
        foreach (var result in results)
        {
            var profile = book.Profiles.FirstOrDefault(candidate => candidate.Id == result.CommandId);
            var current = IsResultCurrent(result, profile);
            var unsaved = transientResult?.Id == result.Id && book.Results.All(saved => saved.Id != result.Id);
            resultPanel.Children.Add(new TextBlock
            {
                Text = ReviewWindowPolicy.ResultStatus(result, profile?.Name ?? "已删除的检查", current, unsaved),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = (Brush)FindResource(current ? "Ink" : "Muted")
            });
        }
        var newest = results[0];
        var output = string.Join("\n", new[] { newest.Output, newest.Error }.Where(text => !string.IsNullOrWhiteSpace(text)));
        resultOutput.Text = output.Length > 0 ? output : "（没有输出）";
        resultOutput.Visibility = Visibility.Visible;
    }

    private bool IsResultCurrent(VerificationResult result, VerificationProfile? profile)
    {
        if (snapshot is null || profile is null || freshnessDirty) return false;
        try { return ReviewPolicy.IsCurrent(result, run.AttemptId, snapshot, ReviewService.CommandFingerprint(profile)); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    private async Task CommitAsync()
    {
        if (busy || snapshot is null || closed) return;
        var current = snapshot;
        // Read the user's explicit selection and message on the UI thread before any await.
        var selected = SelectedPaths();
        var message = commitMessage.Text.Trim();
        if (selected.Length == 0) { SetStatus("未勾选任何文件，不会提交。", true); return; }
        if (message.Length == 0) { SetStatus("提交消息为空；请先填写提交消息。", true); return; }
        SetBusy(true, "正在重核 HEAD、索引与所选内容…");
        string committed;
        try
        {
            committed = await service.CommitAsync(current, selected, message, lifetime.Token);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetStatus("提交被拒绝（勾选与消息保留）：" + e.Message, true);
            return;
        }
        finally { SetBusy(false); }
        commitMessage.Text = "";
        SetStatus("已提交 " + committed + "；正在刷新改动与新的 HEAD。");
        await RefreshCaptureAsync();
    }

    private async Task PushAsync()
    {
        if (busy || snapshot is null || closed) return;
        var current = snapshot;
        if (Confirm(ReviewWindowPolicy.PushPrompt(current), "确认推送") != MessageBoxResult.Yes) return;
        SetBusy(true, "正在推送当前 HEAD…");
        try
        {
            var result = await service.PushAsync(current, lifetime.Token);
            SetStatus("推送完成：" + result);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetStatus("推送失败：" + e.Message, true);
        }
        finally { SetBusy(false); }
    }

    private async Task CreatePullRequestAsync()
    {
        if (busy || snapshot is null || closed) return;
        var current = snapshot;
        var title = prTitle.Text.Trim();
        var body = prBody.Text;
        if (title.Length == 0) { SetStatus("PR 标题不能为空。", true); return; }
        if (Confirm(ReviewWindowPolicy.PrPrompt(title, current), "确认创建 Pull Request") != MessageBoxResult.Yes) return;
        SetBusy(true, "正在创建 Pull Request…");
        try
        {
            var url = await service.CreatePullRequestAsync(current, title, body, lifetime.Token);
            SetStatus("已创建 Pull Request：" + url);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetStatus("创建 PR 失败（标题与说明保留）：" + e.Message, true);
        }
        finally { SetBusy(false); }
    }

    private MessageBoxResult Confirm(string text, string title)
    {
        return MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
    }

    private async Task QueryCiAsync()
    {
        if (busy || snapshot is null || closed) return;
        var current = snapshot;
        SetBusy(true, "正在查询目标 SHA 的 CI…");
        try
        {
            var report = await service.GetCiAsync(current, lifetime.Token);
            ciReport = report;
            RenderCi();
            var appearance = ReviewWindowPolicy.CiAppearance(report, current.HeadSha);
            SetStatus("CI：" + appearance.Label, appearance.Error);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetStatus("CI 查询失败（状态未知，不能视为通过）：" + e.Message, true);
        }
        finally { SetBusy(false); }
    }

    private void RenderCi()
    {
        ciPanel.Children.Clear();
        if (ciReport is null)
        {
            ciPanel.Children.Add(new TextBlock { Text = "尚未查询。CI 只匹配当前 HEAD 的完整 SHA；网络不可达、无权限或缺少 workflow 都不会显示为通过。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Muted") });
            return;
        }
        if (snapshot is null) return;
        var appearance = ReviewWindowPolicy.CiAppearance(ciReport, snapshot.HeadSha);
        var tone = appearance.Green ? new SolidColorBrush(Color.FromRgb(0x1E, 0x7B, 0x34))
            : appearance.Error ? (Brush)FindResource("ErrorInk")
            : appearance.Warning ? (Brush)FindResource("WarningInk")
            : (Brush)FindResource("Muted");
        ciPanel.Children.Add(new TextBlock { Text = appearance.Label, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Foreground = tone, Margin = new Thickness(0, 4, 0, 4) });
        ciPanel.Children.Add(new TextBlock { Text = $"仓库：{ciReport.Repository} · 报告 SHA：{ciReport.Sha} · 当前 HEAD：{snapshot.HeadSha}", TextWrapping = TextWrapping.Wrap, FontSize = 12, FontFamily = new FontFamily("Consolas, Courier New") });
        if (ciReport.Message.Length > 0)
            ciPanel.Children.Add(new TextBlock { Text = ciReport.Message, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        foreach (var ciRun in ciReport.Runs.Take(20))
        {
            ciPanel.Children.Add(new TextBlock
            {
                Text = $"{ciRun.Name} · {ciRun.Status}{(ciRun.Conclusion is { Length: > 0 } conclusion ? " / " + conclusion : "")} · {ciRun.Sha}",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = (Brush)FindResource("Muted")
            });
        }
        if (ciReport.Message.Length == 0 && ciReport.Runs.Length == 0 && appearance.Green)
            ciPanel.Children.Add(new TextBlock { Text = "目标 SHA 的检查全部成功。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Muted") });
    }

    private async Task RefreshPortsAsync()
    {
        if (busy || closed) return;
        SetBusy(true, "正在读取受管预览端口…");
        try
        {
            var ports = await service.PreviewPortsAsync(run.AttemptId, lifetime.Token);
            RenderPorts(ports);
            SetStatus(ports.Count == 0 ? "没有检测到该尝试的受管预览端口。" : $"已列出 {ports.Count} 个受管预览端口；只接受本机 loopback 地址。");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetStatus("读取受管预览端口失败：" + e.Message, true);
        }
        finally { SetBusy(false); }
    }

    private void RenderPorts(IReadOnlyList<ManagedPreviewPort> ports)
    {
        portPanel.Children.Clear();
        if (ports.Count == 0)
        {
            portPanel.Children.Add(new TextBlock { Text = "没有检测到受管预览端口。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Muted") });
            return;
        }
        foreach (var port in ports.OrderBy(candidate => candidate.Port))
        {
            var open = new Button { Content = "在浏览器打开", MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };
            AutomationProperties.SetName(open, $"在浏览器打开端口 {port.Port}");
            var url = port.Url;
            open.Click += (_, _) => OpenPreview(url);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(open);
            row.Children.Add(new TextBlock { Text = $"{url} · PID {port.ProcessId}", TextWrapping = TextWrapping.Wrap, FontSize = 12, FontFamily = new FontFamily("Consolas, Courier New"), VerticalAlignment = VerticalAlignment.Center });
            portPanel.Children.Add(row);
        }
    }

    private void OpenPreview(string url)
    {
        if (!ReviewWindowPolicy.IsLoopbackUrl(url))
        {
            SetStatus("拒绝打开非本机 loopback 预览地址：" + url, true);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            SetStatus("已请求系统默认浏览器打开预览端口。");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetStatus("无法打开浏览器：" + e.Message, true);
        }
    }
}

/// <summary>Pure review UI policy shared with the WPF regression checks; no WPF or process access.</summary>
internal static class ReviewWindowPolicy
{
    internal readonly record struct CiVisual(string Label, bool Green, bool Error, bool Warning);

    /// <summary>
    /// Returns the attempt's planned workspace path, or null when the plan itself is absent. A plan that names the
    /// attempt but disagrees with the run's batch or physical workspace key throws instead of silently reviewing another directory.
    /// </summary>
    internal static string? LockedWorkspacePath(IEnumerable<ControlEntry>? entries, Guid attemptId, Guid batchId, string expectedKey)
    {
        if (string.IsNullOrEmpty(expectedKey)) throw new InvalidDataException("尝试记录缺少工作区物理身份，无法锁定审阅目录。");
        var entry = entries?.FirstOrDefault(candidate => candidate?.Plan is { } plan && plan.Id == attemptId);
        if (entry is null) return null;
        if (entry.Plan.BatchId != batchId) throw new InvalidDataException("控制账本中该尝试的批次与任务记录不一致，拒绝审阅。");
        if (!string.Equals(entry.Plan.Workspace.Key, expectedKey, StringComparison.Ordinal))
            throw new InvalidDataException("控制账本中该尝试的工作区物理身份与任务记录不一致，拒绝审阅。");
        if (string.IsNullOrWhiteSpace(entry.Plan.Workspace.Path) || !Path.IsPathFullyQualified(entry.Plan.Workspace.Path))
            throw new InvalidDataException("控制账本中该尝试的工作区路径无效，拒绝审阅。");
        return entry.Plan.Workspace.Path;
    }

    internal static bool WorkspaceKeyMatches(string expectedKey, WorkspaceIdentity identity) =>
        !string.IsNullOrEmpty(expectedKey) && string.Equals(expectedKey, identity.Key, StringComparison.Ordinal);

    internal static bool DefaultChecked(ReviewFile file) => !file.Untracked;

    internal static string FileLabel(ReviewFile file)
    {
        var parts = new List<string> { file.Path, file.Status };
        if (file.Untracked) parts.Add("未跟踪；需勾选");
        if (file.Binary) parts.Add("二进制");
        parts.Add(FormatSize(file.SizeBytes));
        return string.Join(" · ", parts);
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KiB",
        < 1024L * 1024 * 1024 => (bytes / 1048576d).ToString("0.0", CultureInfo.InvariantCulture) + " MiB",
        _ => (bytes / 1073741824d).ToString("0.0", CultureInfo.InvariantCulture) + " GiB"
    };

    internal static string DiffDisplay(ReviewDiff diff)
    {
        var text = diff.Text;
        if (diff.Truncated) text += (text.Length > 0 ? "\n\n" : "") + "……输出已按上限截断。";
        if (!string.IsNullOrWhiteSpace(diff.Summary)) text += (text.Length > 0 ? "\n\n" : "") + diff.Summary;
        return text.Length > 0 ? text : "没有可显示的差异文本。";
    }

    internal static bool CommitReady(int selectedCount, string message) => selectedCount > 0 && !string.IsNullOrWhiteSpace(message);

    internal static bool RunReady(bool busy, ReviewSnapshot? snapshot, VerificationProfile? profile) => !busy && snapshot is not null && profile is not null;

    internal static bool IsLoopbackUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        if (uri.UserInfo.Length > 0) return false;
        if (uri.Port is < 1 or > 65535) return false;
        var host = uri.DnsSafeHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    internal static IReadOnlyList<VerificationResult> ResultsFor(ReviewBook book, Guid taskRunId) =>
        [.. book.Results.Where(result => result.TaskRunId == taskRunId).OrderByDescending(result => result.At).Take(20)];

    internal static string StateLabel(string state) => state switch
    {
        "Passed" => "通过",
        "Failed" => "失败",
        "Cancelled" => "已取消",
        "TimedOut" => "超时",
        "Stale" => "过期",
        "Running" => "运行中",
        _ => state
    };

    internal static string ResultStatus(VerificationResult result, string profileName, bool current, bool unsaved)
    {
        var state = StateLabel(result.State);
        var freshness = current ? "当前有效" : "过期（SHA、工作区或命令已变化）";
        return $"{profileName} · {state} · {freshness} · {ShortSha(result.HeadSha)} · {result.At.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)}" + (unsaved ? " · 未保存" : "");
    }

    internal static string ShortSha(string sha) => string.IsNullOrEmpty(sha) ? "（无）" : sha.Length <= 12 ? sha : sha[..12];

    internal static CiVisual CiAppearance(CiReport report, string targetSha)
    {
        if (!string.Equals(report.Sha, targetSha, StringComparison.OrdinalIgnoreCase))
            return new CiVisual("SHA 不匹配：报告 " + ShortSha(report.Sha) + "，当前 " + ShortSha(targetSha) + "；不能视为通过", false, true, false);
        var state = report.State;
        return state switch
        {
            "Passed" => new CiVisual("通过：目标 SHA 的检查已成功", true, false, false),
            "Failed" => new CiVisual("失败：目标 SHA 的检查未通过", false, true, false),
            "Running" => new CiVisual("运行中：结果未定，不能视为通过", false, false, true),
            "NoWorkflow" => new CiVisual("没有工作流：该 SHA 没有可报告的检查", false, false, true),
            "Unauthorized" => new CiVisual("无权限：无法读取该仓库的检查", false, false, true),
            "Unavailable" => new CiVisual("网络/服务不可用：CI 状态未知", false, false, true),
            _ => new CiVisual("未知：CI 状态未确认" + (state.Length > 0 ? "（" + state + "）" : ""), false, false, true)
        };
    }

    internal static string PushPrompt(ReviewSnapshot snapshot) =>
        $"将把当前 HEAD {snapshot.HeadSha} 推送到该仓库的远程分支。\n此操作会改变远程仓库，不会自动创建 PR 或合并。\n仅在你确认后执行。";

    internal static string PrPrompt(string title, ReviewSnapshot snapshot) =>
        $"将为当前 HEAD {snapshot.HeadSha} 创建 Pull Request：\n标题：{title}\n此操作会在 GitHub 创建 PR，不会自动合并或删除分支。\n仅在你确认后执行。";
}
