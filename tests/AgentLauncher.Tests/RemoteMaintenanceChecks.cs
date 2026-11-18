using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Tests;

// 远程维护检查（B2 资源面板 / C1 远程续接 / C2 版本固定与回滚）：
// 纯解析/推导逻辑直接断言；SSH 通道用 fake-ssh（主机地址携带夹具标记，沿用 disconnect/hold 注入模式），不触碰真实 VPS。
internal static class RemoteMaintenanceChecks
{
    public static async Task<int> RunAsync()
    {
        var fakeSshPath = Path.Combine(AppContext.BaseDirectory, "ssh.exe");
        if (!File.Exists(fakeSshPath)) throw new InvalidOperationException($"fake SSH not found: {fakeSshPath}");
        var fakeSshDir = Path.GetDirectoryName(fakeSshPath)!;
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", fakeSshDir + Path.PathSeparator + originalPath);
        var tempRoot = Path.Combine(Path.GetTempPath(), "agent-launcher-remote-maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var evidence = new List<object>();
            void Assert(bool ok, string why) { if (!ok) throw new Exception(why); }
            async Task Test(string name, Func<Task> action)
            {
                try { await action(); Console.WriteLine("PASS " + name); evidence.Add(new { name, passed = true }); }
                catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e.Message); evidence.Add(new { name, passed = false, error = e.ToString() }); }
            }
            RemoteHost Host(string address) => new(Guid.NewGuid(), "测试 VPS", address, 22, "testuser", "agent", ["pi"], null);
            const string dir = "/home/agent/work";

            await Test("B2 resource sample parsing free -b/nproc/loadavg", () =>
            {
                const string output = "               total        used        free      shared  buff/cache   available\nMem:     17179869184  4294967296  2147483648   1048576 10737418240 12884901888\nSwap:              0           0           0\n4\n0.52 0.58 0.59 1/234 5678\n";
                var sample = RemoteAgentMaintenance.ParseResourceSample(output);
                Assert(sample is { TotalBytes: 17179869184, AvailableBytes: 12884901888, Cores: 4 }, "资源解析结果不正确：" + sample);
                Assert(Math.Abs(sample!.Load15 - 0.59) < 1e-9, "应取 15 分钟负载（第三列），实际：" + sample.Load15);
                return Task.CompletedTask;
            });

            await Test("B2 resource parsing fail-closed on malformed output", () =>
            {
                const string mem = "Mem:     17179869184  4294967296  2147483648   1048576 10737418240 12884901888";
                string[] bad =
                [
                    "",
                    "FAKE-SSH: -p 22 -o BatchMode=yes testuser@fake.example.com free -b && nproc",
                    "Mem: 17179869184 4294967296 2147483648 1048576 10737418240\n4\n0.52 0.58 0.59 1/234 5678",  // 旧版 free 无 available 列
                    "Mem: total used free shared buff/cache available\n4\n0.52 0.58 0.59 1/234 5678",
                    "Mem: 0 0 0 0 0 0\n4\n0.52 0.58 0.59 1/234 5678",
                    "Mem: 100 0 0 0 0 200\n4\n0.52 0.58 0.59 1/234 5678",  // available > total
                    mem + "\n0.52 0.58 0.59 1/234 5678",                    // 缺 nproc
                    mem + "\n4",                                            // 缺 loadavg
                    mem + "\n4\n0.52 0.58 -1 1/234 5678",                   // 负负载
                    mem + "\n4\n0.52 0.58 0.59",                            // loadavg 缺 runnable 列
                    mem + "\n" + mem + "\n4\n0.52 0.58 0.59 1/234 5678",    // 重复 Mem 行
                    mem + "\nnot-a-number\n0.52 0.58 0.59 1/234 5678"
                ];
                foreach (var output in bad)
                    Assert(RemoteAgentMaintenance.ParseResourceSample(output) is null, "畸形输出被解析成了资源值：" + output.Replace("\n", "\\n"));
                return Task.CompletedTask;
            });

            await Test("B2 SampleResourcesAsync via fake-ssh ok/bad/disconnect", async () =>
            {
                var ok = await SshRemoteCommand.SampleResourcesAsync(Host("resources-ok.example.com"));
                Assert(ok is { TotalBytes: 17179869184, AvailableBytes: 12884901888, Cores: 4 }, "fake-ssh 资源夹具应解析成功：" + ok);
                Assert(await SshRemoteCommand.SampleResourcesAsync(Host("resources-bad.example.com")) is null, "畸形远端输出应 fail-closed 返回 null");
                Assert(await SshRemoteCommand.SampleResourcesAsync(Host("disconnect.example.com")) is null, "SSH 255 应返回 null");
                Assert(await SshRemoteCommand.SampleResourcesAsync(Host("fake.example.com")) is null, "非资源输出（exit 0）也应解析失败返回 null");
            });

