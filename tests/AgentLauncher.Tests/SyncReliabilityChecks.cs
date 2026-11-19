using System.Diagnostics;
using System.Text;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Tests;

// 同步可靠性 R34–R43 纯函数与探针边界回归（规格 2026-11-18 §4/§5/§6/§8）。
// 全部用 canned mutagen sync list --long 文本与本地进程夹具，不依赖真实 mutagen/VPS。
internal static class SyncReliabilityChecks
{
    // 与真实输出同形：短横线分隔、段内缩进、Last error 在 Status 之前、Transition problems 在 Beta 段内。
    internal static string Session(
        string name, string mode, string status,
        string? betaConnected = "Yes", string conflicts = "", string lastError = "", string betaExtra = "", string? alphaConnected = "Yes") =>
        "-------------------------------------------------------------------------------\n" +
        $"Name: {name}\n" +
        $"Identifier: sync_fake{name}\n" +
        "Configuration:\n" +
        $"\tSynchronization mode: {mode}\n" +
        "Alpha:\n" +
        "\tURL: D:\\fixture\\hub\n" +
        (alphaConnected is null ? "" : $"\tConnected: {alphaConnected}\n") +
        "Beta:\n" +
        "\tURL: ssh://agent@jy.example.com:22/home/agent/work\n" +
        (betaConnected is null ? "" : $"\tConnected: {betaConnected}\n") +
        betaExtra +
        conflicts +
        (lastError.Length > 0 ? $"Last error: {lastError}\n" : "") +
        $"Status: {status}\n" +
        "-------------------------------------------------------------------------------\n";

    internal static string ConflictLines(params string[] paths)
    {
        var text = "Conflicts:\n";
        foreach (var p in paths)
        {
            text += $"\t(alpha) {p} (<non-existent> -> File (c9b716f8ee2c8e505e4302af98227ad4cdcf8786))\n";
            text += $"\t(beta)  {p} (<non-existent> -> File (112df4c014e10ee40f1ffef20e4e0bf31cf53ded))\n";
        }
        return text;
    }

