using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

if (args is ["--pane-ready", var readyPath, var releasePath, .. var extraArguments])
{
    // 治具窗格子进程：原子写出就绪证据（Pid/Cwd/Args），等待共享 release 文件后以 0 退出；60 秒超时返回 3。
    var pendingReady = readyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        using (var stream = new FileStream(pendingReady, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            JsonSerializer.Serialize(stream, new { Pid = Environment.ProcessId, Cwd = Environment.CurrentDirectory, Args = extraArguments });
            stream.Flush(true);
        }
        File.Move(pendingReady, readyPath); // 原子可见；绝不覆盖既有就绪证据。
    }
    finally { if (File.Exists(pendingReady)) File.Delete(pendingReady); }
    var fixtureWatch = Stopwatch.StartNew();
    while (!File.Exists(releasePath))
    {
        if (fixtureWatch.Elapsed > TimeSpan.FromSeconds(60)) return 3;
        Thread.Sleep(50);
    }
    return 0;
}

if (args is not ["--run", "--n", var countText, "--terminal", var terminal, "--evidence", var evidenceDirectory]
    || !int.TryParse(countText, out var paneCount) || paneCount is < 1 or > 4)
{
    Console.Error.WriteLine("用法: AgentLauncher.LayoutChecks --run --n <1-4> --terminal <wt.exe> --evidence <目录>");
    return 2;
}
return await LayoutChecks.RunAsync(paneCount, terminal, evidenceDirectory);