            await Test("C1 ResumeFlagsFor mapping fail-closed", () =>
            {
                foreach (var agent in new[] { "pi", "kimi", "qodercli", "qoder" })
                    Assert(RemoteAgentMaintenance.ResumeFlagsFor(agent) is ["--continue"], agent + " 应映射为 [--continue]");
                foreach (var agent in new[] { "omp", "codex", "claude", "gemini", "custom", "" })
                    Assert(RemoteAgentMaintenance.ResumeFlagsFor(agent) is null, agent + " 未实测续接参数，应返回 null（fail-closed）");
                return Task.CompletedTask;
            });

            await Test("C1 help flag word-boundary matching", () =>
            {
                Assert(RemoteAgentMaintenance.HelpContainsFlag("  -c, --continue    Continue the last session", "--continue"), "应匹配 --continue");
                Assert(RemoteAgentMaintenance.HelpContainsFlag("  -c, --continue", "-c"), "应匹配 -c");
                Assert(!RemoteAgentMaintenance.HelpContainsFlag("--continue-session", "--continue"), "--continue-session 不应匹配 --continue");
                Assert(!RemoteAgentMaintenance.HelpContainsFlag("--no-continue", "--continue"), "--no-continue 不应匹配 --continue");
                Assert(!RemoteAgentMaintenance.HelpContainsFlag("x-c --continue", "-c"), "x-c 不应匹配 -c");
                Assert(!RemoteAgentMaintenance.HelpContainsFlag("--continue", "-c"), "--continue 内部不应匹配 -c");
                Assert(!RemoteAgentMaintenance.HelpContainsFlag("", "--continue"), "空 help 不应匹配");
                return Task.CompletedTask;
            });

            await Test("C1 remote help verification fail-closed via fake-ssh", async () =>
            {
                var (ok, _) = await SshRemoteCommand.VerifyResumeFlagsAsync(Host("help-ok.example.com"), "pi", dir, ["--continue"]);
                Assert(ok, "help 含 --continue 时应通过核对");
                var (missingOk, missingMessage) = await SshRemoteCommand.VerifyResumeFlagsAsync(Host("help-missing.example.com"), "pi", dir, ["--continue"]);
                Assert(!missingOk && missingMessage.Contains("已拒绝"), "help 不含 --continue 时应拒绝续接：" + missingMessage);
                var (downOk, downMessage) = await SshRemoteCommand.VerifyResumeFlagsAsync(Host("disconnect.example.com"), "pi", dir, ["--continue"]);
                Assert(!downOk && downMessage.Contains("已拒绝"), "SSH 失败时应拒绝续接：" + downMessage);
                var unknownThrew = false;
                try { await SshRemoteCommand.VerifyResumeFlagsAsync(Host("help-ok.example.com"), "unknown-agent", dir, ["--continue"]); }
                catch (InvalidDataException) { unknownThrew = true; }
                Assert(unknownThrew, "未知 agent 无 help 命令，应 fail-closed 报错");
            });

