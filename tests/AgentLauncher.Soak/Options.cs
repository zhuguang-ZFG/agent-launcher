using System.Globalization;
using System.Text.RegularExpressions;

namespace SoakRunner;

internal sealed class UsageException(string message) : Exception(message);

internal enum RunnerMode
{
    Observe,
    Driver
}

internal sealed class RunnerOptions
{
    public RunnerMode Mode { get; private set; }
    public string Root { get; private set; } = "";
    public string RuntimeDirectory { get; private set; } = "";
    public string OmpPath { get; private set; } = "";
    public string OmpVersion { get; private set; } = "";
    public string NodePath { get; private set; } = "";
    public string GitPath { get; private set; } = "git";
    public string RunnerExecutable { get; private set; } = "";
    public string OutputDirectory { get; private set; } = "";
    public int Rounds { get; private set; }
    public int RoundSeconds { get; private set; }
    public int CalibrationSeconds { get; private set; }
    public bool AdmissionOnly { get; private set; }
    public int StartupSeconds { get; private set; } = 120;
    public int ShutdownSeconds { get; private set; } = 120;
    public int SampleSeconds { get; private set; } = 5;
    public int CommandTimeoutSeconds { get; private set; } = 30;
    public int CycleIntervalMilliseconds { get; private set; } = 1000;
    public int OmpShutdownSeconds { get; private set; } = 60;

    public string StateRoot { get; private set; } = "";
    public string AgentDirectory { get; private set; } = "";
    public string HomeDirectory { get; private set; } = "";
    public string StopFile { get; private set; } = "";
    public string ResultFile { get; private set; } = "";
    public int DurationSeconds { get; private set; }

    public static string Usage => """
用法:
  SoakRunner observe --root <AgentLauncher-tests-...> --runtime <red-runtime-dir>
      --omp <omp.exe> --omp-version <exact-version> --node <node.exe>
      [--admission-only] [--calibration-seconds <n>] [--out <dir>]
      [--git <git.exe>] [--startup-seconds 120] [--shutdown-seconds 120]
      [--sample-seconds 5] [--command-timeout-seconds 30] [--cycle-interval-ms 1000]
      [--omp-shutdown-seconds 60]

  SoakRunner driver --omp <omp.exe> --omp-version <exact-version> --node <node.exe>
      --state-root <dir> --agent-dir <dir> --home <dir>
      --duration-seconds <n> --stop-file <path> --result <path>
      [--git <git.exe>] [--command-timeout-seconds 30] [--cycle-interval-ms 1000]
      [--omp-shutdown-seconds 60]

说明:
  observe 是唯一入口，通过 AgentLauncher.Worker monitor 的 reserve / run 正常路径启动两个 driver。
  每个 driver 在独立工作目录、独立 PI_CODING_AGENT_DIR / HOME 中启动当前 omp --mode rpc。
  driver 只发送 bash 命令；不发送 prompt/login/provider/model 消息，不使用 SDK，不创建内部 host shim。
  真实稳定性模式固定为 3 个连续 1800 秒轮次；任何短于 1800 秒的轮次必须使用 --calibration-seconds，
  且校准结果永不进入长期稳定性结论。
  --root 必须每次为不存在的新目录，basename 必须以 AgentLauncher-tests- 开头。
  如已设置 AGENT_LAUNCHER_TEST_ROOT，必须与 --root 一致。路径不允许经过重解析点。
  输出目录默认位于测试根目录下；--out 如提供也必须位于测试根目录下且不能预先存在。
  退出码: 0 成功或仅准入检查通过; 2 用法; 3 准入阻止; 4 执行失败; 6 协议/版本不匹配。
""";

    public static RunnerOptions Parse(string[] args)
    {
        if (args.Length == 0) throw new UsageException("缺少模式。");
        var mode = args[0] switch
        {
            "observe" => RunnerMode.Observe,
            "driver" => RunnerMode.Driver,
            _ => throw new UsageException("模式必须是 observe 或 driver。")
        };
        var values = ParseValues(args);
        var driverOnly = new HashSet<string>(StringComparer.Ordinal)
        { "--state-root", "--agent-dir", "--home", "--duration-seconds", "--stop-file", "--result" };
        var observeOnly = new HashSet<string>(StringComparer.Ordinal)
        { "--admission-only", "--root", "--runtime", "--runner-exe", "--out", "--rounds", "--round-seconds", "--calibration-seconds", "--startup-seconds", "--shutdown-seconds", "--sample-seconds" };
        foreach (var key in values.Keys)
            if ((mode == RunnerMode.Observe ? driverOnly : observeOnly).Contains(key))
                throw new UsageException($"{key} 不适用于 {args[0]} 模式。");
        return mode == RunnerMode.Observe ? ParseObserve(values) : ParseDriver(values);
    }

