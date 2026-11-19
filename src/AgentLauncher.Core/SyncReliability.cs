using System.Text.RegularExpressions;

namespace AgentLauncher.Core;

// 同步可靠性（规格 2026-11-18 §4/§5/§6）：每链路七态、30s 新鲜度、整体标签优先级、冲突清单过滤与错误脱敏。
// 纯函数、无 IO：输入是 mutagen sync list --long 的文本与观测时间，输出是可显示的状态与清单。
public enum SyncLinkState { SafetyPaused, Conflict, Unknown, Offline, NotConverged, Syncing, Converged }

/// <summary>只读冲突行（§5）：相对路径 + 受影响会话 + 两端角色。不含内容、哈希、URL、地址、端口。</summary>
public sealed record SyncConflictRow(string RelativePath, string LinkLabel, string AlphaRole, string BetaRole);

/// <summary>冲突清单：最多 50 行；RemainingCount 含被截断的安全行与 mutagen 排除行；DiscardedCount 是无法展示的不安全条目。</summary>
public sealed record SyncConflictList(SyncConflictRow[] Rows, int RemainingCount, int DiscardedCount);

/// <summary>mutagen sync list --long 单会话块的解析结果。SyncMode 为空表示输出未显示模式（不能确认 two-way-safe）。</summary>
public sealed record ParsedMutagenSession(
    string Name, string AlphaUrl, string BetaUrl,
    string SyncMode, string Status, bool Paused, bool BetaConnected, bool ConnectionFailure,
    string LastError, bool TransitionProblems,
    string[] SafeConflictPaths, int DiscardedConflictEntries, int ExcludedConflicts, bool ConflictSectionPresent);

/// <summary>一次链路评估：当前状态 + 可读详情 + 上次成功观测时间（始终单独给出，过期不升为当前）。</summary>
public sealed record SyncLinkObservation(SyncLinkState State, string Detail, DateTimeOffset? LastObservedAt)
{
    public string Label => SyncReliability.Label(State);
}

public static class SyncReliability
{
    /// <summary>§4：成功观测距现在超过 30 秒只能作上次观测。取自 15s 节流 + 10s 超时的余量，不是传播时限。</summary>
    public const int FreshnessSeconds = 30;
    /// <summary>§5：冲突清单最多显示 50 行，剩余条数必须写出。</summary>
    public const int MaxConflictRows = 50;

    private static readonly string[] ConnectionMarkers =
    [
        "unable to connect", "connection refused", "actively refused", "dial tcp", "could not resolve hostname",
        "connection timed out", "timed out", "handshake", "connection reset", "no route to host", "network is unreachable",
    ];
    private static readonly string[] SyncingStatuses =
    [
        "Scanning files", "Reconciling changes", "Staging files on alpha", "Staging files on beta",
        "Applying changes", "Waiting 5 seconds for rescan", "Saving archive",
    ];
    private static readonly Regex ConflictLine = new(@"^\((alpha|beta)\)\s+(.+)\s+\((.* -> .*)\)$", RegexOptions.Compiled);
    private static readonly Regex ExcludedLine = new(@"^\.\.\.\+(\d+) more\.\.\.$", RegexOptions.Compiled);

    public static string Label(SyncLinkState state) => state switch
    {
        SyncLinkState.SafetyPaused => "安全暂停",
        SyncLinkState.Conflict => "冲突",
        SyncLinkState.Unknown => "未知",
        SyncLinkState.Offline => "离线",
        SyncLinkState.NotConverged => "未收敛",
        SyncLinkState.Syncing => "同步中",
        SyncLinkState.Converged => "已收敛",
        _ => "未知",
    };

    /// <summary>整行短横线分隔会话块；单会话可能没有分隔线（与 WorkspaceInsights.SplitSessions 同一规则）。空白块不是会话，跳过。</summary>
    public static string[] SplitSessionBlocks(string listOutput)
    {
        var blocks = new List<string>();
        var current = new System.Text.StringBuilder();
        void Flush()
        {
            var text = current.ToString();
            if (text.Trim().Length > 0) blocks.Add(text);
            current.Clear();
        }
        foreach (var line in (listOutput ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length >= 10 && line.All(c => c == '-')) { Flush(); continue; }
            current.AppendLine(line);
        }
        Flush();
        return blocks.ToArray();
    }

