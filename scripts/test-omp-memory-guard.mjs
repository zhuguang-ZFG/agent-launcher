import { createServer } from "node:net";
import { spawn, execFileSync } from "node:child_process";
import { mkdirSync, writeFileSync, readFileSync } from "node:fs";
import { resolve, join } from "node:path";
import { fileURLToPath } from "node:url";
import { randomUUID } from "node:crypto";
import assert from "node:assert/strict";
import { totalmem } from "node:os";

const root = fileURLToPath(new URL("../", import.meta.url));
const omp = process.argv[2];
const sub = process.argv[3] === "sub";
if (!omp) throw new Error("Pass the installed omp.exe path; this test installs no runtime.");
const output = resolve(root, "artifacts", "omp-memory-guard-" + randomUUID());
mkdirSync(output, { recursive: true });
let requests = 0;
let hellos = 0;
const pipe = "AgentLauncher-memory-" + randomUUID().replaceAll("-", "");
const server = createServer(socket => {
    socket.once("data", data => {
        // 0x02 is the extension's load announcement, not a tool check: it must not consume pressure counts.
        if (data[0] === 2) { hellos++; socket.end(JSON.stringify({ allowed: true, reason: "内存保护已加载" }) + "\n"); return; }
        assert.equal(data[0], 1);
        requests++;
        socket.end(JSON.stringify({ allowed: requests !== (sub ? 2 : 1), reason: "fixture memory pressure: retry sequentially" }) + "\n");
    });
});
const config = join(output, "config");
mkdirSync(config);
writeFileSync(join(config, "config.yml"), "disabledProviders: [ollama, lm-studio, llama.cpp]\ntask:\n  maxConcurrency: 4\n  batch: false\nasync:\n  enabled: false\n");
if (sub) {
    const agents = join(output, ".omp", "agents");
    mkdirSync(agents, { recursive: true });
    writeFileSync(join(agents, "memory-child.md"), "---\nname: memory-child\ndescription: Isolated memory guard child\nmodel: launcher-memory-fixture/probe\nblocking: true\ntools: [launcher_memory_probe]\n---\nLAUNCHER_MEMORY_CHILD\nRun the model-free probe.\n");
}
const environment = { ...process.env, PI_CODING_AGENT_DIR: config, AGENT_LAUNCHER_MEMORY_PIPE: pipe,
    LAUNCHER_GUARD_SCOPE: sub ? "sub" : "main",
    LAUNCHER_GUARD_RESULTS: join(output, "results.json"), LAUNCHER_GUARD_EXECUTED: join(output, "executed.txt") };
const version = execFileSync(omp, ["--version"], { env: environment, encoding: "utf8", windowsHide: true, timeout: 10000 }).trim();
assert.match(version, /\b18\.20\.0\b/, "this adapter's real-host contract is pinned to Omp 18.20.0");
const args = ["--no-extensions", "--no-skills", "--no-rules", "--no-context-files", "--no-lsp", "--no-session", "--no-title",
    "--extension", resolve(root, "src/AgentLauncher.Windows/omp-memory-guard.mjs"),
    "--extension", resolve(root, "tests/fixtures/omp-memory-provider.mjs"),
    "--model", "launcher-memory-fixture/probe", "--mode", "json", "-p", "Run the isolated guard fixture."];
let stdout = "", stderr = "";
await new Promise(resolve => server.listen(`\\\\.\\pipe\\${pipe}`, resolve));
const child = spawn(omp, args, { cwd: output, env: environment, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
const timer = setTimeout(() => child.kill(), 45000);
child.stdout.on("data", chunk => { if (stdout.length < 256 * 1024) stdout += chunk; });
child.stderr.on("data", chunk => { if (stderr.length < 256 * 1024) stderr += chunk; });
try {
    const code = await new Promise((resolve, reject) => { child.once("error", reject); child.once("exit", resolve); });
    writeFileSync(join(output, "stdout.log"), stdout);
    writeFileSync(join(output, "stderr.log"), stderr);
    assert.equal(code, 0, stderr);
    const { results, concurrency } = JSON.parse(readFileSync(environment.LAUNCHER_GUARD_RESULTS, "utf8"));
    assert.equal(concurrency, totalmem() <= 16 * 1024 ** 3 ? 1 : 2, "Omp did not apply the runtime concurrency cap");
    assert.equal(requests, sub ? 3 : 2, "real dispatch did not reach the guard for each tool");
    assert.equal(results.length, 2);
    assert.equal(results[0].isError, true, "pressure did not block the first tool");
    assert.match(JSON.stringify(results[0]), /fixture memory pressure/);
    assert.equal(results[1].isError, false, "tool did not recover when pressure cleared");
    assert.equal(readFileSync(environment.LAUNCHER_GUARD_EXECUTED, "utf8"), "executed\n");
    assert.match(readFileSync(join(config, "config.yml"), "utf8"), /maxConcurrency: 4/, "runtime cap mutated stored settings");
    assert.ok(hellos >= 1, "extension dispatch never announced a load hello");
    if (sub) {
        const parent = JSON.parse(readFileSync(environment.LAUNCHER_GUARD_RESULTS + ".parent", "utf8"));
        assert.equal(parent.results[0].isError, false, "child task did not complete");
    }
    writeFileSync(join(output, "result.json"), JSON.stringify({ passed: true, requests, hellos, version, scope: sub ? "real task child agent-loop" : "real root agent-loop", providerNetwork: false }));
    console.log(`PASS real Omp: pressure blocked one tool; recovery executed once; Agent exited normally. Evidence: ${output}`);
} finally { clearTimeout(timer); server.close(); }
