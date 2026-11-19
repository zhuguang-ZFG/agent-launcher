using AgentLauncher.Core;

namespace AgentLauncher.Tests;

// 同步可靠性 R34–R43 纯函数回归（规格 2026-11-18 §4/§5/§6/§8）。
// 全部用 canned mutagen sync list --long 文本（形状取自 mutagen 0.18.1 实测），不依赖真实 mutagen/VPS。
internal static class SyncReliabilityChecks
{
    // 与真实输出同形：短横线分隔、段内缩进、Last error 在 Status 之前、Transition problems 在 Beta 段内。
    internal static string Session(
        string name, string mode, string status,
        string betaConnected = "Yes", string conflicts = "", string lastError = "", string betaExtra = "") =>
        "-------------------------------------------------------------------------------\n" +
        $"Name: {name}\n" +
        $"Identifier: sync_fake{name}\n" +
        "Configuration:\n" +
        $"\tSynchronization mode: {mode}\n" +
        "Alpha:\n" +
        "\tURL: D:\\fixture\\hub\n" +
        "\tConnected: Yes\n" +
        "Beta:\n" +
        "\tURL: ssh://agent@jy.example.com:22/home/agent/work\n" +
        $"\tConnected: {betaConnected}\n" +
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
            var noMode = SyncReliability.ParseSession("Name: agent-launcher-jy0001\nStatus: Watching for changes\n")!;
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
            var named = AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(output, "D:\\fixture\\hub", "jy.example.com", "/home/agent/work", "agent-launcher-jy0001");
            assert(named is { Name: "agent-launcher-jy0001" } && named.Value.Session.SyncMode == "two-way-safe", "命名匹配失败");
            var byEndpoint = AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(output, "D:\\fixture\\hub", "aly.example.com", "/home/agent/work");
            assert(byEndpoint is { Name: "agent-launcher-aly0002" }, "端点匹配失败");
            assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(output, "D:\\fixture\\hub", "jy.example.com", "/home/agent/work", "agent-launcher-missing") == null, "不存在的名字被匹配");
            assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession("", "D:\\fixture\\hub", "jy.example.com", "/home/agent/work") == null, "空 list 匹配到会话");
            assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(output, "", "jy.example.com", "/home/agent/work") == null, "缺本地路径未拒绝");
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