    /// <summary>§5 安全路径过滤：拒绝空、绝对、盘符、UNC、任何含 .. 段；&lt;root&gt; 是根冲突不是相对文件路径，同样丢弃。</summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "<root>") return false;
        if (path.StartsWith('/') || path.StartsWith('\\')) return false;
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') return false;
        return path.Split('/', '\\').All(segment => segment != "..");
    }

    /// <summary>解析单个会话块。无 Name 行视为无法解析（null）。冲突计数形（默认 list）记为无法展示的条目。</summary>
    public static ParsedMutagenSession? ParseSession(string block)
    {
        var name = Field(block, "Name:");
        if (name.Length == 0) return null;
        string alphaUrl = "", betaUrl = "", section = "", syncMode = "";
        var betaConnected = true;
        var safe = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var discardedPaths = new HashSet<string>(StringComparer.Ordinal);
        int discarded = 0, excluded = 0;
        var conflictSection = false;
        var inConflicts = false;
        foreach (var raw in (block ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            var indented = raw.StartsWith('\t') || raw.StartsWith(' ');
            if (!indented)
            {
                section = trimmed is "Alpha:" or "Beta:" ? trimmed[..^1] : "";
                inConflicts = false;
            }
            if (trimmed.StartsWith("URL:", StringComparison.Ordinal) && section.Length > 0)
            {
                var url = trimmed[4..].Trim();
                if (section == "Alpha" && alphaUrl.Length == 0) alphaUrl = url;
                if (section == "Beta" && betaUrl.Length == 0) betaUrl = url;
            }
            if (section == "Beta" && trimmed.StartsWith("Connected:", StringComparison.Ordinal))
                betaConnected = trimmed[10..].Trim() == "Yes";
            if (trimmed.StartsWith("Synchronization mode:", StringComparison.Ordinal))
                syncMode = NormalizeMode(trimmed[21..].Trim());
            else if (trimmed.StartsWith("Sync mode:", StringComparison.Ordinal))
                syncMode = NormalizeMode(trimmed[10..].Trim());
            if (trimmed.StartsWith("Conflicts:", StringComparison.Ordinal))
            {
                conflictSection = true;
                var value = trimmed[10..].Trim();
                if (int.TryParse(value, out var count))
                {
                    // 默认 list 只有计数：知道有冲突但无法展示路径 → 全部记为无法展示（fail-closed 未知）。
                    discarded += count;
                    inConflicts = false;
                }
                else inConflicts = value.Length == 0;
                continue;
            }
            if (!inConflicts) continue;
            var excludedMatch = ExcludedLine.Match(trimmed);
            if (excludedMatch.Success) { excluded += int.Parse(excludedMatch.Groups[1].Value); continue; }
            var match = ConflictLine.Match(trimmed);
            if (!match.Success) { discarded++; continue; }
            var path = match.Groups[2].Value.Trim();
            // 一个冲突条目渲染为 alpha/beta 两行：不安全路径按条目（路径）去重计数，不按行计数。
            if (!IsSafeRelativePath(path)) { if (discardedPaths.Add(path)) discarded++; continue; }
            if (seen.Add(path)) safe.Add(path);
        }
        var status = Field(block, "Status:");
        var lastError = Field(block, "Last error:");
        var connectionFailure = !betaConnected &&
            (status is "Connecting to beta" or "Disconnected" ||
             ConnectionMarkers.Any(m => lastError.Contains(m, StringComparison.OrdinalIgnoreCase)));
        return new ParsedMutagenSession(
            name, alphaUrl, betaUrl, syncMode, status,
            Paused: status == "[Paused]", BetaConnected: betaConnected, ConnectionFailure: connectionFailure,
            LastError: lastError, TransitionProblems: (block ?? "").Contains("Transition problems:", StringComparison.Ordinal),
            SafeConflictPaths: safe.ToArray(), DiscardedConflictEntries: discarded, ExcludedConflicts: excluded,
            ConflictSectionPresent: conflictSection);
    }

    private static string NormalizeMode(string value)
    {
        var compact = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return compact switch
        {
            "twowaysafe" => "two-way-safe",
            "twowayresolved" => "two-way-resolved",
            _ => value.ToLowerInvariant(),
        };
    }

    private static string Field(string? block, string prefix)
    {
        foreach (var line in (block ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal)) return trimmed[prefix.Length..].Trim();
        }
        return "";
    }

    /// <summary>§4 状态机：只用这一次观测计算当前状态；超过 30s 的观测降为未知并保留上次标签作上次观测。</summary>
    public static SyncLinkObservation EvaluateLink(ParsedMutagenSession session, DateTimeOffset observedAt, DateTimeOffset now)
    {
        var (state, detail) = Classify(session);
        if (now - observedAt > TimeSpan.FromSeconds(FreshnessSeconds))
            return new(SyncLinkState.Unknown,
                $"观测过期（距上次成功观测超过 {FreshnessSeconds} 秒）；上次状态 {Label(state)} 只是上次观测，不是当前状态。",
                observedAt);
        return new(state, detail, observedAt);
    }

    private static (SyncLinkState State, string Detail) Classify(ParsedMutagenSession session)
    {
        if (session.Paused)
            return (SyncLinkState.SafetyPaused, "操作员暂停（mutagen pause）；会话历史保留，不自动 resume。");
        if (session.Status.StartsWith("Halted", StringComparison.Ordinal))
        {
            var reason = session.Status.Contains("root deletion", StringComparison.Ordinal) ? "一端的同步根被删除"
                : session.Status.Contains("one-sided root emptying", StringComparison.Ordinal) ? "只有一端把同步根清空"
                : session.Status.Contains("root type change", StringComparison.Ordinal) ? "同步根被换成另一种类型"
                : "原因无法归类";
            return (SyncLinkState.SafetyPaused, $"Mutagen 安全暂停：{reason}。历史保留，不自动 resume、不自动 reset；安全检测不是原子的，不承诺零丢失。");
        }
        if (session.SafeConflictPaths.Length > 0)
        {
            var hidden = session.DiscardedConflictEntries + session.ExcludedConflicts;
            return (SyncLinkState.Conflict,
                $"{session.SafeConflictPaths.Length} 个双方分歧冲突待处理（清单只读）" +
                (hidden > 0 ? $"；另有 {hidden} 个条目无法展示。" : "。"));
        }
        if (session.ConflictSectionPresent && (session.DiscardedConflictEntries > 0 || session.ExcludedConflicts > 0))
            return (SyncLinkState.Unknown, "冲突段无法完整解析且没有任何安全冲突行；按未知处理，不能当成已收敛。");
        if (session.ConnectionFailure)
            return (SyncLinkState.Offline, "远端端点连接失败（本次 list 成功，beta 未连接）；会话与历史保留，不 terminate、不重建。");
        if (!session.BetaConnected)
            return (SyncLinkState.NotConverged, "beta 端点未连接，但输出无法确认为连接失败；不能叫已收敛。");
        if (session.TransitionProblems)
            return (SyncLinkState.NotConverged, "Mutagen 报告 Transition problems（常见原因：远程目录父目录不属于 agent 用户，无写权）；改动可能无法落回本机。");
        if (session.SyncMode == "two-way-resolved")
            return (SyncLinkState.NotConverged, "会话模式是 two-way-resolved：alpha（本机）赢得全部分歧，包括删除覆盖对方修改；同权下最高未收敛。替换需走破坏性重置的单独确认（本轮未实现）。");
        if (session.Status == "Watching for changes")
            return session.SyncMode == "two-way-safe"
                ? (SyncLinkState.Converged, "two-way-safe 会话 Watching，无转换问题、无冲突、无暂停；该次观测没有未处理问题。不代替字节比较，也不保证观测之后的新写入已到达。")
                : (SyncLinkState.NotConverged, "输出未确认同步模式为 two-way-safe；不能叫已收敛。");
        if (SyncingStatuses.Contains(session.Status))
            return (SyncLinkState.Syncing, $"本轮正在处理（{session.Status}）；无安全暂停、冲突、转换问题或离线。");
        return (SyncLinkState.NotConverged, $"状态「{session.Status}」无法确认为同步中；会话还在，但不能叫已收敛。");
    }

    /// <summary>探针失败（找不到命令/超时/非零退出/输出为空）：当前状态未知，上次观测时间保留。</summary>
    public static SyncLinkObservation FailedProbe(string error, DateTimeOffset? lastSuccessAt, SyncLinkState? lastState, DateTimeOffset now)
    {
        var history = lastSuccessAt is { } at
            ? $"上次观测 {at.ToLocalTime():HH:mm:ss}" + (lastState is { } s ? $"（上次状态 {Label(s)}）" : "") + "；不能作为当前状态。"
            : "尚无成功观测。";
        return new(SyncLinkState.Unknown, SanitizeErrorText(error) + " " + history, lastSuccessAt);
    }

    /// <summary>§4 整体标签：按七条优先级取第一条成立的条件。两条必要链路都已收敛才是全部已收敛；单链路至多单链路已收敛。</summary>
    public static string OverallLabel(IReadOnlyList<SyncLinkState> requiredLinks)
    {
        if (requiredLinks.Count == 0) return "未同步";
        if (requiredLinks.Any(s => s == SyncLinkState.SafetyPaused)) return "安全暂停";
        if (requiredLinks.Any(s => s == SyncLinkState.Conflict)) return "冲突";
        if (requiredLinks.Any(s => s == SyncLinkState.Unknown)) return "未知";
        if (requiredLinks.Any(s => s == SyncLinkState.Offline)) return "离线";
        if (requiredLinks.Any(s => s == SyncLinkState.NotConverged)) return "未收敛";
        if (requiredLinks.Any(s => s == SyncLinkState.Syncing)) return "同步中";
        return requiredLinks.Count >= 2 ? "全部已收敛" : "单链路已收敛";
    }

    /// <summary>§5 冲突清单聚合：最多 50 行；剩余 = 被截断的安全行 + mutagen 排除行；截断不改变整体冲突。</summary>
    public static SyncConflictList BuildConflictList(IReadOnlyList<(ParsedMutagenSession Session, string LinkLabel, string AlphaRole, string BetaRole)> links)
    {
        var rows = new List<SyncConflictRow>();
        var discarded = 0;
        var excluded = 0;
        foreach (var (session, linkLabel, alphaRole, betaRole) in links)
        {
            discarded += session.DiscardedConflictEntries;
            excluded += session.ExcludedConflicts;
            foreach (var path in session.SafeConflictPaths) rows.Add(new(path, linkLabel, alphaRole, betaRole));
        }
        var shown = rows.Take(MaxConflictRows).ToArray();
        return new(shown, rows.Count - shown.Length + excluded, discarded);
    }

    /// <summary>§4 脱敏：去地址、用户名、密钥路径与远程路径；保留 agent-launcher- 会话名（主机卡片名由 UI 从配置补充，不来自错误文本）。</summary>
    public static string SanitizeErrorText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = Regex.Replace(text, @"ssh://[^\s]+", "ssh://[已脱敏]");
        result = Regex.Replace(result, @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+", "[已脱敏]");
        result = Regex.Replace(result, @"\b\d{1,3}(?:\.\d{1,3}){3}\b", "[地址已脱敏]");
        result = Regex.Replace(result, @"\b[A-Za-z]:\\[^\s]*", "[路径已脱敏]");
        result = Regex.Replace(result, @"\\\\[^\s]+", "[路径已脱敏]");
        result = Regex.Replace(result, @"(?:/[\w.-]+){2,}", "[路径已脱敏]");
        return result;
    }

    /// <summary>§6/R41：None 与 SSHFS 都不是已收敛。None 按 2026-11-19 更晚决定（IMPLEMENTATION §92）是拒收文案。
    /// attemptAlive=最近尝试 Started/Reattaching（SSHFS 启动 fail-closed，活着即挂载成功）；attemptFailed=最近尝试 FailedBeforeStart。</summary>
    public static (string Label, string Detail) NonMutagenSyncBadge(WorkspaceSyncMode mode, bool attemptAlive, bool attemptFailed)
    {
        if (mode == WorkspaceSyncMode.None)
            return ("未同步", "该主机同步模式为 none：这次不是同权。三台同权要求 mutagen 或 sshfs，none 的保存与启动已被拒收；远端 agent 的改动不会落回本机。");
        if (attemptAlive)
            return ("已挂载", "SSHFS 挂载成功；文件经挂载点直接读写。这是挂载提示，不是已收敛，更不是三台总线；京云的改动不会经 SSHFS 传到阿里云。");
        if (attemptFailed)
            return ("挂载失败", "SSHFS 挂载失败，该次启动已被阻止（fail-closed）；不是已收敛。请检查 WinFsp/SSHFS-Win 与远程目录。");
        return ("未挂载", "尚无成功的 SSHFS 挂载尝试；不是已收敛。挂载在远程启动时建立。");
    }
}