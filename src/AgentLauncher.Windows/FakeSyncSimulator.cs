#if INTEGRATION_TESTS
using System.Text.Json;

namespace AgentLauncher.Windows;

// 同步可靠性传播模拟器（仅 INTEGRATION_TESTS）：三个本地目录模拟本机/京云/阿里云，
// 按链路在线/离线开关做小文件复制传播；双方改同一路径→冲突且互不覆盖；list 输出由状态生成（mutagen --long 形状）；
// flush 失败开关；调用日志（create/list/flush/terminate）供断言不 terminate/create/reset。
// 场景文件由 AGENT_LAUNCHER_FAKE_SYNC_SCENARIO 指向，每次操作重读，测试改写 JSON 即可切换开关。
internal sealed record FakeSyncLink
{
    public string SessionName { get; init; } = "";
    public string LinkLabel { get; init; } = "";
    public string AlphaPath { get; init; } = "";
    public string BetaPath { get; init; } = "";
    public bool Online { get; init; } = true;
    public bool Paused { get; init; }
    public string? Halted { get; init; }
}

internal sealed record FakeSyncScenario
{
    public int PropagationDelayMs { get; init; } = 0;
    public bool FlushFails { get; init; }
    public string CallLog { get; init; } = "";
    public List<FakeSyncLink> Links { get; init; } = [];
}

