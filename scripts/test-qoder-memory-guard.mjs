import { createServer } from "node:net";
import { spawn, execFileSync } from "node:child_process";
import { mkdirSync, writeFileSync, readFileSync } from "node:fs";
import { resolve, join } from "node:path";
import { fileURLToPath } from "node:url";
import { randomUUID } from "node:crypto";
import assert from "node:assert/strict";

// No login, model request, or user's Qoder settings are used by this contract test.
const [qoder, runtime] = process.argv.slice(2);
if (!qoder || !runtime) throw new Error("Pass installed qodercli.exe and a complete test runtime directory.");
const root = resolve(fileURLToPath(new URL("../", import.meta.url)), "artifacts", "AgentLauncher-tests-qoder-" + randomUUID());
const worker = resolve(runtime, "AgentLauncher.Worker.exe");
const config = join(root, "config");
mkdirSync(config, { recursive: true });
const env = { ...process.env, AGENT_LAUNCHER_TEST_ROOT: root };
delete env.QODER_PERSONAL_ACCESS_TOKEN;
const runCli = args => execFileSync(qoder, ["--config-dir", config, ...args], { cwd: root, env, windowsHide: true, encoding: "utf8", timeout: 15000 });
const version = runCli(["--version"]).trim();
assert.equal(version, "1.1.89", "the real-host adapter check is pinned to Qoder 1.1.89");
const plugin = execFileSync(resolve(runtime, "AgentLauncher.Tests.exe"), ["--qoder-plugin-fixture", worker, root], { cwd: root, env, windowsHide: true, encoding: "utf8", timeout: 10000 }).trim();
const hooksPath = join(plugin, "hooks", "hooks.json");
const hooks = JSON.parse(readFileSync(hooksPath, "utf8"));
const hook = hooks.hooks.PreToolUse[0].hooks[0];
assert.equal(hook.command, worker);
assert.deepEqual(hook.args, ["qoder-memory-hook"]);
const validation = runCli(["plugins", "validate", plugin]);
writeFileSync(join(root, "plugin-validation.txt"), validation);
assert.match(validation, /valid and ready to install/);
const other = join(root, "user-plugin");
mkdirSync(join(other, ".qoder-plugin"), { recursive: true });
mkdirSync(join(other, "hooks"));
writeFileSync(join(other, ".qoder-plugin", "plugin.json"), JSON.stringify({ name: "user-fixture", version: "1.0.0", description: "existing hook fixture" }));
writeFileSync(join(other, "hooks", "hooks.json"), JSON.stringify({ hooks: { PreToolUse: [{ hooks: [{ type: "command", command: process.execPath, args: ["-e", "process.exit(0)"] }] }] } }));
const originalSettings = JSON.stringify({ hooks: { PreToolUse: [{ matcher: "Read", hooks: [{ type: "command", command: process.execPath, args: ["-e", "process.exit(0)"] }] }] } });
writeFileSync(join(config, "settings.json"), originalSettings);
const listing = runCli(["--plugin-dir", plugin, "--plugin-dir", other, "plugins", "list", "--json"]);
writeFileSync(join(root, "plugin-list.json"), listing);
assert.match(listing, /agent-launcher-memory/);
assert.match(listing, /user-fixture/);
assert.equal(readFileSync(join(config, "settings.json"), "utf8"), originalSettings);

const pipe = "AgentLauncher-memory-" + randomUUID().replaceAll("-", "");
const responses = [true, false, true];
let checks = 0, hellos = 0;
const server = createServer(socket => {
    socket.on("error", () => {});
    socket.once("data", data => {
        assert.equal(data.length, 1, "hook leaked input instead of sending one protocol byte");
        if (data[0] === 2) { hellos++;socket.end('{"allowed":true,"reason":"loaded"}\n');return; }
        assert.equal(data[0], 1);
        socket.end(JSON.stringify({ allowed: responses[checks++], reason: "fixture pressure" }) + "\n");
    });
});
await new Promise(done => server.listen(`\\\\.\\pipe\\${pipe}`, done));
async function invoke(input, endpoint = pipe, closeInput = true) {
    const child = spawn(hook.command, hook.args, { cwd: root, env: { ...env, AGENT_LAUNCHER_MEMORY_PIPE: endpoint }, windowsHide: true, stdio: ["pipe", "pipe", "pipe"] });
    let stdout = "", stderr = "";
    child.stdout.on("data", x => { stdout += x; });child.stderr.on("data", x => { stderr += x; });
    if (closeInput) child.stdin.end(JSON.stringify(input));
    const timer = setTimeout(() => child.kill(), 5000);
    try {
        const code = await new Promise((done, reject) => { child.once("error", reject);child.once("exit", done); });
        assert.equal(code, 0, "hook did not return a structured result: " + stderr);
        assert.equal(stderr, "");
        return JSON.parse(stdout);
    } finally { clearTimeout(timer);child.stdin.destroy(); }
}
try {
    const hello = await invoke({ hook_event_name: "SessionStart" });
    assert.ok(hello.systemMessage);
    const input = { hook_event_name: "PreToolUse", tool_name: "Bash", tool_input: { command: "fixture-private-command" } };
    assert.deepEqual(await invoke(input), {});
    const denied = await invoke(input);
    assert.equal(denied.hookSpecificOutput.permissionDecision, "deny");
    assert.equal(denied.hookSpecificOutput.hookEventName, "PreToolUse");
    assert.doesNotMatch(JSON.stringify(denied), /fixture-private-command/);
    assert.deepEqual(await invoke(input), {});
    assert.equal((await invoke(input, "")).hookSpecificOutput.permissionDecision, "deny");
    assert.equal((await invoke(input, pipe, false)).hookSpecificOutput.permissionDecision, "deny", "silent stdin exceeded the hook deadline");
    assert.equal(checks, 3);assert.equal(hellos, 1);
    writeFileSync(join(root, "result.json"), JSON.stringify({ passed: true, version, pluginValidation: true, additivePluginListing: true, workerProtocol: true, checks, hellos, modelCalls: 0, authenticatedToolDispatch: "not tested: isolated Qoder requires login" }, null, 2));
    console.log("PASS Qoder plugin discovery and real Worker hook protocol. No authenticated tool dispatch claimed. Evidence: " + root);
} finally { server.close(); }
