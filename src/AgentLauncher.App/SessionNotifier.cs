using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AgentLauncher.Core;

namespace AgentLauncher.App;

internal sealed record ToastRequest(string Category, string Title, string Message, Guid EntryId);

// Runtime-only session event notifications. The owner is held weakly via the registry so the notifier never
// keeps a closed window alive. All delivery is client-side: no Worker protocol change, no third-party dependency.
internal sealed class SessionNotifier
{
    private static readonly ConditionalWeakTable<Window, SessionNotifier> Registry = new();
    private readonly Window owner;
    private readonly Dictionary<Guid, string> lastKeys = [];
    private readonly Dictionary<string, DateTimeOffset> lastShown = new(StringComparer.Ordinal);
    private readonly List<ToastPopup> active = [];
    private readonly List<ToastRequest> delivered = [];
    private IReadOnlyDictionary<Guid, ControlEntry> previous = new Dictionary<Guid, ControlEntry>();
    private bool armed;

    internal SessionNotifier(Window owner) => this.owner = owner;
    internal static SessionNotifier For(Window owner) => Registry.GetValue(owner, _ => new SessionNotifier(owner));

    // Test seams used by UiChecks (integration builds only). Production never sets them.
    internal bool TestMode { get; set; }
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
    internal IReadOnlyList<ToastRequest> Delivered => delivered;

    internal void Reset()
    {
        lastKeys.Clear(); lastShown.Clear(); delivered.Clear();
        previous = new Dictionary<Guid, ControlEntry>(); armed = false;
        foreach (var toast in active.ToArray()) toast.Close();
    }

    // Called on every monitor-status refresh. The first observation only establishes the baseline, so states
    // that already existed when the App started are never back-notified.
    internal void Observe(IReadOnlyList<ControlEntry>? entries, bool enabled)
    {
        var current = entries ?? [];
        var map = new Dictionary<Guid, ControlEntry>();
        foreach (var entry in current) if (!map.ContainsKey(entry.Plan.Id)) map[entry.Plan.Id] = entry;
        if (!armed) { armed = true; previous = map; return; }
        if (!enabled) { previous = map; return; }

        var events = new List<ToastRequest>();
        foreach (var entry in current)
        {
            if (!previous.TryGetValue(entry.Plan.Id, out var before)) continue;
            var name = SafeName(entry.Plan.Name);
            var detail = entry.Detail ?? "";
            var earlierDetail = before.Detail ?? "";

            void Add(string category, string title, string message)
            {
                var fingerprint = $"{entry.Plan.Id}|{category}|{entry.State}|{detail}";
                if (lastKeys.TryGetValue(entry.Plan.Id, out var last) && last == fingerprint) return;
                lastKeys[entry.Plan.Id] = fingerprint;
                events.Add(new ToastRequest(category, title, message, entry.Plan.Id));
            }

            // Memory limit touch, including the shared pool. Detail carries paths/arguments, so only fixed
            // wording and a sanitized name are ever placed in the toast text.
            if (detail.Contains("触及") && !earlierDetail.Contains("触及"))
                Add("memory", name + " 触及内存上限",
                    detail.Contains("共享池") ? "共享池分配被拒绝，保护已生效。" : "窗格分配被拒绝，保护已生效。");

            if (entry.State == "FailedBeforeStart" && before.State != "FailedBeforeStart")
                Add("failed", name + " 启动前失败", "进程未启动，可到运行记录查看。");
            else if (entry.State == "Disconnected" && before.State != "Disconnected")
                Add("disconnected", name + " SSH 连接断开", "远程进程可能仍在运行，请手动核对远程主机状态后再决定操作。");
            else if ((entry.State is "Unknown" or "NeedsReconciliation") && before.State is not ("Unknown" or "NeedsReconciliation"))
                Add("unknown", name + " 状态待核对", "占用尚未确认，请查看运行记录。");
            else if ((entry.State is "Settled" or "Exited" or "Finished") && before.State != entry.State && before.State is not ("Settled" or "Finished"))
            {
                var code = entry.ExitCode ?? ExtractExitCode(detail);
                Add(entry.State == "Exited" ? "agent-exited" : "finished", name + (entry.State == "Exited" ? " Agent 已退出" : " 会话已结束"),
                    entry.State == "Exited" ? "Agent 已退出，派生工具仍在运行，请核对运行记录。" : code is null ? "受管进程已全部退出。" : $"退出码 {code}，受管进程已全部退出。");
            }

            // A resource-waiting entry (Pending/Creating) entering Started means its queue wait was released.
            if (entry.State == "Started" && (before.State is "Pending" or "Creating"))
                Add("started", name + " 已解除资源等待", "等待结束后已开始运行。");
        }

        previous = map;
        foreach (var toast in events) Deliver(toast);
        var live = current.Select(e => e.Plan.Id).ToHashSet();
        foreach (var key in lastKeys.Keys.Where(k => !live.Contains(k)).ToArray()) lastKeys.Remove(key);
    }