internal static class LayoutChecks
{
    public static async Task<int> RunAsync(int paneCount, string terminal, string evidenceDirectory)
    {
        var root = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT");
        if (root is not { Length: > 0 } || !root.Contains("AgentLauncher-tests-"))
            throw new InvalidOperationException("布局实机验证必须使用隔离的 AGENT_LAUNCHER_TEST_ROOT 测试根。");
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", "1"); // 与 test.ps1 相同：隔离内存采样。
        if (!File.Exists(terminal)) throw new FileNotFoundException("Terminal 路径不存在：" + terminal);
        if (!File.Exists(Store.WorkerPath)) throw new FileNotFoundException("缺少 AgentLauncher.Worker.exe，请从发布目录运行：" + Store.WorkerPath);
        if (Directory.Exists(evidenceDirectory)) throw new IOException("证据目录已存在：" + evidenceDirectory);
        Directory.CreateDirectory(evidenceDirectory);
        Directory.CreateDirectory(Store.Root);
        Console.OutputEncoding = new UTF8Encoding(false);

        var checks = new List<object>(); var limitations = new List<string>(); var children = new List<object>();
        var passed = true; var releaseWritten = false; NativeWin.Window? batchWindow = null;
        void Check(bool ok, string name, string detail)
        {
            checks.Add(new { Name = name, Passed = ok, Detail = detail });
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length == 0 ? "" : "：") + detail);
            if (!ok) passed = false;
        }
        static string ExpectedTitle(LaunchItem item)
        {
            var folder = Path.GetFileName(item.Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return $"{item.Position:00}  {item.Name} · {folder}"; // 与 TerminalAppearance.AddPaneArguments 的标题格式一致。
        }

        var batch = Guid.NewGuid();
        var token = "布局治具 " + batch.ToString("N")[..8]; // 窗口标题检索令牌，逐批唯一。
        var cwd = Path.Combine(Store.Root, "布局 & 治具");
        Directory.CreateDirectory(cwd);
        var release = Path.Combine(Store.Root, "layout.release");
        var entries = Enumerable.Range(1, paneCount).Select(n =>
        {
            var ready = Path.Combine(Store.Root, $"layout-ready-{n}.json");
            return (Item: new LaunchItem(Guid.NewGuid(), batch, n, $"{token} {n}", cwd, PhysicalDirectory.Workspace(cwd), true,
                new CommandSpec(Environment.ProcessPath!, ["--pane-ready", ready, release, $"中文 & ; 参数{n}"], "exe"), ResourcePolicy.AgentBudget), Ready: ready);
        }).ToArray();
        var plans = entries.Select(entry => entry.Item).ToArray();
        var firstIndex = PaneLayout.Map(paneCount, "vertical").First(cell => cell.CreationOrder == 0).EnabledIndex;
        var expectedTitle = ExpectedTitle(plans[firstIndex]);
        limitations.Add("WT 窗格非独立 HWND，无程序化几何源；列等宽、行等高只能人工核对截图。");
        limitations.Add("本轮按指令未纳入自动截图；几何证据需人工截图核对。");

        try
        {
            var monitor = await Ipc.EnsureMonitorAsync();
            Check(monitor.Ok, "监控器就绪", monitor.Message);
            if (!monitor.Ok) throw new IOException("监控器未就绪：" + monitor.Message);
            var reserve = await Ipc.SendAsync(new Request("reserve", Guid.NewGuid(), plans));
            Check(reserve.Ok, paneCount + " 个治具启动项被监控器预约", reserve.Ok ? "" : reserve.Message);
            if (!reserve.Ok) throw new IOException("预约失败：" + reserve.Message);
            Commands.SubmitTerminal(terminal, plans, "vertical");
            Check(true, "分屏命令已提交到真实 Windows Terminal", terminal);

            var readyWatch = Stopwatch.StartNew();
            while (readyWatch.Elapsed < TimeSpan.FromSeconds(40) && entries.Any(entry => !File.Exists(entry.Ready))) await Task.Delay(100);
            var missing = entries.Where(entry => !File.Exists(entry.Ready)).Select(entry => entry.Item.Position).ToArray();
            Check(missing.Length == 0, paneCount + " 个窗格子进程全部就绪", missing.Length == 0 ? "全部写出 ready 证据" : "缺失位置：" + string.Join(",", missing));
            foreach (var entry in entries)
            {
                var item = entry.Item;
                if (!File.Exists(entry.Ready)) { Check(false, "窗格 " + item.Position + " 的 cwd 与参数与计划一致", "缺少就绪证据"); continue; }
                using var document = JsonDocument.Parse(File.ReadAllText(entry.Ready));
                var actualCwd = document.RootElement.GetProperty("Cwd").GetString();
                var actualArgs = document.RootElement.GetProperty("Args").EnumerateArray().Select(value => value.GetString()).ToArray();
                var expectedArgs = new[] { $"中文 & ; 参数{item.Position}" };
                var ok = actualCwd == item.Cwd && actualArgs is not null && actualArgs.SequenceEqual(expectedArgs);
                Check(ok, "窗格 " + item.Position + " 的 cwd 与参数与计划一致",
                    ok ? "cwd 与参数逐项一致" : "实际 cwd=" + actualCwd + "，参数=" + JsonSerializer.Serialize(actualArgs));
                File.Copy(entry.Ready, Path.Combine(evidenceDirectory, "child-" + item.Position + ".json"), overwrite: false);
                children.Add(new { item.Position, Pid = document.RootElement.GetProperty("Pid").GetInt32(), Cwd = actualCwd, Args = actualArgs });
            }
            Check(entries.All(entry => File.Exists(Path.Combine(Store.Root, "attempts", entry.Item.Id.ToString("N"), "claim"))),
                "全部窗格已被 Worker 领取", "");

            if (missing.Length == 0)
            {
                NativeWin.Window? found = null;
                var titleWatch = Stopwatch.StartNew();
                while (titleWatch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    var current = NativeWin.Windows().Where(window => window.Title.Contains(token)).ToList();
                    if (current.Count == 1) found = current[0];
                    if (current.Count == 1 && current[0].Title == expectedTitle) break;
                    await Task.Delay(100);
                }
                var finalWindows = NativeWin.Windows().Where(window => window.Title.Contains(token)).ToList();
                Check(finalWindows.Count == 1, "批次恰好 1 个可见 WindowsTerminal 顶层窗口",
                    finalWindows.Count == 1 ? "标题：" + finalWindows[0].Title : "实际 " + finalWindows.Count + " 个：" + string.Join(" | ", finalWindows.Select(window => window.Title)));
                if (found is { } window)
                {
                    batchWindow = window;
                    Check(window.Title == expectedTitle, "窗口标题等于创建序 0 窗格标题", "期望「" + expectedTitle + "」，实际「" + window.Title + "」");
                    Check(NativeWin.IsZoomed(window.Handle), "N>1 批次以 --maximized 最大化打开", NativeWin.IsZoomed(window.Handle) ? "窗口已最大化" : "窗口未处于最大化状态");
                }
                else Check(false, "窗口标题等于创建序 0 窗格标题", "未找到唯一批次窗口，无法核对标题");

                File.WriteAllText(release, "release");
                releaseWritten = true;
                var finishedWatch = Stopwatch.StartNew();
                while (finishedWatch.Elapsed < TimeSpan.FromSeconds(20)
                    && plans.Any(item => Store.Read<Receipt>(Store.ReceiptPath(item.Id))?.Phase != "Finished")) await Task.Delay(100);
                foreach (var item in plans)
                {
                    var receipt = Store.Read<Receipt>(Store.ReceiptPath(item.Id));
                    Check(receipt is { Phase: "Finished", ExitCode: 0 }, "窗格 " + item.Position + " 回执 Finished 且 ExitCode 0",
                        receipt is null ? "无回执" : receipt.Phase + "，ExitCode=" + receipt.ExitCode);
                }
                if (batchWindow is { } closeTarget)
                {
                    var closed = NativeWin.WaitClosed(closeTarget.Handle, TimeSpan.FromSeconds(10));
                    if (!closed) NativeWin.PostClose(closeTarget.Handle);
                    if (!closed) closed = NativeWin.WaitClosed(closeTarget.Handle, TimeSpan.FromSeconds(5));
                    Check(closed, "本批次 WindowsTerminal 窗口已关闭", closed ? "子进程全部退出后关闭" : "发送 WM_CLOSE 后仍未关闭");
                }
            }
        }
        catch (Exception error) { Check(false, "流程未完成", error.GetType().Name + "：" + error.Message); }
        finally
        {
            try { if (!releaseWritten) File.WriteAllText(release, "release"); } catch { }
            foreach (var item in plans)
            {
                if (File.Exists(Path.Combine(Store.Root, "attempts", item.Id.ToString("N"), "claim"))) continue;
                try { await Ipc.SendAsync(new Request("revoke", Guid.NewGuid(), Attempt: item.Id)); } catch { }
            }
            foreach (var window in NativeWin.Windows().Where(window => window.Title.Contains(token)))
            {
                try { NativeWin.PostClose(window.Handle); } catch { } // 只关本批次令牌命中的窗口，绝不触碰用户窗口。
            }
            var report = new { At = DateTimeOffset.UtcNow, PaneCount = paneCount, Layout = "vertical", Terminal = terminal, Root = Store.Root, BatchId = batch.ToString("N"), Passed = passed, Checks = checks, Children = children, Limitations = limitations };
            try
            {
                await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "result.json"),
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
            catch (Exception writeError) { Console.Error.WriteLine("result.json 写入失败：" + writeError.Message); }
            Console.WriteLine(passed ? "TERMINAL_LAYOUT_COMPLETE" : "TERMINAL_LAYOUT_FAILED");
        }
        return passed ? 0 : 1;
    }
}