    private static RunnerOptions ParseObserve(Dictionary<string, string?> values)
    {
        var options = new RunnerOptions { Mode = RunnerMode.Observe };
        options.AdmissionOnly = values.ContainsKey("--admission-only");
        if (options.AdmissionOnly && values.ContainsKey("--calibration-seconds"))
            throw new UsageException("--admission-only 不能与 --calibration-seconds 同时使用。");

        var rootArgument = Get(values, "--root") ?? Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(rootArgument)) throw new UsageException("缺少 --root 或 AGENT_LAUNCHER_TEST_ROOT。");
        options.Root = RequireAbsolute(rootArgument, "--root");
        EvidenceChecks.RequirePlainPath(options.Root);
        if (Directory.Exists(options.Root) || File.Exists(options.Root))
            throw new UsageException("测试根目录必须是不存在的新目录。");
        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(options.Root));
        if (!rootName.StartsWith("AgentLauncher-tests-", StringComparison.OrdinalIgnoreCase))
            throw new UsageException("测试根目录 basename 必须以 AgentLauncher-tests- 开头。");
        var environmentRoot = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            var normalizedEnvironmentRoot = RequireAbsolute(environmentRoot, "AGENT_LAUNCHER_TEST_ROOT");
            if (!string.Equals(options.Root, normalizedEnvironmentRoot, StringComparison.OrdinalIgnoreCase))
                throw new UsageException("--root 与 AGENT_LAUNCHER_TEST_ROOT 不一致。");
        }

        var runtimeArgument = Get(values, "--runtime") ?? AppContext.BaseDirectory;
        options.RuntimeDirectory = RequireAbsolute(runtimeArgument, "--runtime");
        RequireFile(Path.Combine(options.RuntimeDirectory, "AgentLauncher.Worker.exe"), "runtime AgentLauncher.Worker.exe");
        RequireFile(Path.Combine(options.RuntimeDirectory, "AgentLauncher.Core.dll"), "runtime AgentLauncher.Core.dll");
        RequireFile(Path.Combine(options.RuntimeDirectory, "AgentLauncher.Windows.dll"), "runtime AgentLauncher.Windows.dll");
        RequireFile(Path.Combine(options.RuntimeDirectory, "AgentLauncher.Worker.dll"), "runtime AgentLauncher.Worker.dll");

        var runner = Get(values, "--runner-exe") ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(runner)) throw new UsageException("无法确定 runner 可执行文件；请显式提供 --runner-exe。");
        options.RunnerExecutable = RequireAbsolute(runner, "--runner-exe");
        RequireFile(options.RunnerExecutable, "--runner-exe");
        if (!Path.GetFileName(options.RunnerExecutable).Equals("SoakRunner.exe", StringComparison.OrdinalIgnoreCase))
            throw new UsageException("--runner-exe 必须是构建出的 SoakRunner.exe apphost，不能是 dotnet.exe。");

        options.StartupSeconds = ReadInt(values, "--startup-seconds", 120, 20, 600);
        options.ShutdownSeconds = ReadInt(values, "--shutdown-seconds", 120, 20, 900);
        options.SampleSeconds = ReadInt(values, "--sample-seconds", 5, 1, 60);
        options.CommandTimeoutSeconds = ReadInt(values, "--command-timeout-seconds", 30, 5, 300);
        options.CycleIntervalMilliseconds = ReadInt(values, "--cycle-interval-ms", 1000, 0, 60000);
        options.OmpShutdownSeconds = ReadInt(values, "--omp-shutdown-seconds", 60, 5, 300);

        var calibration = values.ContainsKey("--calibration-seconds") ? ReadInt(values, "--calibration-seconds", 0, 10, 3600) : 0;
        if (options.AdmissionOnly)
        {
            if (values.ContainsKey("--rounds") || values.ContainsKey("--round-seconds"))
                throw new UsageException("准入检查不能接受轮次参数。");
            options.Rounds = 0;
            options.RoundSeconds = 0;
        }
        else if (calibration > 0)
        {
            options.CalibrationSeconds = calibration;
            if (values.ContainsKey("--rounds") && ReadInt(values, "--rounds", 1, 1, 3) != 1)
                throw new UsageException("校准模式只允许 1 个轮次。");
            if (values.ContainsKey("--round-seconds") && ReadInt(values, "--round-seconds", calibration, 10, 3600) != calibration)
                throw new UsageException("校准模式下 --round-seconds 必须等于 --calibration-seconds。");
            options.Rounds = 1;
            options.RoundSeconds = calibration;
        }
        else
        {
            var rounds = ReadInt(values, "--rounds", 3, 1, 3);
            var seconds = ReadInt(values, "--round-seconds", 1800, 10, 3600);
            if (rounds != 3 || seconds != 1800)
                throw new UsageException("长期稳定性模式必须是 3 个连续 1800 秒轮次；短轮次请使用 --calibration-seconds。");
            options.Rounds = 3;
            options.RoundSeconds = 1800;
        }

        var outputArgument = Get(values, "--out");
        if (outputArgument is null)
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            options.OutputDirectory = Path.Combine(options.Root, $"soak-output-{stamp}-{Guid.NewGuid().ToString("N")[..6]}");
        }
        else
        {
            options.OutputDirectory = RequireAbsolute(outputArgument, "--out");
            if (!JsonIo.IsUnder(options.Root, options.OutputDirectory))
                throw new UsageException("--out 必须位于测试根目录内。");
            if (File.Exists(options.OutputDirectory) || Directory.Exists(options.OutputDirectory))
                throw new UsageException("--out 不能预先存在。");
        }

        var omp = Get(values, "--omp");
        var node = Get(values, "--node");
        if (!options.AdmissionOnly)
        {
            if (string.IsNullOrWhiteSpace(omp)) throw new UsageException("缺少 --omp。");
            if (string.IsNullOrWhiteSpace(node)) throw new UsageException("缺少 --node。");
        }
        if (!string.IsNullOrWhiteSpace(omp))
        {
            options.OmpPath = RequireAbsolute(omp, "--omp");
            RequireFile(options.OmpPath, "--omp");
            if (!Path.GetExtension(options.OmpPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new UsageException("--omp 必须是 .exe。");
        }
        if (!string.IsNullOrWhiteSpace(node))
        {
            options.NodePath = RequireAbsolute(node, "--node");
            RequireFile(options.NodePath, "--node");
            if (!Path.GetExtension(options.NodePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new UsageException("--node 必须是 node.exe。");
        }

        var version = Get(values, "--omp-version");
        if (!options.AdmissionOnly)
        {
            if (string.IsNullOrWhiteSpace(version)) throw new UsageException("缺少 --omp-version（必须是显式测试过的精确版本）。");
            if (!Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.\-]+)?$", RegexOptions.CultureInvariant))
                throw new UsageException("--omp-version 必须是精确版本号，例如 18.18.9。");
            options.OmpVersion = version;
        }

        var git = Get(values, "--git");
        if (!string.IsNullOrWhiteSpace(git))
        {
            options.GitPath = RequireAbsolute(git, "--git");
            RequireFile(options.GitPath, "--git");
            if (!Path.GetExtension(options.GitPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new UsageException("--git 必须是 git.exe。");
        }
        return options;
    }

    private static RunnerOptions ParseDriver(Dictionary<string, string?> values)
    {
        var options = new RunnerOptions { Mode = RunnerMode.Driver };
        options.OmpPath = RequireAbsolute(Require(values, "--omp"), "--omp");
        RequireFile(options.OmpPath, "--omp");
        if (!Path.GetExtension(options.OmpPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new UsageException("--omp 必须是 .exe。");
        options.OmpVersion = Require(values, "--omp-version");
        if (!Regex.IsMatch(options.OmpVersion, @"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.\-]+)?$", RegexOptions.CultureInvariant))
            throw new UsageException("--omp-version 必须是精确版本号。");

        options.NodePath = RequireAbsolute(Require(values, "--node"), "--node");
        RequireFile(options.NodePath, "--node");
        if (!Path.GetExtension(options.NodePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new UsageException("--node 必须是 node.exe。");

        var git = Get(values, "--git");
        if (!string.IsNullOrWhiteSpace(git))
        {
            options.GitPath = RequireAbsolute(git, "--git");
            RequireFile(options.GitPath, "--git");
            if (!Path.GetExtension(options.GitPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new UsageException("--git 必须是 git.exe。");
        }

        options.StateRoot = RequireAbsolute(Require(values, "--state-root"), "--state-root");
        options.AgentDirectory = RequireAbsolute(Require(values, "--agent-dir"), "--agent-dir");
        options.HomeDirectory = RequireAbsolute(Require(values, "--home"), "--home");
        options.StopFile = RequireAbsolute(Require(values, "--stop-file"), "--stop-file");
        options.ResultFile = RequireAbsolute(Require(values, "--result"), "--result");
        foreach (var path in new[] { options.AgentDirectory, options.HomeDirectory, options.StopFile, options.ResultFile })
        {
            if (!JsonIo.IsUnder(options.StateRoot, path))
                throw new UsageException("driver 的 state/agent/home/stop/result 路径必须位于 --state-root 内。");
            EvidenceChecks.RequirePlainPath(path);
        }
        if (JsonIo.IsUnder(options.HomeDirectory, options.AgentDirectory) || JsonIo.IsUnder(options.AgentDirectory, options.HomeDirectory) ||
            options.StopFile.Equals(options.ResultFile, StringComparison.OrdinalIgnoreCase))
            throw new UsageException("driver 目录或 stop/result 不能重叠。");
        if (string.Equals(options.AgentDirectory, options.HomeDirectory, StringComparison.OrdinalIgnoreCase))
            throw new UsageException("--agent-dir 与 --home 必须是不同目录。");

        Require(values, "--duration-seconds");
        options.DurationSeconds = ReadInt(values, "--duration-seconds", 0, 1, 86400);
        options.CommandTimeoutSeconds = ReadInt(values, "--command-timeout-seconds", 30, 5, 300);
        options.CycleIntervalMilliseconds = ReadInt(values, "--cycle-interval-ms", 1000, 0, 60000);
        options.OmpShutdownSeconds = ReadInt(values, "--omp-shutdown-seconds", 60, 5, 300);
        return options;
    }

    private static Dictionary<string, string?> ParseValues(string[] args)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal) { "--admission-only" };
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "--admission-only", "--root", "--runtime", "--omp", "--omp-version", "--node", "--git",
            "--runner-exe", "--out", "--rounds", "--round-seconds", "--calibration-seconds",
            "--startup-seconds", "--shutdown-seconds", "--sample-seconds", "--command-timeout-seconds",
            "--cycle-interval-ms", "--omp-shutdown-seconds",
            "--state-root", "--agent-dir", "--home", "--duration-seconds", "--stop-file", "--result"
        };
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"无法识别参数: {argument}");
            var name = argument;
            string? value = null;
            var equals = argument.IndexOf('=');
            if (equals >= 0)
            {
                name = argument[..equals];
                value = argument[(equals + 1)..];
            }
            if (!known.Contains(name)) throw new UsageException($"无法识别参数: {name}");
            if (values.ContainsKey(name)) throw new UsageException($"参数重复: {name}");
            if (flags.Contains(name))
            {
                if (value is not null) throw new UsageException($"{name} 不接受值。");
                values[name] = null;
                continue;
            }
            if (value is null)
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new UsageException($"参数 {name} 缺少值。");
                value = args[++index];
            }
            values[name] = value;
        }
        return values;
    }

    private static string? Get(Dictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out var value) ? value : null;

    private static string Require(Dictionary<string, string?> values, string name)
    {
        var value = Get(values, name);
        if (string.IsNullOrWhiteSpace(value)) throw new UsageException($"缺少 {name}。");
        return value;
    }

    private static int ReadInt(Dictionary<string, string?> values, string name, int fallback, int minimum, int maximum)
    {
        var raw = Get(values, name);
        if (raw is null) return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
            throw new UsageException($"{name} 必须是 {minimum}..{maximum} 的整数。");
        return value;
    }

    private static string RequireAbsolute(string value, string name)
    {
        if (!Path.IsPathFullyQualified(value)) throw new UsageException($"{name} 必须是绝对路径。");
        return Path.GetFullPath(value);
    }

    private static void RequireFile(string path, string name)
    {
        if (!File.Exists(path)) throw new UsageException($"{name} 不存在: {path}");
    }
}