            await Test("C1 resume arguments replace --no-session / append / reject", () =>
            {
                var replaced = RemoteAgentMaintenance.ResumeArguments(["-p", "-a", "--no-session"], ["--continue"], "修复登录超时");
                Assert(replaced.SequenceEqual(new[] { "-p", "-a", "--continue", "修复登录超时" }), "应替换 --no-session 并附加任务说明：" + string.Join(" ", replaced));
                var appended = RemoteAgentMaintenance.ResumeArguments(["-p"], ["--continue"], "新任务");
                Assert(appended.SequenceEqual(new[] { "-p", "--continue", "新任务" }), "无 --no-session 时应追加续接参数");
                var oldDropped = RemoteAgentMaintenance.ResumeArguments(["-p", "-a", "--no-session", "旧任务"], ["--continue"], "新任务");
                Assert(oldDropped.SequenceEqual(new[] { "-p", "-a", "--continue", "新任务" }), "末尾旧任务说明应被新说明替换：" + string.Join(" ", oldDropped));
                var oldDropped2 = RemoteAgentMaintenance.ResumeArguments(["-p", "旧任务"], ["--continue"], "新任务");
                Assert(oldDropped2.SequenceEqual(new[] { "-p", "--continue", "新任务" }), "无 --no-session 时旧说明同样应被替换");
                var valueKept = RemoteAgentMaintenance.ResumeArguments(["-p", "--model", "x"], ["--continue"], "新任务");
                Assert(valueKept.SequenceEqual(new[] { "-p", "--model", "x", "--continue", "新任务" }), "取值 flag 的末尾值不应被误删");
                var interactiveThrew = false;
                try { RemoteAgentMaintenance.ResumeArguments(["-a"], ["--continue"], "任务"); } catch (InvalidDataException) { interactiveThrew = true; }
                Assert(interactiveThrew, "非 -p 任务模式应拒绝续接");
                foreach (var existing in new[] { "--continue", "-c", "--resume", "-r", "--session", "--session=abc" })
                {
                    var threw = false;
                    try { RemoteAgentMaintenance.ResumeArguments([existing], ["--continue"], "任务"); }
                    catch (InvalidDataException) { threw = true; }
                    Assert(threw, "原参数已含 " + existing + " 时应拒绝再次续接");
                }
                var emptyThrew = false;
                try { RemoteAgentMaintenance.ResumeArguments([], ["--continue"], "  "); } catch (InvalidDataException) { emptyThrew = true; }
                Assert(emptyThrew, "空任务说明应被拒绝");
                return Task.CompletedTask;
            });

            await Test("C2 version parsing first line semver", () =>
            {
                Assert(RemoteAgentMaintenance.ParseVersion("pi 1.5.2\nextra") == "1.5.2", "应取首行语义版本");
                Assert(RemoteAgentMaintenance.ParseVersion("\nv2.0.0-beta.1\n") == "2.0.0-beta.1", "应支持预发布版本");
                Assert(RemoteAgentMaintenance.ParseVersion("1.2.3+build.5") == "1.2.3+build.5", "应支持 build 元数据");
                Assert(RemoteAgentMaintenance.ParseVersion("FAKE-SSH: args") is null, "无语义版本应返回 null");
                Assert(RemoteAgentMaintenance.ParseVersion("") is null, "空输出应返回 null");
                Assert(RemoteAgentMaintenance.ParseVersion("version unknown\n9.9.9") is null, "只核对首个非空行，不跨行猜测");
                return Task.CompletedTask;
            });

            await Test("C2 version history cap 50 and previous version", () =>
            {
                var history = Array.Empty<RemoteVersionEntry>();
                for (var i = 0; i < 60; i++)
                    history = RemoteAgentMaintenance.AppendVersion(history, new RemoteVersionEntry("pi", "1.0." + i, DateTimeOffset.UtcNow, "npm install -g @earendil-works/pi-coding-agent@latest"));
                Assert(history.Length == 50, "每主机每 agent 历史上限应为 50，实际：" + history.Length);
                Assert(history[0].Version == "1.0.10" && history[^1].Version == "1.0.59", "裁剪应保留最新 50 条");
                Assert(RemoteAgentMaintenance.PreviousVersion([]) is null, "无历史时回滚目标应为 null");
                Assert(RemoteAgentMaintenance.PreviousVersion([new("pi", "1.0.0", DateTimeOffset.UtcNow, "cmd")]) is null, "只有一条记录时无上一版");
                var same = new RemoteVersionEntry("pi", "1.0.0", DateTimeOffset.UtcNow, "cmd");
                Assert(RemoteAgentMaintenance.PreviousVersion([same, same]) is null, "全部同版本时回滚目标应为 null");
                RemoteVersionEntry Entry(string version) => new("pi", version, DateTimeOffset.UtcNow, "cmd");
                Assert(RemoteAgentMaintenance.PreviousVersion([Entry("1.0.0"), Entry("1.1.0"), Entry("1.1.0")]) == "1.0.0", "回滚目标应为上一个不同版本");
                return Task.CompletedTask;
            });