internal static class FakeSyncSimulator
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, LinkState> States = [];

    private sealed class LinkState
    {
        public required FakeSyncLink Link;
        public Dictionary<string, byte[]?> Baseline = [];
        public Dictionary<string, DateTimeOffset> PendingSince = [];
        public HashSet<string> Conflicts = [];
    }

    private static FakeSyncScenario Scenario()
    {
        var path = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SYNC_SCENARIO");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new FakeSyncScenario();
        return JsonSerializer.Deserialize<FakeSyncScenario>(File.ReadAllText(path), Json) ?? new FakeSyncScenario();
    }

    private static void Log(string op, string session)
    {
        var log = Scenario().CallLog;
        if (log.Length == 0) return;
        File.AppendAllText(log, JsonSerializer.Serialize(new { op, session, at = DateTimeOffset.UtcNow }) + "\n");
    }

    internal static void Reset()
    {
        lock (Gate) { States.Clear(); }
    }

    internal static string Register(string sessionName, string alphaPath, string betaPath)
    {
        lock (Gate)
        {
            var scenario = Scenario();
            var link = scenario.Links.FirstOrDefault(l => l.SessionName == sessionName)
                ?? new FakeSyncLink { SessionName = sessionName, AlphaPath = alphaPath, BetaPath = betaPath };
            States[sessionName] = new LinkState { Link = link };
            Log("create", sessionName);
        }
        return sessionName;
    }

    internal static bool IsRegistered(string sessionName) { lock (Gate) return States.ContainsKey(sessionName); }

    internal static bool HasPending(string sessionName) { lock (Gate) return States.TryGetValue(sessionName, out var s) && s.PendingSince.Count > 0; }

    internal static string[] Conflicts(string sessionName) { lock (Gate) return States.TryGetValue(sessionName, out var s) ? s.Conflicts.ToArray() : []; }

    // 与创建参数一致的忽略规则（不扩大）：.git/node_modules/bin/obj 任意层级目录跳过。
    private static readonly string[] IgnoredDirs = [".git", "node_modules", "bin", "obj"];

    private static Dictionary<string, byte[]?> Snapshot(string root)
    {
        var result = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return result;
        void Walk(string dir, string prefix)
        {
            foreach (var file in Directory.GetFiles(dir))
                result[prefix + Path.GetFileName(file)] = File.ReadAllBytes(file);
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (IgnoredDirs.Contains(name)) continue;
                Walk(sub, prefix + name + "/");
            }
        }
        Walk(root, "");
        return result;
    }

    private static bool Same(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.SequenceEqual(b);

    private static void WriteOrDelete(string root, string relative, byte[]? content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (content is null) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static void Reconcile(LinkState state, int delayMs)
    {
        var link = state.Link;
        var alpha = Snapshot(link.AlphaPath);
        var beta = Snapshot(link.BetaPath);
        var now = DateTimeOffset.UtcNow;
        foreach (var path in alpha.Keys.Union(beta.Keys).ToArray())
        {
            var a = alpha.GetValueOrDefault(path);
            var b = beta.GetValueOrDefault(path);
            var basis = state.Baseline.GetValueOrDefault(path);
            if (Same(a, b))
            {
                state.Baseline[path] = a;
                state.Conflicts.Remove(path);
                state.PendingSince.Remove(path);
                continue;
            }
            var aChanged = !Same(a, basis);
            var bChanged = !Same(b, basis);
            if (aChanged && bChanged)
            {
                // two-way-safe 的无数据丢失自动处理：一侧删除、另一侧修改 → 修改赢（规格 §5 引用的模式行为）。
                if (a is null && b is not null && basis is not null)
                {
                    WriteOrDelete(link.AlphaPath, path, b);
                    state.Baseline[path] = b;
                    state.Conflicts.Remove(path);
                }
                else if (b is null && a is not null && basis is not null)
                {
                    WriteOrDelete(link.BetaPath, path, a);
                    state.Baseline[path] = a;
                    state.Conflicts.Remove(path);
                }
                else
                {
                    state.Conflicts.Add(path); // 双方修改/新建不同内容：冲突，互不覆盖，不自动选边。
                }
                state.PendingSince.Remove(path);
                continue;
            }
            var winner = aChanged ? a : b;
            var target = aChanged ? link.BetaPath : link.AlphaPath;
            if (state.PendingSince.TryGetValue(path, out var since) && (now - since).TotalMilliseconds >= delayMs)
            {
                WriteOrDelete(target, path, winner);
                state.Baseline[path] = winner;
                state.PendingSince.Remove(path);
                state.Conflicts.Remove(path);
            }
            else state.PendingSince.TryAdd(path, now);
        }
        // 清理根删除后的基线残留（两侧都不存在的路径）。
        foreach (var path in state.Baseline.Keys.Where(p => !alpha.ContainsKey(p) && !beta.ContainsKey(p)).ToArray())
            state.Baseline.Remove(path);
        // 双方都删除的路径不再有分歧内容，冲突与待传播随之消解（与基线残留清理对称；
        // 否则删除后冲突/Reconciling 永久残留——Reconcile 主循环只遍历仍存在的路径，不会清掉这些键）。
        foreach (var path in state.Conflicts.Where(p => !alpha.ContainsKey(p) && !beta.ContainsKey(p)).ToArray())
            state.Conflicts.Remove(path);
        foreach (var path in state.PendingSince.Keys.Where(p => !alpha.ContainsKey(p) && !beta.ContainsKey(p)).ToArray())
            state.PendingSince.Remove(path);
    }

    internal static void Tick()
    {
        lock (Gate)
        {
            var scenario = Scenario();
            foreach (var state in States.Values)
            {
                var link = scenario.Links.FirstOrDefault(l => l.SessionName == state.Link.SessionName) ?? state.Link;
                state.Link = link;
                if (!link.Online || link.Paused || link.Halted is not null) continue;
                Reconcile(state, scenario.PropagationDelayMs);
            }
        }
    }

    internal static string RenderList()
    {
        lock (Gate)
        {
            Log("list", "*");
            var scenario = Scenario();
            var text = "";
            foreach (var state in States.Values)
            {
                var link = scenario.Links.FirstOrDefault(l => l.SessionName == state.Link.SessionName) ?? state.Link;
                var status = link.Halted is { } halted ? "Halted due to " + halted
                    : link.Paused ? "[Paused]"
                    : !link.Online ? "Connecting to beta"
                    : state.PendingSince.Count > 0 ? "Reconciling changes"
                    : "Watching for changes";
                text += "-------------------------------------------------------------------------------\n";
                text += $"Name: {link.SessionName}\n";
                text += $"Identifier: sync_fake{link.SessionName}\n";
                text += "Configuration:\n\tSynchronization mode: Two Way Safe\n";
                text += $"Alpha:\n\tURL: {link.AlphaPath}\n\tConnected: Yes\n";
                text += $"Beta:\n\tURL: {link.BetaPath}\n\tConnected: {(link.Online ? "Yes" : "No")}\n";
                foreach (var path in state.Conflicts.OrderBy(p => p, StringComparer.Ordinal))
                {
                    if (path == state.Conflicts.OrderBy(p => p, StringComparer.Ordinal).First()) text += "Conflicts:\n";
                    text += $"\t(alpha) {path} (<non-existent> -> File (fakealpha))\n";
                    text += $"\t(beta)  {path} (<non-existent> -> File (fakebeta))\n";
                }
                if (!link.Online)
                    text += "Last error: unable to connect to beta: unable to connect to endpoint: dial tcp: connection refused (fake offline)\n";
                text += $"Status: {status}\n";
                text += "-------------------------------------------------------------------------------\n";
            }
            return text;
        }
    }

    internal static string? Flush(string sessionName)
    {
        lock (Gate)
        {
            Log("flush", sessionName);
            if (!States.TryGetValue(sessionName, out var state)) return null;
            var scenario = Scenario();
            state.Link = scenario.Links.FirstOrDefault(l => l.SessionName == sessionName) ?? state.Link;
            if (scenario.FlushFails)
                return $"工作区同步收敛失败（fake flush 失败开关）：会话 {sessionName} 已保留，请手动检查。";
            if (!state.Link.Online || state.Link.Paused || state.Link.Halted is not null)
                return $"工作区同步链路离线或暂停：会话 {sessionName} 已保留，请手动检查。";
            Reconcile(state, 0);
            Reconcile(state, 0);
            Log("terminate", sessionName);
            States.Remove(sessionName);
            return null;
        }
    }

    internal static async Task WaitReadyAsync(string sessionName, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            lock (Gate)
            {
                if (!States.TryGetValue(sessionName, out var state)) throw new IOException($"fake 同步会话不存在：{sessionName}");
                var link = Scenario().Links.FirstOrDefault(l => l.SessionName == sessionName) ?? state.Link;
                if (!link.Online) throw new TimeoutException($"fake 同步链路离线，无法就绪：{sessionName}");
                if (link.Paused || link.Halted is not null) throw new IOException($"fake 同步链路暂停，无法就绪；会话已保留：{sessionName}");
            }
            Tick();
            if (!HasPending(sessionName)) return;
            await Task.Delay(50, cancellation);
        }
        throw new TimeoutException($"fake 同步就绪超时：{sessionName}");
    }
}
#endif