    private void Deliver(ToastRequest request)
    {
        var now = Now();
        if (lastShown.TryGetValue(request.Category, out var at) && now - at < TimeSpan.FromSeconds(2)) return;
        lastShown[request.Category] = now;
        delivered.Add(request);
        if (delivered.Count > 100) delivered.RemoveAt(0);
        if (TestMode) return;

        Brush Resource(string key, Brush fallback) => owner.TryFindResource(key) as Brush ?? fallback;
        var popup = new ToastPopup(request,
            Resource("Surface", Brushes.White),
            Resource("Ink", Brushes.Black),
            Resource("Muted", Brushes.Gray),
            Resource("Accent", Brushes.RoyalBlue),
            play: () => Navigate(request),
            dismissed: toast =>
            {
                active.Remove(toast);
                for (var i = 0; i < active.Count; i++) active[i].Restack(i);
            });
        active.Add(popup);
        popup.Restack(active.Count - 1);
        popup.Show();
        if (active.Count > 3) active[0].Close();
    }

    // Click only activates the existing window and scrolls to the matching row; it never launches or closes sessions.
    private void Navigate(ToastRequest request)
    {
        try
        {
            if(owner is MainWindow main)main.RestoreBackground();else owner.Activate();
            var needle = SafeNameFromTitle(request.Title);
            bool FocusIn(Panel panel)
            {
                foreach (var item in panel.Children)
                {
                    if (item is not FrameworkElement child) continue;
                    if (needle.Length > 0 && Descendants(child).OfType<TextBlock>().Any(t => t.Text.Contains(needle, StringComparison.Ordinal)))
                    {
                        child.BringIntoView();
                        return true;
                    }
                }
                return false;
            }
            if (owner.FindName("ActiveSessions") is Panel active && FocusIn(active))
            {
                if (owner.FindName("ActiveNow") is FrameworkElement now) now.BringIntoView();
                return;
            }
            if (owner.FindName("SessionExpander") is Expander expander) expander.IsExpanded = true;
            if (owner.FindName("Sessions") is Panel panel) FocusIn(panel);
        }
        catch (InvalidOperationException) { /* window closing during click */ }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static string SafeNameFromTitle(string title)
    {
        var suffix = " 触及内存上限| 启动前失败| 状态待核对| 会话已结束| 已解除资源等待";
        foreach (var part in suffix.Split('|'))
            if (title.EndsWith(part, StringComparison.Ordinal)) return title[..^part.Length];
        return "";
    }

    internal static string SafeName(string? name)
    {
        var value = name ?? "";
        var cut = value.IndexOfAny(['\\', '/', ':', '\r', '\n']);
        if (cut >= 0) value = value[..cut];
        value = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim().TrimStart('-', '/');
        return value.Length == 0 ? "会话" : value.Length > 24 ? value[..24] : value;
    }

    internal static int? ExtractExitCode(string detail)
    {
        var match = Regex.Match(detail, @"退出码\s*(-?\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }
}

internal sealed class ToastPopup : Window
{
    private readonly Action<ToastPopup> dismissed;
    private readonly Border card;

    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    internal ToastPopup(ToastRequest request, Brush surface, Brush ink, Brush muted, Brush accent, Action play, Action<ToastPopup> dismissed)
    {
        this.dismissed = dismissed;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        Topmost = true; ShowActivated = false; Focusable = false; AllowsTransparency = true;
        Background = null; Width = 336; Height = 92;
        card = new Border
        {
            Background = surface, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10),
            BorderThickness = new Thickness(1), BorderBrush = accent,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.28 }
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = request.Title, Foreground = ink, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(new TextBlock { Text = request.Message, Foreground = muted, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        card.Child = stack;
        Content = card;
        Cursor = System.Windows.Input.Cursors.Hand;
        MouseLeftButtonUp += (_, _) => { play(); Close(); };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        timer.Start();
        Closed += (_, _) => { timer.Stop(); dismissed(this); };
    }

    internal void Restack(int index)
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 16;
        Top = work.Bottom - Height - 16 - index * (Height + 8);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 | 0x80); // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
    }
}