            await Test("C2 pinned/rollback command derivation", () =>
            {
                const string npm = "npm install -g @earendil-works/pi-coding-agent@latest";
                const string pip = "python3 -m pip install --upgrade kimi-cli";
                Assert(RemoteAgentMaintenance.PinUpgradeCommand(npm, "1.5.2") == "npm install -g @earendil-works/pi-coding-agent@1.5.2", "npm 应把 @latest 替换为 @<version>");
                Assert(RemoteAgentMaintenance.PinUpgradeCommand(pip, "0.9.0") == "python3 -m pip install kimi-cli==0.9.0", "pip 应把 upgrade 替换为 install <pkg>==<version>");
                Assert(RemoteAgentMaintenance.PinUpgradeCommand(npm, "latest") == "npm install -g @earendil-works/pi-coding-agent@latest", "指定 latest 等价于最新");
                Assert(RemoteAgentMaintenance.SupportsVersionPinning(npm) && RemoteAgentMaintenance.SupportsVersionPinning(pip), "默认 npm/pip 形态应支持固定版本");
                const string overrideCommand = "bash /opt/upgrade-agent.sh";
                Assert(!RemoteAgentMaintenance.SupportsVersionPinning(overrideCommand), "不含 @latest/pip upgrade 的覆盖命令不应支持固定版本");
                Assert(RemoteAgentMaintenance.PinUpgradeCommand(overrideCommand, "1.0.0") is null, "覆盖命令固定版本应返回 null（只允许最新/原样）");
                foreach (var badVersion in new[] { "", "1.2.3; rm -rf /", "1.2.3 && echo", "$(whoami)", "1.2.3\n", "-1.0", "a b" })
                    Assert(RemoteAgentMaintenance.PinUpgradeCommand(npm, badVersion) is null, "不安全版本号应被拒绝：" + badVersion);
                return Task.CompletedTask;
            });

            await Test("C2 version history store record/trim/rebuild", () =>
            {
                var path = Path.Combine(tempRoot, "remote-versions.json");
                var hostId = Guid.NewGuid().ToString();
                for (var i = 0; i < 55; i++)
                    RemoteVersionHistory.Record(path, hostId, "pi", "1.0." + i, "pi --version");
                var history = RemoteVersionHistory.For(path, hostId, "pi");
                Assert(history.Length == 50 && history[^1].Version == "1.0.54", "落盘历史应裁剪到 50 条且保留最新");
                Assert(RemoteVersionHistory.For(path, hostId, "kimi").Length == 0, "不同 agent 的历史应隔离");
                Assert(RemoteVersionHistory.For(path, Guid.NewGuid().ToString(), "pi").Length == 0, "不同主机的历史应隔离");
                RemoteVersionHistory.RecordFromVersionOutput(path, Host("version-ok.example.com") with { Id = Guid.Parse(hostId) }, "pi", "no version here", "pi --version");
                Assert(RemoteVersionHistory.For(path, hostId, "pi")[^1].Version == "1.0.54", "无法解析版本时不应写入历史");
                File.WriteAllText(path, "{corrupted");
                RemoteVersionHistory.Record(path, hostId, "pi", "2.0.0", "pi --version");
                Assert(RemoteVersionHistory.For(path, hostId, "pi") is [{ Version: "2.0.0" }], "损坏历史应按空历史重建，不阻断维护");
                Store.Save(path, new RemoteVersionBook(2, []));
                Assert(RemoteVersionHistory.For(path, hostId, "pi").Length == 0, "未来 schema 应按空历史处理（fail-closed）");
                return Task.CompletedTask;
            });

            await Test("C2 CheckVersionAsync via fake-ssh records version", async () =>
            {
                var host = Host("version-ok.example.com");
                var result = await SshRemoteCommand.CheckVersionAsync(host, "pi", dir);
                Assert(!result.TimedOut && result.ExitCode == 0 && result.StdOut.Trim() == "1.2.3", "fake-ssh 版本夹具应返回 1.2.3");
                var path = Path.Combine(tempRoot, "remote-versions-check.json");
                RemoteVersionHistory.RecordFromVersionOutput(path, host, "pi", result.StdOut, RemoteAgentMaintenance.VersionCommandFor("pi")!);
                var entry = RemoteVersionHistory.For(path, host.Id.ToString(), "pi").Single();
                Assert(entry is { Agent: "pi", Version: "1.2.3", Command: "pi --version" }, "版本历史条目不正确：" + entry);
            });

            Store.Save(Path.Combine(tempRoot, "remote-maintenance-results.json"), evidence);
            Console.WriteLine("Evidence: " + Path.Combine(tempRoot, "remote-maintenance-results.json"));
            return evidence.Any(e => e.GetType().GetProperty("passed")!.GetValue(e) is false) ? 1 : 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { /* 证据文件被占用时保留 */ }
        }
    }
}