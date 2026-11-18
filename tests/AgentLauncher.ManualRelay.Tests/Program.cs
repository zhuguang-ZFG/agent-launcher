using System.Diagnostics;
using System.Text;
using AgentLauncher.ManualRelay;

var failures = 0;
void Test(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
string Temp(string name)
{
    var path = Path.Combine(Path.GetTempPath(), "agent-launcher-manual-relay", Guid.NewGuid().ToString("N"), name);
    Directory.CreateDirectory(path);
    return path;
}
int AttemptCount(string store, string taskId)
{
    var path = Path.Combine(store, "tasks", taskId, "attempts");
    return Directory.Exists(path) ? Directory.GetDirectories(path).Length : 0;
}
int EventCount(string store, string taskId, string type)
{
    var path = Path.Combine(store, "tasks", taskId, "events.jsonl");
    if (!File.Exists(path)) return 0;
    var needle = "\"Type\":\"" + type + "\"";
    return File.ReadAllLines(path).Count(line => line.Contains(needle, StringComparison.Ordinal));
}
string CommitPath(string store, string taskId, string attemptId) => Path.Combine(store, "tasks", taskId, "attempts", attemptId, "commit.json");
string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "AgentLauncher.sln"))) return dir.FullName;
        dir = dir.Parent;
    }
    throw new Exception("找不到仓库根");
}
(int Code, string Output, string Error) RunCli(string dotnet, string root, params string[] args)
{
    var psi = new ProcessStartInfo(dotnet) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    psi.ArgumentList.Add("run");
    psi.ArgumentList.Add("--project");
    psi.ArgumentList.Add("src/AgentLauncher.ManualRelay.Cli/AgentLauncher.ManualRelay.Cli.csproj");
    psi.ArgumentList.Add("-c");
    psi.ArgumentList.Add("Release");
    psi.ArgumentList.Add("--");
    foreach (var arg in args) psi.ArgumentList.Add(arg);
    using var process = Process.Start(psi) ?? throw new Exception("无法启动 CLI");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    if (!process.WaitForExit(60000)) { process.Kill(entireProcessTree: true); throw new Exception("CLI 没有退出"); }
    return (process.ExitCode, output, error);
}

Test("MR1 safe path and symlink escape", () =>
{
    var root = Temp("ws");
    var outside = Temp("out");
    var sentinel = "SECRET-SENTINEL-9f3a";
    File.WriteAllText(Path.Combine(outside, "secret.txt"), sentinel);
    Directory.CreateDirectory(Path.Combine(root, "ok"));
    File.WriteAllText(Path.Combine(root, "ok", "a.txt"), "hello");
    Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);
    File.CreateSymbolicLink(Path.Combine(root, "linked.txt"), Path.Combine(outside, "secret.txt"));
    var resolved = SafePath.ResolveExisting(root, "ok/a.txt");
    Check(File.ReadAllText(resolved) == "hello", "安全相对路径应读到 hello");
    foreach (var bad in new[] { "escape/secret.txt", "linked.txt", "../secret.txt", Path.GetFullPath(Path.Combine(outside, "secret.txt")), "ok/../../secret.txt", "ok/./a.txt" })
    {
        var rejected = false;
        try { SafePath.ResolveExisting(root, bad); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "应拒绝路径 " + bad);
    }
    Check(File.ReadAllText(Path.Combine(outside, "secret.txt")) == sentinel, "拒绝路径时不得改写外部文件");
});