    // 测试 exe 复制为 mutagen.exe 后仅输出夹具；不会查找或启动真实 Mutagen。
    internal static async Task<int> WriteListFixtureAsync(string? name)
    {
        if (!AppContext.BaseDirectory.Contains("AgentLauncher-tests-probe-boundary-", StringComparison.Ordinal))
            throw new IOException("Probe fixture requires an isolated test directory.");
        var session = Session("agent-launcher-probe", "Two Way Safe", "Watching for changes");
        if (name == "oversized")
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "sync", "list", "--long", "overflow-child:" + Environment.ProcessId }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"child-{child.Id}.pid"), child.Id.ToString());
            if (await child.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) != "ready") return 3;
            return 0;
        }
        if (name?.StartsWith("overflow-child:", StringComparison.Ordinal) == true)
        {
            using var parent = Process.GetProcessById(int.Parse(name[15..]));
            Console.Error.WriteLine("ready");
            await parent.WaitForExitAsync();
            // 父进程先成功退出，再由继承 stdout 的子进程越界：稳定覆盖 Code=0 + Truncated。
            try { Console.Write(session + new string(' ', 512 * 1024)); }
            catch (IOException) { } // 捕获方关闭越界管道。
            return 0;
        }
        var output = name switch
        {
            null or "no-sessions" => "No sessions found\n",
            "no-sessions-separated" => "-------------------------------------------------------------------------------\r\nNo sessions found\r\n-------------------------------------------------------------------------------\r\n",
            "complete" or "exit-error" => session,
            "multiple" => session + Session("agent-launcher-second", "Two Way Safe", "[Paused]", alphaConnected: null, betaConnected: null),
            "no-separators" => session.Replace("-------------------------------------------------------------------------------\n", ""),
            "offline" => Session("agent-launcher-probe", "Two Way Safe", "Connecting to beta", betaConnected: "No"),
            "empty" => "",
            "whitespace" => " \t\r\n",
            "garbage" => "unrecognizable successful output\n",
            "name-only" => "Name: agent-launcher-probe\n",
            "missing-alpha" => session.Replace("\tURL: D:\\fixture\\hub\n", ""),
            "missing-beta" => session.Replace("\tURL: ssh://agent@jy.example.com:22/home/agent/work\n", ""),
            "missing-status" => session.Replace("Status: Watching for changes\n", ""),
            "mixed" => session + "unrecognizable trailing block\n",
            "false-no-sessions" => "warning: No sessions found (incomplete)\n",
            _ => throw new IOException("Unknown probe fixture: " + name),
        };
        Console.Write(output);
        if (name != "exit-error") return 0;
        Console.Error.WriteLine("fixture list failed");
        return 7;
    }

    private static void WithListFixture(Action action)
    {
        var root = Path.Combine(Path.GetTempPath(), "AgentLauncher-tests-probe-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cwd = Environment.CurrentDirectory;
        var path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            // apphost 仍加载原 Tests.dll；复制同目录依赖，无需生产注入点。
            File.Copy(Path.Combine(AppContext.BaseDirectory, "AgentLauncher.Tests.exe"), Path.Combine(root, "mutagen.exe"));
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)))
                File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
            Environment.CurrentDirectory = root;
            Environment.SetEnvironmentVariable("PATH", root);
            action();
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
            Environment.SetEnvironmentVariable("PATH", path);
            foreach (var file in Directory.EnumerateFiles(root, "child-*.pid"))
            {
                try
                {
                    using var child = Process.GetProcessById(int.Parse(File.ReadAllText(file)));
                    if (!child.WaitForExit(5000)) { child.Kill(true); child.WaitForExit(); }
                }
                catch (ArgumentException) { } // 已退出。
            }
            Directory.Delete(root, recursive: true);
        }
    }

    public static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        var now = new DateTimeOffset(2026, 11, 19, 14, 2, 11, TimeSpan.FromHours(8));
        var fresh = now.AddSeconds(-5);
        var stale = now.AddSeconds(-31);

        test("R34 mutagen long list parses mode urls status and two session blocks", () =>
        {
            var jy = Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes");
            var aliyun = Session("agent-launcher-aly0002", "Two Way Safe", "Watching for changes");
            var blocks = SyncReliability.SplitSessionBlocks(jy + aliyun);
            assert(blocks.Length == 2, "两条会话块切分错误");
            var parsed = SyncReliability.ParseSession(blocks[0])!;
            assert(parsed.Name == "agent-launcher-jy0001", "会话名解析错误");
            assert(parsed.SyncMode == "two-way-safe", "同步模式未归一化：" + parsed.SyncMode);
            assert(parsed.AlphaUrl == "D:\\fixture\\hub" && parsed.BetaUrl == "ssh://agent@jy.example.com:22/home/agent/work", "端点 URL 解析错误");
            assert(parsed.Status == "Watching for changes" && parsed.BetaConnected && !parsed.Paused, "状态/连接解析错误");
            assert(SyncReliability.ParseSession(blocks[1])!.Name == "agent-launcher-aly0002", "第二块解析错误");
            // 单会话无分隔线也能解析；无 Name 的垃圾输出 → null（R39 无法解析）。
            assert(SyncReliability.ParseSession(jy)!.Name == "agent-launcher-jy0001", "单块解析失败");
            assert(SyncReliability.ParseSession("garbage without fields") == null, "垃圾输出被解析成会话");
            assert(SyncReliability.ParseSession("") == null, "空输出被解析成会话");
        });

        test("R34 converged requires two-way-safe Watching fresh and both links for overall", () =>
        {
            var ok = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes"))!;
            var obs = SyncReliability.EvaluateLink(ok, fresh, now);
            assert(obs.State == SyncLinkState.Converged && obs.Label == "已收敛", "健康会话不是已收敛：" + obs.Detail);
            assert(obs.LastObservedAt == fresh, "上次观测时间丢失");
            assert(SyncReliability.OverallLabel(new[] { SyncLinkState.Converged, SyncLinkState.Converged }) == "全部已收敛", "双链路已收敛整体标签错误");
            // 单条 Watching 不构成全部已收敛（R34）；单链路至多单链路已收敛（§3）。
            assert(SyncReliability.OverallLabel(new[] { SyncLinkState.Converged }) == "单链路已收敛", "单链路被写成全部已收敛");
            assert(SyncReliability.OverallLabel(Array.Empty<SyncLinkState>()) == "未同步", "空链路整体标签错误");
            // 一条过期 → 未知 → 整体未知，绝不全部已收敛。
            var expired = SyncReliability.EvaluateLink(ok, stale, now);
            assert(expired.State == SyncLinkState.Unknown, "过期观测不是未知");
            assert(expired.Detail.Contains("已收敛") && expired.Detail.Contains("上次观测"), "过期详情未保留上次标签作上次观测");
            assert(SyncReliability.OverallLabel(new[] { SyncLinkState.Converged, expired.State }) == "未知", "过期链路没有把整体拉成未知");
        });

        foreach (var endpoint in new[] { "Alpha", "Beta" })
            foreach (var connected in new string?[] { null, "", "Maybe", "yes", "No (retrying)" })
                test($"R39 {endpoint} Connected {connected ?? "missing"} stays unknown", () =>
                {
                    foreach (var status in new[] { "Watching for changes", "Connecting to beta", "Scanning files" })
                    {
                        var parsed = SyncReliability.ParseSession(Session("agent-launcher-review", "Two Way Safe", status,
                            alphaConnected: endpoint == "Alpha" ? connected : "Yes",
                            betaConnected: endpoint == "Beta" ? connected : "Yes", lastError: "connection refused"))!;
                        var observation = SyncReliability.EvaluateLink(parsed, fresh, now);
                        assert(observation.State == SyncLinkState.Unknown, "缺失/无效 Connected 不是未知：" + observation.Detail);
                        assert(!parsed.ConnectionFailure, "没有明确 beta No 却确认了离线");
                    }
                });

        test("R39 both missing Connected fields stay unknown", () =>
        {
            var parsed = SyncReliability.ParseSession(Session("agent-launcher-review", "Two Way Safe", "Watching for changes",
                alphaConnected: null, betaConnected: null))!;
            assert(SyncReliability.EvaluateLink(parsed, fresh, now).State == SyncLinkState.Unknown, "两端连接证据缺失却已收敛");
        });

        test("R34 explicit alpha No prevents convergence and syncing", () =>
        {
            foreach (var status in new[] { "Watching for changes", "Connecting to alpha", "Scanning files" })
            {
                var parsed = SyncReliability.ParseSession(Session("agent-launcher-review", "Two Way Safe", status,
                    alphaConnected: "No", lastError: "unable to connect to alpha"))!;
                var observation = SyncReliability.EvaluateLink(parsed, fresh, now);
                assert(observation.State == SyncLinkState.NotConverged && observation.Detail.Contains("alpha"), "alpha No 未被识别为未收敛：" + observation.Detail);
            }
        });

        test("R34 incomplete connection evidence preserves pause conflict and offline priority", () =>
        {
            foreach (var (status, conflicts, betaConnected, expected) in new[]
            {
                ("[Paused]", "", "Maybe", SyncLinkState.SafetyPaused),
                ("Halted due to root deletion", ConflictLines("a.txt"), "No", SyncLinkState.SafetyPaused),
                ("Connecting to beta", ConflictLines("a.txt"), "No", SyncLinkState.Conflict),
                ("Connecting to beta", "Conflicts: unreadable\n", "No", SyncLinkState.Unknown),
                ("Connecting to beta", "", "No", SyncLinkState.Offline),
            })
            {
                var parsed = SyncReliability.ParseSession(Session("agent-launcher-review", "Two Way Safe", status,
                    alphaConnected: null, betaConnected: betaConnected, conflicts: conflicts,
                    betaExtra: "\tScan problems:\n\t\twork: permission denied\n"))!;
                assert(SyncReliability.EvaluateLink(parsed, fresh, now).State == expected, "连接证据或扫描问题覆盖了更高优先级状态：" + expected);
            }
        });

        foreach (var endpoint in new[] { "Alpha", "Beta" })
            test($"R34 {endpoint} scan problems prevent Watching convergence", () =>
            {
                foreach (var status in new[] { "Watching for changes", "Reconciling changes" })
                {
                    const string problems = "\tScan problems:\n\t\twork: permission denied\n";
                    var output = Session("agent-launcher-review", "Two Way Safe", status, betaExtra: endpoint == "Beta" ? problems : "");
                    if (endpoint == "Alpha") output = output.Replace("Beta:\n", problems + "Beta:\n");
                    var observation = SyncReliability.EvaluateLink(SyncReliability.ParseSession(output)!, fresh, now);
                    assert(observation.State == SyncLinkState.NotConverged && observation.Detail.Contains("Scan problems"), "扫描问题未阻止收敛：" + observation.Detail);
                }
            });

        test("R34 retained synchronization errors prevent Watching convergence", () =>
        {
            var parsed=SyncReliability.ParseSession(Session("agent-launcher-review","Two Way Safe","Watching for changes",lastError:"unable to save archive"))!;
            assert(SyncReliability.EvaluateLink(parsed,fresh,now).State==SyncLinkState.NotConverged,"Last error 被忽略并声称已收敛");
        });

        test("R34/R39 overall priority follows spec section 4 order", () =>
        {
            string Overall(params SyncLinkState[] s) => SyncReliability.OverallLabel(s);
            assert(Overall(SyncLinkState.SafetyPaused, SyncLinkState.Conflict) == "安全暂停", "优先级 1 错误");
            assert(Overall(SyncLinkState.Conflict, SyncLinkState.Unknown) == "冲突", "优先级 2 错误");
            assert(Overall(SyncLinkState.Unknown, SyncLinkState.Offline) == "未知", "优先级 3 错误");
            assert(Overall(SyncLinkState.Offline, SyncLinkState.NotConverged) == "离线", "优先级 4 错误");
            assert(Overall(SyncLinkState.NotConverged, SyncLinkState.Syncing) == "未收敛", "优先级 5 错误");
            assert(Overall(SyncLinkState.Syncing, SyncLinkState.Converged) == "同步中", "优先级 6 错误");
            assert(Overall(SyncLinkState.Converged, SyncLinkState.Converged) == "全部已收敛", "优先级 7 错误");
        });

        test("R39 list probe rejects a truncated successful capture with a converged prefix", () => WithListFixture(() =>
        {
            var raw = ReviewProcess.CaptureAsync("mutagen", Environment.CurrentDirectory, ["sync", "list", "--long", "oversized"], default, 10, 512 * 1024).GetAwaiter().GetResult();
            assert(raw.Code == 0 && raw.Truncated && !raw.TimedOut, $"夹具未覆盖成功截断：Code={raw.Code}, Truncated={raw.Truncated}, TimedOut={raw.TimedOut}");
            var prefix = WorkspaceInsights.FindSyncSession(Encoding.UTF8.GetString(raw.Output), @"D:\fixture\hub", "jy.example.com", "/home/agent/work", "agent", 22);
            assert(prefix is not null && SyncReliability.EvaluateLink(prefix.Value.Session, fresh, now).State == SyncLinkState.Converged, "夹具前缀没有复现可能的错误收敛");
            var result = WorkspaceInsights.ListMutagenSessionsAsync(default, "oversized").GetAwaiter().GetResult();
            assert(!result.Ok, "截断但成功退出的 list 被当成成功观测");
            assert(result.Output.Length == 0 && result.Error.Contains("截断"), "截断探针泄漏前缀或缺少可读原因");
        }));

        foreach (var fixture in new[] { "empty", "whitespace", "garbage", "name-only", "missing-alpha", "missing-beta", "missing-status", "mixed", "false-no-sessions" })
            test("R39 list probe rejects successful " + fixture, () => WithListFixture(() =>
            {
                var result = WorkspaceInsights.ListMutagenSessionsAsync(default, fixture).GetAwaiter().GetResult();
                assert(!result.Ok, "空/无法识别的 list 被当成成功观测：" + fixture);
                assert(result.Output.Length == 0 && result.Error.Length > 0, "不可信输出泄漏或未给出错误原因");
            }));

        foreach (var fixture in new string?[] { null, "no-sessions", "no-sessions-separated", "complete", "multiple", "no-separators", "offline" })
            test("R39 list probe accepts complete " + (fixture ?? "unnamed empty list"), () => WithListFixture(() =>
            {
                var result = WorkspaceInsights.ListMutagenSessionsAsync(default, fixture).GetAwaiter().GetResult();
                assert(result.Ok && result.Error.Length == 0, "合法 list 输出被拒绝：" + result.Error);
                assert(result.Output.Contains(fixture is null || fixture.StartsWith("no-sessions", StringComparison.Ordinal) ? "No sessions found" : "Name: agent-launcher-probe"), "合法输出丢失");
                if (fixture == "complete")
                {
                    var matched = WorkspaceInsights.FindSyncSession(result.Output, @"D:\fixture\hub", "jy.example.com", "/home/agent/work", "agent", 22);
                    assert(matched is not null && SyncReliability.EvaluateLink(matched.Value.Session, fresh, now).State == SyncLinkState.Converged, "完整健康会话不再收敛");
                }
            }));

        test("R39 list probe still rejects nonzero exit with complete output", () => WithListFixture(() =>
        {
            var result = WorkspaceInsights.ListMutagenSessionsAsync(default, "exit-error").GetAwaiter().GetResult();
            assert(!result.Ok && result.Error.Contains("fixture list failed"), "退出失败被完整输出覆盖");
        }));

        test("R39 list probe preserves cancellation", () => WithListFixture(() =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { WorkspaceInsights.ListMutagenSessionsAsync(cancellation.Token, "complete").GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return; }
            assert(false, "探针吞掉了取消");
        }));

        test("R39 probe failure and staleness are unknown and keep last observation", () =>
        {
            foreach (var error in new[] { "mutagen sync list 超时（10 秒），已终止；同步状态未知。", "mutagen sync list 失败（会话可能不存在）：exit 1", "未找到 mutagen 或无法运行：命令不存在", "输出为空" })
            {
                var obs = SyncReliability.FailedProbe(error, fresh, SyncLinkState.Converged, now);
                assert(obs.State == SyncLinkState.Unknown && obs.Label == "未知", "探针失败不是未知：" + error);
                assert(obs.LastObservedAt == fresh, "探针失败丢掉了上次观测时间");
                assert(obs.Detail.Contains("14:02:06") || obs.Detail.Contains("上次观测"), "详情未给出上次观测");
                assert(!obs.Detail.Contains("已收敛") || obs.Detail.Contains("上次状态"), "未知被写成当前已收敛");
            }
            var never = SyncReliability.FailedProbe("超时", null, null, now);
            assert(never.State == SyncLinkState.Unknown && never.LastObservedAt == null && never.Detail.Contains("尚无成功观测"), "无历史探针失败详情错误");
            // 恰好 30s 仍新鲜，31s 过期（阈值取自规格 §4）。
            var boundary = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes"))!;
            assert(SyncReliability.EvaluateLink(boundary, now.AddSeconds(-30), now).State == SyncLinkState.Converged, "恰好 30 秒被误判过期");
            assert(SyncReliability.EvaluateLink(boundary, now.AddSeconds(-31), now).State == SyncLinkState.Unknown, "31 秒未判过期");
        });

        test("R40 safety halt reasons and operator pause are safety-paused never converged", () =>
        {
            foreach (var (status, reason) in new[]
            {
                ("Halted due to root deletion", "同步根被删除"),
                ("Halted due to one-sided root emptying", "清空"),
                ("Halted due to root type change", "类型"),
                ("Halted due to something unclassified", "安全暂停"),
                ("[Paused]", "操作员暂停"),
            })
            {
                var parsed = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", status))!;
                var obs = SyncReliability.EvaluateLink(parsed, fresh, now);
                assert(obs.State == SyncLinkState.SafetyPaused && obs.Label == "安全暂停", $"{status} 不是安全暂停");
                assert(obs.Detail.Contains(reason), $"{status} 详情未写明原因分类：{obs.Detail}");
            }
            // 安全暂停优先于冲突与离线。
            var haltedWithConflict = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Halted due to root deletion", conflicts: ConflictLines("a.txt")))!;
            assert(SyncReliability.EvaluateLink(haltedWithConflict, fresh, now).State == SyncLinkState.SafetyPaused, "安全暂停没有优先于冲突");
        });

        test("R36 conflict paths are parsed paired filtered and capped at 50 with remainder", () =>
        {
            var parsed = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes", conflicts: ConflictLines("docs/plan.md", "src/a.txt")))!;
            assert(parsed.SafeConflictPaths.SequenceEqual(new[] { "docs/plan.md", "src/a.txt" }), "冲突路径解析/去重错误：" + string.Join(",", parsed.SafeConflictPaths));
            var obs = SyncReliability.EvaluateLink(parsed, fresh, now);
            assert(obs.State == SyncLinkState.Conflict && obs.Label == "冲突", "有安全冲突行不是冲突");
            var list = SyncReliability.BuildConflictList(new[] { (parsed, "本机—京云", "本机", "京云") });
            assert(list.Rows.Length == 2 && list.Rows[0].RelativePath == "docs/plan.md" && list.Rows[0].LinkLabel == "本机—京云" && list.Rows[0].AlphaRole == "本机" && list.Rows[0].BetaRole == "京云", "冲突行字段错误");
            assert(list.RemainingCount == 0 && list.DiscardedCount == 0, "无截断时剩余计数错误");
            // 60 条 → 50 行 + 剩余 10；整体仍是冲突。
            var many = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes",
                conflicts: ConflictLines(Enumerable.Range(1, 60).Select(i => $"f{i:D3}.txt").ToArray())))!;
            var capped = SyncReliability.BuildConflictList(new[] { (many, "本机—京云", "本机", "京云") });
            assert(capped.Rows.Length == 50 && capped.RemainingCount == 10, "50 行截断或剩余计数错误");
            assert(SyncReliability.EvaluateLink(many, fresh, now).State == SyncLinkState.Conflict, "截断后不再是冲突");
            // mutagen 排除行 ...+7 more... 计入剩余。
            var excluded = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes",
                conflicts: ConflictLines("a.txt") + "\t...+7 more...\n"))!;
            assert(excluded.ExcludedConflicts == 7, "排除冲突计数解析错误");
            var excludedList = SyncReliability.BuildConflictList(new[] { (excluded, "本机—京云", "本机", "京云") });
            assert(excludedList.Rows.Length == 1 && excludedList.RemainingCount == 7, "排除冲突未计入剩余");
            assert(SyncReliability.EvaluateLink(excluded, fresh, now).State == SyncLinkState.Conflict, "有安全行+排除行不是冲突");
        });

        test("R36 unsafe conflict paths are dropped and all-unsafe means unknown", () =>
        {
            foreach (var bad in new[] { "", "../escape.txt", "a/../../b.txt", "/etc/passwd", "\\abs\\x.txt", "C:\\windows\\evil.txt", "c:/x.txt", "\\\\server\\share\\x.txt", "<root>" })
                assert(!SyncReliability.IsSafeRelativePath(bad), "不安全路径被接受：" + bad);
            foreach (var good in new[] { "a.txt", "docs/plan.md", "中文 文件.txt", "a/b/../c.txt".Replace("/../", "/") })
                assert(SyncReliability.IsSafeRelativePath(good), "安全路径被拒绝：" + good);
            assert(!SyncReliability.IsSafeRelativePath("a/../b.txt"), ".. 段未被拒绝");
            // 混合：保留安全行，状态冲突，详情注明无法展示的条目。
            var mixed = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes",
                conflicts: ConflictLines("ok.txt", "../escape.txt", "C:\\evil.txt")))!;
            assert(mixed.SafeConflictPaths.SequenceEqual(new[] { "ok.txt" }) && mixed.DiscardedConflictEntries == 2, "混合冲突过滤错误");
            var mixedObs = SyncReliability.EvaluateLink(mixed, fresh, now);
            assert(mixedObs.State == SyncLinkState.Conflict && mixedObs.Detail.Contains("无法展示"), "混合冲突状态/详情错误");
            // 全不安全：一条安全行都没有 → 未知（§4/§5）。
            var allBad = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes",
                conflicts: ConflictLines("../escape.txt", "/etc/passwd")))!;
            assert(allBad.SafeConflictPaths.Length == 0 && allBad.DiscardedConflictEntries == 2, "全不安全冲突计数错误");
            assert(SyncReliability.EvaluateLink(allBad, fresh, now).State == SyncLinkState.Unknown, "全不安全冲突不是未知");
            // 默认 list 的计数形 Conflicts: 3 无法展示路径 → 未知（fail-closed）。
            var countOnly = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes", conflicts: "Conflicts: 3\n"))!;
            assert(countOnly.ConflictSectionPresent && countOnly.SafeConflictPaths.Length == 0, "计数形冲突解析错误");
            assert(SyncReliability.EvaluateLink(countOnly, fresh, now).State == SyncLinkState.Unknown, "计数形冲突不是未知");
        });

        test("R38 one offline link blocks overall convergence and healthy link stays converged", () =>
        {
            var offline = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Connecting to beta",
                betaConnected: "No",
                lastError: "unable to connect to beta: unable to connect to endpoint: unable to dial agent endpoint: dial tcp 203.0.113.7:22: connectex: No connection could be made because the target machine actively refused it."))!;
            assert(offline.ConnectionFailure, "连接失败未被识别");
            var offlineObs = SyncReliability.EvaluateLink(offline, fresh, now);
            assert(offlineObs.State == SyncLinkState.Offline && offlineObs.Label == "离线", "连接失败不是离线");
            var healthy = SyncReliability.ParseSession(Session("agent-launcher-aly0002", "Two Way Safe", "Watching for changes"))!;
            assert(SyncReliability.EvaluateLink(healthy, fresh, now).State == SyncLinkState.Converged, "健康链路不是已收敛");
            assert(SyncReliability.OverallLabel(new[] { offlineObs.State, SyncLinkState.Converged }) == "离线", "一离线一健康整体不是离线");
            assert(SyncReliability.OverallLabel(new[] { offlineObs.State, SyncLinkState.Converged }) != "全部已收敛", "离线被写成全部已收敛");
            // Connected: No 但状态 Watching 且无连接错误文本 → 不能确认连接失败 → 未收敛（§4 离线需确认）。
            var ambiguous = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes", betaConnected: "No"))!;
            assert(!ambiguous.ConnectionFailure && SyncReliability.EvaluateLink(ambiguous, fresh, now).State == SyncLinkState.NotConverged, "含糊连接状态被误判离线/已收敛");
        });

        test("R34 two-way-resolved caps at not-converged and syncing states are recognized", () =>
        {
            var resolved = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Resolved", "Watching for changes"))!;
            assert(resolved.SyncMode == "two-way-resolved", "resolved 模式归一化错误");
            var resolvedObs = SyncReliability.EvaluateLink(resolved, fresh, now);
            assert(resolvedObs.State == SyncLinkState.NotConverged && resolvedObs.Detail.Contains("alpha"), "resolved 会话进入已收敛或详情未说明 alpha 赢");
            var resolvedSyncing = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Resolved", "Reconciling changes"))!;
            assert(SyncReliability.EvaluateLink(resolvedSyncing, fresh, now).State == SyncLinkState.NotConverged, "resolved 会话超过未收敛上限");
            foreach (var status in new[] { "Scanning files", "Reconciling changes", "Staging files on alpha", "Staging files on beta", "Applying changes", "Waiting 5 seconds for rescan", "Saving archive" })
            {
                var parsed = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", status))!;
                assert(SyncReliability.EvaluateLink(parsed, fresh, now).State == SyncLinkState.Syncing, $"{status} 不是同步中");
            }
            // Transition problems → 未收敛；模式未显示 → 不能确认 two-way-safe → 未收敛（fail-closed）。
            var problems = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes", betaExtra: "\tTransition problems:\n\t\twork: permission denied\n"))!;
            assert(problems.TransitionProblems && SyncReliability.EvaluateLink(problems, fresh, now).State == SyncLinkState.NotConverged, "Transition problems 不是未收敛");
            var noMode = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "", "Watching for changes").Replace("\tSynchronization mode: \n", ""))!;
            assert(noMode.SyncMode == "" && SyncReliability.EvaluateLink(noMode, fresh, now).State == SyncLinkState.NotConverged, "未显示模式被当成 two-way-safe");
            // 无法确认为同步中的非 Watching（如 Disconnected 且无连接失败证据）→ 未收敛。
            var disconnected = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Disconnected"))!;
            assert(SyncReliability.EvaluateLink(disconnected, fresh, now).State is SyncLinkState.NotConverged or SyncLinkState.Offline, "Disconnected 被当成已收敛");
        });

        test("R41 None and SSHFS badges never claim convergence", () =>
        {
            var (noneLabel, noneDetail) = SyncReliability.NonMutagenSyncBadge(WorkspaceSyncMode.None, false, false);
            assert(noneLabel == "未同步", "None 标签错误");
            assert(noneDetail.Contains("不是同权") && noneDetail.Contains("拒收"), "None 详情缺少不是同权/拒收文案：" + noneDetail);
            var (mounted, mountDetail) = SyncReliability.NonMutagenSyncBadge(WorkspaceSyncMode.SshfsMount, true, false);
            assert(mounted == "已挂载" && mountDetail.Contains("不是已收敛"), "SSHFS 成功文案错误");
            var (failed, failDetail) = SyncReliability.NonMutagenSyncBadge(WorkspaceSyncMode.SshfsMount, false, true);
            assert(failed == "挂载失败" && failDetail.Contains("不是已收敛"), "SSHFS 失败文案错误");
            var (idle, _) = SyncReliability.NonMutagenSyncBadge(WorkspaceSyncMode.SshfsMount, false, false);
            assert(idle == "未挂载", "SSHFS 无尝试文案错误");
            // 两者映射进整体时都不能产生全部已收敛。
            assert(SyncReliability.OverallLabel(new[] { SyncLinkState.Converged, SyncLinkState.NotConverged }) == "未收敛", "None/SSHFS 链路未阻止全部已收敛");
        });

        test("R43 resource boundaries unchanged and single link never means all converged", () =>
        {
            assert(ResourcePolicy.MaxConcurrentAgents == 8, "8 窗格名额被改动");
            assert(ResourcePolicy.MaxSlots == 16, "16 保存槽位被改动");
            var remote = new ControlEntry { Plan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "r", "D:\\fixture", new("D:\\fixture", "k"), false, new("pi", [], "remote"), 0, RemoteExecution: new RemoteExecutionSpec("jy.example.com", 22, "root", "agent", "pi", "/home/agent/work", [], null, null)), State = "Disconnected" };
            assert(!ControlEntryStates.IsTerminal(remote), "远程 Disconnected 被当成终态（会腾出名额）");
            assert(SyncReliability.OverallLabel(new[] { SyncLinkState.Converged }) != "全部已收敛", "单链路被写成全部已收敛");
        });

        test("R34 error text sanitization keeps session name and strips address user key path", () =>
        {
            var raw = "会话 agent-launcher-jy0001 就绪超时：unable to connect to beta: ssh://agent@jy.example.com:22/home/agent/work dial tcp 203.0.113.7:22: connectex refused; key C:\\Users\\operator\\.ssh\\id_ed25519 加载失败; user@host 认证失败; UNC \\\\nas\\share\\key.pem 不可达";
            var clean = SyncReliability.SanitizeErrorText(raw);
            assert(clean.Contains("agent-launcher-jy0001"), "会话名被脱敏掉");
            assert(!clean.Contains("203.0.113.7"), "IP 未脱敏");
            assert(!clean.Contains("jy.example.com"), "主机地址未脱敏");
            assert(!clean.Contains("agent@"), "用户名未脱敏");
            assert(!clean.Contains("id_ed25519") && !clean.Contains("C:\\Users"), "密钥路径未脱敏");
            assert(!clean.Contains("\\\\nas"), "UNC 未脱敏");
            assert(!clean.Contains("/home/agent"), "远程路径未脱敏");
            assert(SyncReliability.SanitizeErrorText("") == "", "空文本脱敏崩溃");
        });

        test("R34 bracketed IPv6 SSH errors redact address and zone", () =>
        {
            foreach (var address in new[] { "2001:db8::7", "fe80::7%12", "fe80::7%Ethernet-2" })
            {
                var clean = SyncReliability.SanitizeErrorText($"agent-launcher-review mutagen 0.18.1: ssh: connect to host [{address}] port 22: Connection refused");
                assert(!clean.Contains(address) && !clean.Contains("%"), "IPv6 SSH 地址或区域标识未脱敏：" + clean);
                assert(clean.Contains("agent-launcher-review") && clean.Contains("0.18.1") && clean.Contains("Connection refused"), "会话名、版本号或错误原因丢失");
            }
        });

        test("R34 bare IPv6 connection errors redact full address and zone", () =>
        {
            foreach (var address in new[] { "2001:db8::7", "2001:0DB8:0000:0000:0000:0000:0000:0007", "::1", "2001:db8::", "::", "::ffff:203.0.113.7", "fe80::7%12", "fe80::7%eth0.2" })
                foreach (var suffix in new[] { "", ":", "." })
                {
                    var clean = SyncReliability.SanitizeErrorText($"agent-launcher-review mutagen 0.18.1: unable to connect to {address}{suffix} network is unreachable");
                    assert(!clean.Contains(address) && !clean.Contains("%") && !clean.Contains("203.0.113.7"), "裸 IPv6 地址或区域标识未脱敏：" + clean);
                    assert(clean.Contains("agent-launcher-review") && clean.Contains("0.18.1") && clean.Contains("network is unreachable"), "会话名、版本号或错误原因丢失");
                }
        });

        test("R34 IPv6 SSH endpoint redaction removes username and home path", () =>
        {
            foreach (var endpoint in new[]
            {
                "private-user@[2001:db8::7]:22:~/private-work",
                "private-user@[fe80::7%eth0]:2222:~/private-work/subdir",
                "private-user@[2001:db8::7]:/private-work",
                "[2001:db8::7]:22:~/private-work",
                "ssh://private-user@[fe80::7%eth0]:22/~/private-work",
            })
            {
                var clean = SyncReliability.SanitizeErrorText($"agent-launcher-review mutagen 0.18.1: endpoint {endpoint} failed");
                assert(!clean.Contains("private-user") && !clean.Contains("private-work") && !clean.Contains("~/"), "IPv6 端点用户名或路径未脱敏：" + clean);
                assert(!clean.Contains("2001:db8") && !clean.Contains("fe80") && !clean.Contains("eth0"), "IPv6 端点地址未脱敏：" + clean);
                assert(clean.Contains("agent-launcher-review") && clean.Contains("0.18.1") && clean.Contains("failed"), "会话名、版本号或错误原因丢失");
            }
        });

        test("R34 IPv6 redaction preserves non-address colons and session identity", () =>
        {
            const string text = "agent-launcher-dead-beef mutagen 0.18.1: failed at 14:02:11; code dead:beef; [status]";
            assert(SyncReliability.SanitizeErrorText(text) == text, "非地址文本被误删");
        });

        test("R42 create arguments use two-way-safe and keep the exact ignore list", () =>
        {
            var spec = new WorkspaceSyncSpec(WorkspaceSyncMode.Mutagen, "D:\\fixture\\hub", "/home/agent/work", null, null);
            var remote = new RemoteExecutionSpec("jy.example.com", 22, "root", "agent", "pi", "/home/agent/work", [], null, null, spec);
            var args = AgentLauncher.Windows.WorkspaceSync.BuildCreateArguments(spec, remote, "agent-launcher-test0001");
            var modeIndex = Array.IndexOf(args, "--sync-mode");
            assert(modeIndex >= 0 && args[modeIndex + 1] == "two-way-safe", "新建会话不是 two-way-safe");
            assert(!args.Contains("two-way-resolved"), "创建参数仍含 two-way-resolved");
            // 忽略列表不扩大：--ignore-vcs + 恰好 node_modules/ bin/ obj/ 三个 --ignore。
            var ignores = args.Where((a, i) => i > 0 && args[i - 1] == "--ignore").ToArray();
            assert(args.Contains("--ignore-vcs") && ignores.SequenceEqual(new[] { "node_modules/", "bin/", "obj/" }), "忽略列表被扩大或缩小：" + string.Join(",", ignores));
            // 本地路径在前（alpha=本机），远程端点在后，名字精确。
            assert(args[^2] == "D:\\fixture\\hub" && args[^1] == "agent@jy.example.com:22:/home/agent/work", "端点顺序或形状错误：" + args[^1]);
            assert(args[0] == "sync" && args[1] == "create" && args[Array.IndexOf(args, "--name") + 1] == "agent-launcher-test0001", "create 命令形状错误");
        });

        test("R43 probe is a single read-only long list without flush reset or ssh", () =>
        {
            var all = AgentLauncher.Windows.WorkspaceInsights.BuildListArguments(null);
            assert(all.SequenceEqual(new[] { "sync", "list", "--long" }), "全量探针参数错误：" + string.Join(" ", all));
            var named = AgentLauncher.Windows.WorkspaceInsights.BuildListArguments("agent-launcher-test0001");
            assert(named.SequenceEqual(new[] { "sync", "list", "--long", "agent-launcher-test0001" }), "命名探针参数错误");
            foreach (var args in new[] { all, named })
                assert(!args.Any(a => a is "flush" or "reset" or "terminate" or "create" or "pause" or "resume"), "诊断参数混入写操作");
        });

        test("R42 sync session interface exposes no unconfirmed reset pause resume", () =>
        {
            var methods = typeof(AgentLauncher.Windows.IWorkspaceSyncSession).GetMethods().Select(m => m.Name).ToArray();
            assert(!methods.Any(n => n is "Reset" or "Pause" or "Resume"), "会话接口暴露了未确认的 reset/pause/resume：" + string.Join(",", methods));
        });

        test("R34 FindSyncSession matches by name then endpoints and parses mode", () =>
        {
            var jy = Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes");
            var aliyun = Session("agent-launcher-aly0002", "Two Way Safe", "Watching for changes").Replace("jy.example.com", "aly.example.com");
            var output = jy + aliyun;
            var named = AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(output, "D:\\fixture\\hub", "jy.example.com", "/home/agent/work", "agent", 22, "agent-launcher-jy0001");
            assert(named is { Name: "agent-launcher-jy0001" } && named.Value.Session.SyncMode == "two-way-safe", "命名匹配失败");
            var byEndpoint = AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(output, "D:\\fixture\\hub", "aly.example.com", "/home/agent/work", "agent", 22);
            assert(byEndpoint is { Name: "agent-launcher-aly0002" }, "端点匹配失败");
            assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(output, "D:\\fixture\\hub", "jy.example.com", "/home/agent/work", "agent", 22, "agent-launcher-missing") == null, "不存在的名字被匹配");
            assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession("", "D:\\fixture\\hub", "jy.example.com", "/home/agent/work", "agent", 22) == null, "空 list 匹配到会话");
            assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(output, "", "jy.example.com", "/home/agent/work", "agent", 22) == null, "缺本地路径未拒绝");
        });

        foreach (var conflictText in new[] { "Conflicts: unreadable\n", "Conflicts:\n", "Conflicts: -1\n" })
            test("R39 malformed conflict section stays unknown: " + conflictText.TrimEnd(), () =>
            {
                var parsed = SyncReliability.ParseSession(Session("agent-launcher-review", "Two Way Safe", "Watching for changes", conflicts: conflictText))!;
                assert(SyncReliability.EvaluateLink(parsed, fresh, now).State == SyncLinkState.Unknown, "无法解析的冲突被显示为已收敛");
            });

        test("R36 malformed conflict header preserves safe rows as conflict", () =>
        {
            var conflicts = ConflictLines("ok.txt").Replace("Conflicts:\n", "Conflicts: unreadable\n");
            var parsed = SyncReliability.ParseSession(Session("agent-launcher-review", "Two Way Safe", "Watching for changes", conflicts: conflicts))!;
            assert(SyncReliability.EvaluateLink(parsed, fresh, now).State == SyncLinkState.Conflict, "安全冲突行被丢弃");
            assert(parsed.SafeConflictPaths.SequenceEqual(new[] { "ok.txt" }) && parsed.DiscardedConflictEntries > 0, "冲突路径或无法展示提示丢失");
        });

        test("R34 DNS error sanitization keeps version and session identity", () =>
        {
            foreach (var (raw, host) in new[]
            {
                ("could not resolve hostname node.example.test: name not known", "node.example.test"),
                ("dial tcp: lookup api.example.test on 203.0.113.7:53: no such host", "api.example.test"),
                ("could not resolve hostname private-node: name not known", "private-node"),
                ("failed connecting to API.Example.TEST.:22", "API.Example.TEST"),
            })
            {
                var clean = SyncReliability.SanitizeErrorText("agent-launcher-review mutagen 0.18.1: " + raw);
                assert(!clean.Contains(host, StringComparison.OrdinalIgnoreCase), "DNS 主机名未脱敏：" + clean);
                assert(clean.Contains("agent-launcher-review") && clean.Contains("0.18.1"), "会话名或版本号被误删");
            }
        });

        test("R34 session lookup rejects endpoint prefix collisions and ambiguous matches", () =>
        {
            var good = Session("agent-launcher-review", "Two Way Safe", "Watching for changes");
            foreach (var unrelated in new[]
            {
                good.Replace(@"D:\fixture\hub", @"D:\fixture\hub-backup"),
                good.Replace("jy.example.com", "not-jy.example.com"),
                good.Replace("/home/agent/work", "/home/agent/work-backup"),
            })
            {
                assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(unrelated, @"D:\fixture\hub", "jy.example.com", "/home/agent/work", "agent", 22) is null, "不相关端点被匹配");
                assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(unrelated, @"D:\fixture\hub", "jy.example.com", "/home/agent/work", "agent", 22, "agent-launcher-review") is null, "命名匹配忽略了端点不符");
            }
            var duplicate = good + good.Replace("agent-launcher-review", "agent-launcher-second");
            assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(duplicate, @"D:\fixture\hub", "jy.example.com", "/home/agent/work", "agent", 22) is null, "含糊会话被当成唯一匹配");
            assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(duplicate, @"D:\fixture\hub", "jy.example.com", "/home/agent/work", "agent", 22, "agent-launcher-review")?.Name == "agent-launcher-review", "明确会话名无法消除歧义");
            assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(good, @"d:\fixture\hub\", "JY.EXAMPLE.COM", "/home/agent/work/", "agent", 22) is not null, "规范化后的相同端点被拒绝");
            assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(good, @"D:\fixture\hub", "jy.example.com", "~/work", "agent", 22) is null, "仅凭目录后缀猜测了远程 home");
        });

        test("R34 session lookup rejects a different SSH identity", () =>
        {
            var fixture = Session("agent-launcher-review", "Two Way Safe", "Watching for changes");
            foreach (var endpoint in new[] { "other@jy.example.com:22:/home/agent/work", "agent@jy.example.com:2222:/home/agent/work", "ssh://other@jy.example.com:2222/home/agent/work" })
            {
                var output = fixture.Replace("ssh://agent@jy.example.com:22/home/agent/work", endpoint);
                assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(output, @"D:\fixture\hub", "jy.example.com", "/home/agent/work", "agent", 22, "agent-launcher-review") is null, "不同 SSH 身份仍匹配：" + endpoint);
            }
        });

        test("R34 session lookup supports canonical Mutagen SSH endpoint output", () =>
        {
            var fixture = Session("agent-launcher-review", "Two Way Safe", "Watching for changes");
            foreach (var endpoint in new[] { "agent@jy.example.com:22:/home/agent/work", "agent@jy.example.com:/home/agent/work", "jy.example.com:/home/agent/work" })
            {
                var output = fixture.Replace("ssh://agent@jy.example.com:22/home/agent/work", endpoint);
                var expectedUser = endpoint.Contains('@') ? "agent" : "";
                assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(output, @"D:\fixture\hub", "jy.example.com", "/home/agent/work", expectedUser, 22) is not null, "Mutagen 原生端点格式无法匹配：" + endpoint);
                if(expectedUser.Length==0)assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(output, @"D:\fixture\hub", "jy.example.com", "/home/agent/work", "agent", 22) is null, "未声明用户的端点被当成指定用户");
            }
            var relative = fixture.Replace("ssh://agent@jy.example.com:22/home/agent/work", "agent@jy.example.com:22:~/work");
            assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(relative, @"D:\fixture\hub", "jy.example.com", "~/work", "agent", 22) is not null, "同一个 home 相对路径被拒绝");
            assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(relative, @"D:\fixture\hub", "jy.example.com", "/home/other/work", "agent", 22) is null, "凭目录尾段匹配了其他用户的目录");
        });

        test("R41 None save and launch are rejected with readable equal-rights copy", () =>
        {
            // 保存侧共享文案（EditHost 使用同一函数；偏差：规格 §6 的「None 继续允许」已被 IMPLEMENTATION §92 拒收决定取代）。
            var error = RemoteHostEditor.ValidateSyncMode(WorkspaceSyncMode.None);
            assert(error is not null && error.Contains("同权") && error.Contains("mutagen") && error.Contains("sshfs"), "None 保存拒收文案缺少同权与改法：" + error);
            assert(RemoteHostEditor.ValidateSyncMode(WorkspaceSyncMode.Mutagen) == null && RemoteHostEditor.ValidateSyncMode(WorkspaceSyncMode.SshfsMount) == null, "合法模式被拒收");
            // 启动侧：WorkspacePreparer 对 None 主机拒收（与 RemoteExecutionChecks 的 F09/R23 断言同源，R41 独立复证）。
            var temp = Path.Combine(Path.GetTempPath(), "AgentLauncher-tests-r41-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var host = new RemoteHost(Guid.NewGuid(), "R41 VPS", "r41.example.com", 22, "root", "agent", ["pi"], null, WorkspaceSyncMode.None);
                var configuration = new Configuration { SchemaVersion = 2, RemoteHosts = [host] };
                var draft = new Draft
                {
                    SchemaVersion = 2,
                    Project = temp,
                    DirectoryMode = "isolated",
                    Slots = [new Slot { Enabled = true, AgentId = "pi", RemoteHostId = host.Id, RemoteDirectory = "/home/agent/work" }]
                };
                var preparer = new AgentLauncher.Windows.WorkspacePreparer(Path.Combine(temp, "worktrees"));
                var rejected = false;
                try { preparer.PrepareAsync(draft, _ => { }, _ => Task.FromResult(false), default, configuration: configuration).GetAwaiter().GetResult(); }
                catch (IOException e) { rejected = e.Message.Contains("同权"); }
                assert(rejected, "None 主机启动未被拒收或错误缺少同权说明");
            }
            finally { Directory.Delete(temp, recursive: true); }
        });
    }
}