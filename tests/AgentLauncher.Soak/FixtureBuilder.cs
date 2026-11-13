using System.Globalization;
using System.Text;

namespace SoakRunner;

internal sealed class FixtureWorkspace
{
    public required string Directory { get; init; }
    public required string WorkloadScript { get; init; }
    public required string GitPath { get; init; }
}

internal static class FixtureBuilder
{
    public static FixtureWorkspace Create(string root, string outputDirectory, string workspaceDirectory, string gitPath)
    {
        if (!Path.IsPathFullyQualified(gitPath) || !Path.GetExtension(gitPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("fixture git 必须是显式 git.exe。");
        if (!File.Exists(gitPath)) throw new FileNotFoundException("fixture git 不存在。", gitPath);
        var workspace = Path.GetFullPath(workspaceDirectory);
        Directory.CreateDirectory(workspace);
        var gitHome = Path.Combine(outputDirectory, "fixture-git-home");
        var gitAgent = Path.Combine(outputDirectory, "fixture-git-agent");
        Directory.CreateDirectory(gitHome);
        Directory.CreateDirectory(gitAgent);
        var environment = ProcessTools.BuildChildEnvironment(
            testRoot: root,
            homeDirectory: gitHome,
            agentDirectory: gitAgent,
            extraPathDirectories: ProcessTools.ExtraPathDirectoriesForTool(gitPath),
            explicitVariables: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["GIT_AUTHOR_NAME"] = "Soak Fixture",
                ["GIT_AUTHOR_EMAIL"] = "soak@example.invalid",
                ["GIT_COMMITTER_NAME"] = "Soak Fixture",
                ["GIT_COMMITTER_EMAIL"] = "soak@example.invalid",
                ["GIT_AUTHOR_DATE"] = "2026-01-01T00:00:00Z",
                ["GIT_COMMITTER_DATE"] = "2026-01-01T00:00:00Z"
            });

        var fixture = BuildFixture();
        var search = BuildSearch(out var searchCount);
        var fixtureBytes = new UTF8Encoding(false).GetBytes(fixture);
        var fixtureSha = JsonIo.Sha256Text(fixture);
        File.WriteAllBytes(Path.Combine(workspace, "fixture.txt"), fixtureBytes);
        File.WriteAllBytes(Path.Combine(workspace, "search.txt"), new UTF8Encoding(false).GetBytes(search));
        var workloadPath = Path.Combine(workspace, "workload.js");
        File.WriteAllText(workloadPath, BuildWorkloadScript(fixtureSha, searchCount), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(workspace, ".gitignore"), ".soak-work/\n", new UTF8Encoding(false));

        RunGit(environment, workspace, gitPath, ["init", "-q"]);
        RunGit(environment, workspace, gitPath, ["config", "core.autocrlf", "false"]);
        RunGit(environment, workspace, gitPath, ["config", "commit.gpgsign", "false"]);
        RunGit(environment, workspace, gitPath, ["add", "--", "fixture.txt", "search.txt", "workload.js", ".gitignore"]);
        RunGit(environment, workspace, gitPath, ["commit", "-q", "-m", "soak fixture"]);
        var status = RunGit(environment, workspace, gitPath, ["status", "--porcelain", "--untracked-files=all"]);
        if (status.StdOut.Trim().Length != 0)
            throw new InvalidOperationException("fixture 工作区在提交后不干净: " + JsonIo.Bounded(status.StdOut, 400));
        if (!Directory.Exists(Path.Combine(workspace, ".git")))
            throw new InvalidOperationException("fixture 工作区缺少 .git。");
        return new FixtureWorkspace { Directory = workspace, WorkloadScript = workloadPath, GitPath = gitPath };
    }

    private static string BuildFixture()
    {
        var text = new StringBuilder();
        text.Append("SOAK_FIXTURE_V1\n");
        for (var index = 0; index < 128; index++)
            text.Append("row-").Append(index.ToString("D4", CultureInfo.InvariantCulture))
                .Append(": deterministic read/write/hash fixture payload for model-free rpc soak\n");
        return text.ToString();
    }

    private static string BuildSearch(out int count)
    {
        var text = new StringBuilder();
        count = 0;
        for (var index = 0; index < 8192; index++)
        {
            text.Append("row-").Append(index.ToString("D4", CultureInfo.InvariantCulture)).Append(": ");
            if (index % 211 == 0)
            {
                text.Append("SOAK_SEARCH_TOKEN bounded-search-hit\n");
                count++;
            }
            else
            {
                text.Append("bounded filler content for the search workload\n");
            }
        }
        return text.ToString();
    }

    private static string BuildWorkloadScript(string fixtureSha, int searchCount) => """
"use strict";
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { execFileSync } = require("child_process");

const root = process.cwd();
const mode = process.argv[2];
const markerArg = process.argv[3];
const nonce = process.argv[4];

function fail(message) {
  process.stderr.write("SOAK_FAIL " + mode + ": " + message + "\n");
  process.exit(1);
}
function mark() {
  if (!markerArg || markerArg.indexOf("..") !== -1 || path.isAbsolute(markerArg)) fail("bad marker");
  if (!nonce || nonce.length < 16) fail("bad nonce");
  const target = path.join(root, markerArg);
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.writeFileSync(target, nonce, "utf8");
}
if (mode === "read") {
  const text = fs.readFileSync(path.join(root, "fixture.txt"), "utf8");
  if (!text.includes("SOAK_FIXTURE_V1") || text.length < 512) fail("fixture read");
  mark(); process.stdout.write("read ok\n");
} else if (mode === "write") {
  const work = path.join(root, ".soak-work");
  fs.mkdirSync(work, { recursive: true });
  const target = path.join(work, "write.bin");
  const payload = Buffer.alloc(65536);
  for (let i = 0; i < payload.length; i++) payload[i] = (i * 31 + 7) & 0xff;
  const fd = fs.openSync(target, "w");
  try { fs.writeSync(fd, payload, 0, payload.length, 0); fs.fsyncSync(fd); } finally { fs.closeSync(fd); }
  const back = fs.readFileSync(target);
  if (!back.equals(payload)) fail("write verify");
  mark(); process.stdout.write("write ok\n");
} else if (mode === "hash") {
  const digest = crypto.createHash("sha256").update(fs.readFileSync(path.join(root, "fixture.txt"))).digest("hex");
  if (digest !== "__FIXTURE_SHA__") fail("hash mismatch " + digest);
  mark(); process.stdout.write("hash ok\n");
} else if (mode === "search") {
  const text = fs.readFileSync(path.join(root, "search.txt"), "utf8");
  let count = 0, at = 0;
  for (;;) {
    const next = text.indexOf("SOAK_SEARCH_TOKEN", at);
    if (next < 0) break;
    count++; at = next + 1;
  }
  if (count !== __SEARCH_COUNT__) fail("search count " + count);
  mark(); process.stdout.write("search ok\n");
} else if (mode === "git-status") {
  let status;
  try {
    status = execFileSync("git", ["status", "--porcelain", "--untracked-files=all"], { cwd: root, encoding: "utf8", maxBuffer: 1048576 });
  } catch (error) {
    fail("git status: " + String(error.message).slice(0, 200));
  }
  if (status.trim() !== "") fail("dirty " + status.slice(0, 200));
  let diff;
  try {
    diff = execFileSync("git", ["diff", "--exit-code", "--stat"], { cwd: root, encoding: "utf8", maxBuffer: 1048576 });
  } catch (error) {
    fail("git diff: " + String(error.message).slice(0, 200));
  }
  if (diff.trim() !== "") fail("diff " + diff.slice(0, 200));
  mark(); process.stdout.write("git ok\n");
} else {
  fail("unknown mode");
}
""".Replace("__FIXTURE_SHA__", fixtureSha, StringComparison.Ordinal)
   .Replace("__SEARCH_COUNT__", searchCount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static BoundedResult RunGit(ChildEnvironment environment, string workspace, string gitPath, string[] arguments)
    {
        var startInfo = ProcessTools.CreateStartInfo(gitPath, arguments, workspace);
        startInfo.Environment.Clear();
        foreach (var pair in environment.Variables) startInfo.Environment[pair.Key] = pair.Value;
        var result = ProcessTools.RunBounded(startInfo, TimeSpan.FromSeconds(30), 64 * 1024);
        if (result.TimedOut) throw new InvalidOperationException("fixture git 超时: " + string.Join(" ", arguments));
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"fixture git 失败({result.ExitCode}): {string.Join(" ", arguments)}: {JsonIo.Bounded(result.StdErr, 400)}");
        if (result.Truncated) throw new InvalidOperationException("fixture git 输出超出边界。");
        return result;
    }
}