Test("MR2 idempotency and conflicting payload", () =>
{
    var clock = new ManualClock();
    var store = Temp("store");
    var relay = new LocalRelay(store, clock.Now);
    const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    var first = relay.Enqueue("same-key", "file_verify", "{\"files\":[{\"sha256\":\"" + sha + "\",\"path\":\"ok/a.txt\"}]}");
    var second = relay.Enqueue("same-key", "file_verify", "{\"files\":[{\"path\":\"ok/a.txt\",\"sha256\":\"" + sha + "\"}]}");
    Check(first.TaskId == second.TaskId, "相同幂等键和规范载荷必须是同一条任务");
    Check(Directory.GetDirectories(Path.Combine(store, "tasks")).Length == 1, "不得新建第二条任务");
    var conflict = false;
    try { relay.Enqueue("same-key", "file_verify", "{\"files\":[{\"path\":\"ok/a.txt\",\"sha256\":\"" + new string('b', 64) + "\"}]}"); }
    catch (InvalidDataException) { conflict = true; }
    Check(conflict, "冲突载荷必须拒绝");
    Check(relay.Read(first.TaskId).Status == "queued", "冲突不得改掉原任务");
    Check(Directory.GetDirectories(Path.Combine(store, "tasks")).Length == 1, "冲突不得留下第二条任务");
    foreach (var bad in new[] { "{\"shell\":\"pwned-marker-7781\"}", "{\"password\":\"secret-marker-4412\"}", "{\"files\":[{\"path\":\"../x\",\"sha256\":\"" + sha + "\"}]}" })
    {
        var rejected = false;
        try { relay.Enqueue("bad-" + bad.Length, "file_verify", bad); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "应拒绝载荷");
    }
    var body = string.Concat(Directory.GetFiles(store, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
    Check(!body.Contains("pwned-marker-7781", StringComparison.Ordinal), "shell 值不得落盘");
    Check(!body.Contains("secret-marker-4412", StringComparison.Ordinal), "凭据值不得落盘");
});

Test("MR2b duplicate json keys are rejected", () =>
{
    var store = Temp("store");
    var relay = new LocalRelay(store, new ManualClock().Now);
    var rejected = false;
    try { relay.Enqueue("dup-key", "file_verify", "{\"shell\":\"dup-marker-9157\",\"shell\":\"dup-marker-9157\",\"files\":[{\"path\":\"ok/a.txt\",\"sha256\":\"" + new string('a', 64) + "\"}]}"); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, "重复字段必须拒绝");
    var written = Directory.Exists(store) ? string.Concat(Directory.GetFiles(store, "*", SearchOption.AllDirectories).Select(File.ReadAllText)) : "";
    Check(!written.Contains("dup-marker-9157", StringComparison.Ordinal), "重复字段中的值不得落盘");
});

Test("MR3 lease contention", () =>
{
    var store = Temp("store");
    var clock = new ManualClock();
    var a = new LocalRelay(store, clock.Now);
    var b = new LocalRelay(store, clock.Now);
    var task = a.Enqueue("race", "result_data", "{\"data\":{\"n\":1}}");
    var start = new Barrier(2);
    LeaseOutcome? left = null, right = null;
    var t1 = Task.Run(() => { start.SignalAndWait(); left = a.TryLease(task.TaskId, "holder-a", TimeSpan.FromMinutes(5)); });
    var t2 = Task.Run(() => { start.SignalAndWait(); right = b.TryLease(task.TaskId, "holder-b", TimeSpan.FromMinutes(5)); });
    Task.WaitAll(t1, t2);
    var outcomes = new[] { left!, right! };
    Check(outcomes.Count(item => item.Accepted) == 1, "必须恰好一个领取成功");
    Check(AttemptCount(store, task.TaskId) == 1, "竞争不得制造两个 attempt");
    Check(EventCount(store, task.TaskId, "leased") == 1, "只能有一条 leased 事件");
});

Test("MR4 lease expiry does not rerun", () =>
{
    var clock = new ManualClock();
    var store = Temp("store");
    var relay = new LocalRelay(store, clock.Now);
    var task = relay.Enqueue("expire", "result_data", "{\"data\":{\"n\":1}}");
    var lease = relay.TryLease(task.TaskId, "holder", TimeSpan.FromMinutes(5));
    Check(lease.Accepted, "首次领取应成功");
    var attempts = AttemptCount(store, task.TaskId);
    clock.Utc = clock.Utc.AddMinutes(5);
    var again = relay.TryLease(task.TaskId, "other", TimeSpan.FromMinutes(5));
    Check(!again.Accepted, "到期后不得再领取");
    Check(again.View.Status == "uncertain", "到期后应为 uncertain");
    Check(AttemptCount(store, task.TaskId) == attempts, "到期不得新建 attempt");
    Check(EventCount(store, task.TaskId, "leased") == 1, "到期不得再记 leased");
});

Test("MR5 expired attempt cannot submit", () =>
{
    var clock = new ManualClock();
    var store = Temp("store");
    var relay = new LocalRelay(store, clock.Now);
    var task = relay.Enqueue("old-attempt", "result_data", "{\"data\":{\"n\":1}}");
    var lease = relay.TryLease(task.TaskId, "holder", TimeSpan.FromMinutes(5));
    var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
    File.WriteAllBytes(Path.Combine(relay.StagingDirectory(task.TaskId, lease.View.AttemptId!), "result.json"), bytes);
    clock.Utc = clock.Utc.AddMinutes(6);
    var submit = relay.Submit(task.TaskId, lease.View.AttemptId!, lease.View.Version, new[] { new FileDigest("result.json", Digests.Sha256(bytes), bytes.Length) });
    Check(!submit.Accepted && submit.Code == "lease_expired", "过期 attempt 必须拒绝提交，实际 " + submit.Code);
    Check(submit.View.Status == "uncertain", "过期提交后应为 uncertain");
    Check(!File.Exists(CommitPath(store, task.TaskId, lease.View.AttemptId!)), "过期提交不得写 commit");
    Check(AttemptCount(store, task.TaskId) == 1, "过期提交不得新建 attempt");
});

Test("MR5b stale version cannot submit", () =>
{
    var clock = new ManualClock();
    var store = Temp("store");
    var relay = new LocalRelay(store, clock.Now);
    var task = relay.Enqueue("stale-version", "result_data", "{\"data\":{\"n\":1}}");
    var lease = relay.TryLease(task.TaskId, "holder", TimeSpan.FromMinutes(5));
    var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
    File.WriteAllBytes(Path.Combine(relay.StagingDirectory(task.TaskId, lease.View.AttemptId!), "result.json"), bytes);
    var submit = relay.Submit(task.TaskId, lease.View.AttemptId!, lease.View.Version - 1, new[] { new FileDigest("result.json", Digests.Sha256(bytes), bytes.Length) });
    Check(!submit.Accepted && submit.Code == "version_mismatch", "旧版本必须拒绝，实际 " + submit.Code);
    Check(relay.Read(task.TaskId).Status == "leased", "版本拒绝后仍应是 leased");
    Check(!File.Exists(CommitPath(store, task.TaskId, lease.View.AttemptId!)), "版本拒绝不得写 commit");
});

Test("MR8a agent lease does not create an attempt", () =>
{
    var clock = new ManualClock();
    var store = Temp("store");
    var relay = new LocalRelay(store, clock.Now);
    var task = relay.Enqueue("think", "agent", "{\"instruction\":\"列出目录并删除临时文件\"}");
    var lease = relay.TryLease(task.TaskId, "holder", TimeSpan.FromMinutes(5));
    Check(!lease.Accepted && lease.Code == "needs_agent", "agent 只能归类，实际 " + lease.Code);
    Check(lease.View.Status == "needs_agent", "状态应为 needs_agent");
    Check(AttemptCount(store, task.TaskId) == 0, "不得创建 attempt");
});

Test("MR6 checksum mismatch", () =>
{
    var clock = new ManualClock();
    var store = Temp("store");
    var relay = new LocalRelay(store, clock.Now);
    var task = relay.Enqueue("sum", "result_data", "{\"data\":{\"n\":1}}");
    var lease = relay.TryLease(task.TaskId, "holder", TimeSpan.FromMinutes(5));
    var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
    File.WriteAllBytes(Path.Combine(relay.StagingDirectory(task.TaskId, lease.View.AttemptId!), "result.json"), bytes);
    var submit = relay.Submit(task.TaskId, lease.View.AttemptId!, lease.View.Version, new[] { new FileDigest("result.json", new string('c', 64), bytes.Length) });
    Check(!submit.Accepted && submit.Code == "checksum_mismatch", "错误校验和必须拒绝，实际 " + submit.Code);
    Check(relay.Read(task.TaskId).Status == "leased", "校验失败不得变成 succeeded");
    Check(!File.Exists(CommitPath(store, task.TaskId, lease.View.AttemptId!)), "校验失败不得写 commit");
});

Test("MR7 interrupted submit recovery", () =>
{
    var clock = new ManualClock();
    var store = Temp("store");
    var relay = new LocalRelay(store, clock.Now);
    var task = relay.Enqueue("crash", "result_data", "{\"data\":{\"n\":1}}");
    var fresh = relay.TryLease(task.TaskId, "holder", TimeSpan.FromMinutes(5));
    Check(relay.Recover(task.TaskId).Status == "leased", "尚未写入结果时不得改成 uncertain");
    var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
    var digest = new FileDigest("result.json", Digests.Sha256(bytes), bytes.Length);
    File.WriteAllBytes(Path.Combine(relay.StagingDirectory(task.TaskId, fresh.View.AttemptId!), "result.json"), bytes);
    relay.FaultAfter = "before-commit";
    var threw = false;
    try { relay.Submit(task.TaskId, fresh.View.AttemptId!, fresh.View.Version, new[] { digest }); }
    catch (IOException) { threw = true; }
    Check(threw, "before-commit 必须中断");
    Check(relay.Read(task.TaskId).Status == "leased", "中断当下不得自行改状态");
    relay.FaultAfter = null;
    var uncertain = relay.Recover(task.TaskId);
    Check(uncertain.Status == "uncertain" && AttemptCount(store, task.TaskId) == 1, "不完整提交必须变成 uncertain 且不新建 attempt");
    var later = relay.Enqueue("crash-after", "result_data", "{\"data\":{\"n\":2}}");
    var lease = relay.TryLease(later.TaskId, "holder", TimeSpan.FromMinutes(5));
    var bytes2 = Encoding.UTF8.GetBytes("{\"ok\":2}");
    var digest2 = new FileDigest("result.json", Digests.Sha256(bytes2), bytes2.Length);
    var resultPath = Path.Combine(store, "tasks", later.TaskId, "attempts", lease.View.AttemptId!, "staging", "result.json");
    File.WriteAllBytes(resultPath, bytes2);
    relay.FaultAfter = "after-commit";
    threw = false;
    try { relay.Submit(later.TaskId, lease.View.AttemptId!, lease.View.Version, new[] { digest2 }); }
    catch (IOException) { threw = true; }
    Check(threw && relay.Read(later.TaskId).Status == "leased", "after-commit 必须在补记状态前中断");
    var before = File.ReadAllBytes(resultPath);
    relay.FaultAfter = null;
    Check(relay.Recover(later.TaskId).Status == "succeeded", "完整提交标记应补记为 succeeded");
    Check(relay.Recover(later.TaskId).Status == "succeeded", "重复恢复仍是 succeeded");
    Check(AttemptCount(store, later.TaskId) == 1, "补记不得新建 attempt");
    Check(File.ReadAllBytes(resultPath).SequenceEqual(before), "恢复不得重写结果字节");
    Check(EventCount(store, later.TaskId, "recovery_submitted") == 1, "成功补记只能发生一次");
});

Test("MR9 duplicate result submit", () =>
{
    var clock = new ManualClock();
    var store = Temp("store");
    var relay = new LocalRelay(store, clock.Now);
    var task = relay.Enqueue("dup", "result_data", "{\"data\":{\"n\":1}}");
    var lease = relay.TryLease(task.TaskId, "holder", TimeSpan.FromMinutes(5));
    var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
    var file = new FileDigest("result.json", Digests.Sha256(bytes), bytes.Length);
    var resultPath = Path.Combine(store, "tasks", task.TaskId, "attempts", lease.View.AttemptId!, "staging", "result.json");
    File.WriteAllBytes(resultPath, bytes);
    var first = relay.Submit(task.TaskId, lease.View.AttemptId!, lease.View.Version, new[] { file });
    Check(first.Accepted && first.Code == "submitted", "首次提交应成功，实际 " + first.Code);
    var committed = File.ReadAllBytes(resultPath);
    var second = relay.Submit(task.TaskId, lease.View.AttemptId!, lease.View.Version, new[] { file });
    Check(second.Accepted && second.Code == "duplicate", "相同结果应视为重复提交，实际 " + second.Code);
    Check(EventCount(store, task.TaskId, "submitted") == 1, "不得写第二份成功事件");
    var third = relay.Submit(task.TaskId, lease.View.AttemptId!, first.View.Version, new[] { new FileDigest("result.json", new string('d', 64), bytes.Length) });
    Check(!third.Accepted && third.Code == "result_conflict", "不同结果必须拒绝，实际 " + third.Code);
    Check(relay.Read(task.TaskId).Status == "succeeded", "冲突不得推翻已提交状态");
    Check(File.ReadAllBytes(resultPath).SequenceEqual(committed), "冲突提交不得改写已提交字节");
});

Test("MR10 confirm uncertain before a new attempt", () =>
{
    var clock = new ManualClock();
    var store = Temp("store");
    var relay = new LocalRelay(store, clock.Now);
    var task = relay.Enqueue("confirm", "result_data", "{\"data\":{\"n\":1}}");
    var lease = relay.TryLease(task.TaskId, "holder", TimeSpan.FromMinutes(5));
    var oldAttempt = lease.View.AttemptId!;
    clock.Utc = clock.Utc.AddMinutes(6);
    Check(!relay.TryLease(task.TaskId, "holder", TimeSpan.FromMinutes(5)).Accepted, "未确认不得重新领取");
    relay.ConfirmUncertain(task.TaskId, "human-confirmed");
    var next = relay.TryLease(task.TaskId, "holder-2", TimeSpan.FromMinutes(5));
    Check(next.Accepted && next.View.AttemptId != oldAttempt, "确认后才允许新 attempt");
    var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
    File.WriteAllBytes(Path.Combine(relay.StagingDirectory(task.TaskId, next.View.AttemptId!), "result.json"), bytes);
    var stale = relay.Submit(task.TaskId, oldAttempt, next.View.Version, new[] { new FileDigest("result.json", Digests.Sha256(bytes), bytes.Length) });
    Check(!stale.Accepted && stale.Code == "attempt_mismatch", "旧 attempt 不能提交，实际 " + stale.Code);
    Check(relay.Read(task.TaskId).Status == "leased", "拒绝旧 attempt 后新租约仍在");
    Check(AttemptCount(store, task.TaskId) == 2, "新 attempt 之外不得再自动增加");
});

Test("MR11 future schema is not overwritten", () =>
{
    var clock = new ManualClock();
    var store = Temp("store");
    var relay = new LocalRelay(store, clock.Now);
    var task = relay.Enqueue("schema", "result_data", "{\"data\":{\"n\":1}}");
    var path = Path.Combine(store, "tasks", task.TaskId, "state.json");
    File.WriteAllText(path, File.ReadAllText(path).Replace("\"SchemaVersion\":1", "\"SchemaVersion\":99", StringComparison.Ordinal));
    var before = File.ReadAllBytes(path);
    var threw = false;
    try { relay.TryLease(task.TaskId, "holder", TimeSpan.FromMinutes(5)); }
    catch (InvalidDataException) { threw = true; }
    Check(threw, "未来 schema 必须拒绝");
    Check(File.ReadAllBytes(path).SequenceEqual(before), "拒绝时不得覆盖原文件");
});

Test("MR8 needs_agent does not execute", () =>
{
    var clock = new ManualClock();
    var store = Temp("store");
    var workspace = Temp("ws");
    var sentinelPath = Path.Combine(workspace, "keep.txt");
    File.WriteAllText(sentinelPath, "workspace-sentinel-2288");
    var before = File.ReadAllBytes(sentinelPath);
    var relay = new LocalRelay(store, clock.Now);
    var task = relay.Enqueue("agent-task", "agent", "{\"instruction\":\"列出目录并删除临时文件\"}");
    var report = new ManualSession().RunOnce(relay, workspace, "holder", TimeSpan.FromMinutes(5), task.TaskId);
    Check(report.Code == "needs_agent" && report.Status == "needs_agent", "会话只能归类，实际 " + report.Code + "/" + report.Status);
    Check(File.ReadAllBytes(sentinelPath).SequenceEqual(before), "不得修改隔离项目");
    Check(AttemptCount(store, task.TaskId) == 0, "不得创建执行 attempt");
    Check(relay.Read(task.TaskId).Status != "succeeded", "不得标成 succeeded");
});

Test("MR13 file_verify stays inside the workspace", () =>
{
    var clock = new ManualClock();
    var store = Temp("store");
    var workspace = Temp("ws");
    var outside = Temp("out");
    const string secret = "SECRET-SENTINEL-9f3a";
    File.WriteAllText(Path.Combine(outside, "secret.txt"), secret);
    Directory.CreateDirectory(Path.Combine(workspace, "notes"));
    File.WriteAllText(Path.Combine(workspace, "notes", "a.txt"), "hello-verify");
    var before = File.ReadAllBytes(Path.Combine(workspace, "notes", "a.txt"));
    Directory.CreateSymbolicLink(Path.Combine(workspace, "escape"), outside);
    var sha = Digests.Sha256(before);
    var relay = new LocalRelay(store, clock.Now);
    var good = relay.Enqueue("verify-ok", "file_verify", "{\"files\":[{\"path\":\"notes/a.txt\",\"sha256\":\"" + sha + "\"}]}");
    var report = new ManualSession().RunOnce(relay, workspace, "holder", TimeSpan.FromMinutes(5), good.TaskId);
    Check(report.Code == "submitted" && report.Status == "succeeded", "匹配的文件校验应提交，实际 " + report.Code + "/" + report.Status);
    Check(File.ReadAllBytes(Path.Combine(workspace, "notes", "a.txt")).SequenceEqual(before), "校验不得修改项目文件");
    var bad = relay.Enqueue("verify-link", "file_verify", "{\"files\":[{\"path\":\"escape/secret.txt\",\"sha256\":\"" + new string('a', 64) + "\"}]}");
    var rejected = new ManualSession().RunOnce(relay, workspace, "holder", TimeSpan.FromMinutes(5), bad.TaskId);
    Check(rejected.Code == "path_rejected" && rejected.Status == "rejected", "符号链接必须拒绝，实际 " + rejected.Code + "/" + rejected.Status);
    var stored = string.Concat(Directory.GetFiles(store, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
    Check(!stored.Contains(secret, StringComparison.Ordinal), "外部文件内容不得进入存储");
});

Test("MR12 one shot cli has no network adapter", () =>
{
    var root = RepoRoot();
    var forbidden = new[] { "HttpClient", "TcpListener", "UdpClient", "Process.Start", "ProcessStartInfo", "WebRequest", "Socket(", "Mutagen", "VPS.txt", "JD.pem", "while(true)", "while (true)", "Task.Delay" };
    foreach (var dir in new[] { Path.Combine(root, "src", "AgentLauncher.ManualRelay"), Path.Combine(root, "src", "AgentLauncher.ManualRelay.Cli") })
    {
        Check(Directory.Exists(dir), "缺少源码目录 " + dir);
        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (var token in forbidden) Check(!text.Contains(token, StringComparison.Ordinal), Path.GetFileName(file) + " 含有 " + token);
            if (dir.EndsWith("AgentLauncher.ManualRelay", StringComparison.Ordinal))
                Check(!text.Contains("DateTime.Now", StringComparison.Ordinal) && !text.Contains("UtcNow", StringComparison.Ordinal), Path.GetFileName(file) + " 不得自行取时钟");
        }
    }
    var dotnet = Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } sdk ? Path.Combine(sdk, "dotnet") : "dotnet";
    var listen = RunCli(dotnet, root, "--listen");
    Check(listen.Code == 2 && listen.Error.Contains("不监听", StringComparison.Ordinal), "监听参数必须拒绝，实际 " + listen.Code + " " + listen.Error);
    var store = Temp("store");
    var workspace = Temp("ws");
    File.WriteAllText(Path.Combine(workspace, "keep.txt"), "untouched");
    var before = File.ReadAllBytes(Path.Combine(workspace, "keep.txt"));
    var task = new LocalRelay(store, () => DateTimeOffset.Parse("2026-11-18T00:00:00Z")).Enqueue("cli", "result_data", "{\"data\":{\"note\":\"once\"}}");
    var run = RunCli(dotnet, root, "--store", store, "--workspace", workspace, "--holder", "holder", "--task", task.TaskId, "--ttl-seconds", "60");
    Check(run.Code == 0, "一次 CLI 应成功，实际 " + run.Code + " " + run.Error + run.Output);
    Check(run.Output.Contains("\"Status\":\"succeeded\"", StringComparison.Ordinal), "输出应是 succeeded：" + run.Output);
    Check(File.ReadAllBytes(Path.Combine(workspace, "keep.txt")).SequenceEqual(before), "CLI 不得修改项目文件");
});

Console.WriteLine(failures == 0 ? "MR tests passed, 0 failed" : failures + " failed");
return failures == 0 ? 0 : 1;

sealed class ManualClock
{
    public DateTimeOffset Utc = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
    public DateTimeOffset Now() => Utc;
}