internal static class NativeWin
{
    public sealed record Window(IntPtr Handle, uint ProcessId, string Title);
    private delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] internal static extern bool PostMessageW(IntPtr window, uint message, IntPtr wordParameter, IntPtr longParameter);
    private const uint OwnerWindow = 4; // GW_OWNER
    internal const uint CloseMessage = 0x0010; // WM_CLOSE

    internal static List<Window> Windows()
    {
        var found = new List<Window>();
        var names = new Dictionary<uint, string>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, OwnerWindow) != IntPtr.Zero) return true;
            if (GetWindowThreadProcessId(window, out var processId) == 0) return true;
            if (!names.TryGetValue(processId, out var name))
            {
                try { using var process = Process.GetProcessById((int)processId); name = process.ProcessName; }
                catch { name = ""; }
                names[processId] = name;
            }
            if (!name.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase)) return true;
            var text = new StringBuilder(512);
            _ = GetWindowTextW(window, text, text.Capacity);
            if (text.Length > 0) found.Add(new Window(window, processId, text.ToString()));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    internal static bool WaitClosed(IntPtr window, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (!IsWindow(window) || !IsWindowVisible(window)) return true;
            Thread.Sleep(100);
        }
        return !IsWindow(window) || !IsWindowVisible(window);
    }

    internal static void PostClose(IntPtr window) => PostMessageW(window, CloseMessage, IntPtr.Zero, IntPtr.Zero);
}
