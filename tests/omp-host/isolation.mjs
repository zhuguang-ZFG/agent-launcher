// Bun-only, model-free capability experiment. Run through the disposable CI wrapper.
import assert from "node:assert/strict";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { Database } from "bun:sqlite";
import { ModelRegistry } from "@oh-my-pi/pi-coding-agent/config/model-registry";
import { Settings } from "@oh-my-pi/pi-coding-agent/config/settings";
import { createAgentSession } from "@oh-my-pi/pi-coding-agent/sdk";
import { AuthStorage, SqliteAuthCredentialStore } from "@oh-my-pi/pi-coding-agent/session/auth-storage";
import { SessionManager } from "@oh-my-pi/pi-coding-agent/session/session-manager";
import { AgentRegistry } from "@oh-my-pi/pi-coding-agent/registry/agent-registry";
import { EventBus } from "@oh-my-pi/pi-coding-agent/utils/event-bus";
import { SkillProtocolHandler } from "@oh-my-pi/pi-coding-agent/internal-urls/skill-protocol";
import { RuleProtocolHandler } from "@oh-my-pi/pi-coding-agent/internal-urls/rule-protocol";

const [resultPath, root] = process.argv.slice(2);
assert(resultPath && root, "Requires isolated evidence and home paths");
assert.equal(Bun.version, "1.4.4");
const cases = [];
const sessions = [];
const authStores = [];
const initialCwd = process.cwd();
function git(args, cwd = root) {
    const result = spawnSync("git", args, { cwd, encoding: "utf8", windowsHide: true });
    assert.equal(result.status, 0, result.stderr || String(result.error));
}
function check(id, passed, actual) {
    cases.push({ Id: id, Passed: !!passed, Actual: actual });
}
async function makeSession(label, cwd) {
    const agentDir = path.join(root, `agent-${label}`);
    await mkdir(agentDir, { recursive: true });
    const marker = `scope-${label}`;
    const skillPath = path.join(cwd, "SKILL.md");
    await writeFile(skillPath, `---\nname: scope\ndescription: ${marker}\n---\n${marker}\n`);
    await writeFile(path.join(cwd, "scope.txt"), marker);
    const rules = [{ name: "scope", description: marker, path: path.join(cwd, "RULE.md"), content: marker, alwaysApply: true,
        _source: { provider: "omp", providerName: "audit", path: cwd, level: "project" } }];
    await writeFile(rules[0].path, marker);
    const authStorage = new AuthStorage(new SqliteAuthCredentialStore(new Database(":memory:")));
    authStores.push(authStorage);
    const eventBus = new EventBus();
    const registry = new AgentRegistry();
    const manager = SessionManager.inMemory(cwd);
    const { session } = await createAgentSession({
        cwd, agentDir, authStorage,
        modelRegistry: new ModelRegistry(authStorage, path.join(agentDir, "models.yml")),
        settings: Settings.isolated({ "compaction.enabled": false }),
        sessionManager: manager, eventBus, agentRegistry: registry,
        agentId: label, agentDisplayName: label, bindProcessState: false,
        disableExtensionDiscovery: true, extensions: [],
        skills: [{ name: "scope", description: marker, filePath: skillPath, baseDir: cwd, source: "project" }],
        rules,
        contextFiles: [], promptTemplates: [], slashCommands: [], preloadedCustomToolPaths: [],
        enableMCP: false, enableLsp: false, enableIrc: false, skipPythonPreflight: true,
        toolNames: ["read"], restrictToolNames: true,
    });
    sessions.push(session);
    return { label, marker, cwd, session, eventBus, registry, manager, rules };
}
try {
    const repo = path.join(root, "repo");
    await mkdir(repo, { recursive: true });
    git(["init", repo]);
    git(["-c", "user.name=Launcher audit", "-c", "user.email=audit@example.invalid", "commit", "--allow-empty", "-m", "Isolated fixture"], repo);
    const worktrees = [path.join(root, "worktree-A"), path.join(root, "worktree-B")];
    for (let i = 0; i < worktrees.length; i++) git(["worktree", "add", "-b", `audit-${i}`, worktrees[i]], repo);
    const a = await makeSession("A", worktrees[0]);
    const skillProtocol = new SkillProtocolHandler();
    const before = await skillProtocol.complete("", { skills: a.session.skills });
    check("S5-A01 single-session skill baseline", before.some(item => item.description === a.marker), before);
    const b = await makeSession("B", worktrees[1]);
    check("S5-A02 distinct session identities", a.manager.getSessionId() !== b.manager.getSessionId(), [a.manager.getSessionId(), b.manager.getSessionId()]);
    check("S5-A03 isolated registries", !a.registry.get("B") && !b.registry.get("A"), [a.registry.list().map(r => r.id), b.registry.list().map(r => r.id)]);
    const reads = await Promise.all([a, b].map(async scope => {
        const result = await scope.session.getToolByName("read").execute(`read-${scope.label}`, { path: "scope.txt" });
        const text = result.content.filter(block => block.type === "text").map(block => block.text).join("\n");
        return { label: scope.label, passed: text.includes(scope.marker) && !text.includes(scope === a ? b.marker : a.marker) };
    }));
    check("S5-A04 concurrent relative read worktrees", reads.every(r => r.passed), reads);
    check("S5-A05 process cwd unchanged", process.cwd() === initialCwd, process.cwd());
    const eventsA = [], eventsB = [];
    const offA = a.eventBus.on("audit", value => eventsA.push(value));
    const offB = b.eventBus.on("audit", value => eventsB.push(value));
    a.eventBus.emit("audit", "A"); b.eventBus.emit("audit", "B");
    await Promise.resolve(); offA(); offB();
    check("S5-A06 event channels", eventsA.join() === "A" && eventsB.join() === "B", [eventsA, eventsB]);
    const completions = await Promise.all([a, b].map(async scope => ({
        scope,
        values: await skillProtocol.complete("", { skills: scope.session.skills }),
    })));
    for (const { scope, values } of completions)
        check(`S5-A07 skill completions ${scope.label}`, values.length === 1 && values[0].description === scope.marker, values);
    const activeCompletions = await skillProtocol.complete();
    check("S5-A11 unscoped completion preserves the active-session fallback",
        activeCompletions.length === 1 && activeCompletions[0].description === b.marker, activeCompletions);
    for (const scope of [a, b]) {
        const rules = await new RuleProtocolHandler().complete("", { rules: scope.rules });
        check(`S5-A08 rule completions ${scope.label}`, rules.some(item => item.description === scope.marker) && !rules.some(item => item.description === (scope === a ? b.marker : a.marker)), rules);
        const read = await scope.session.getToolByName("read").execute(`skill-${scope.label}`, { path: "skill://scope" });
        const text = read.content.filter(block => block.type === "text").map(block => block.text).join("\n");
        check(`S5-A09 skill reads ${scope.label}`, text.includes(scope.marker) && !text.includes(scope === a ? b.marker : a.marker), { OwnMarker: text.includes(scope.marker), ForeignMarker: text.includes(scope === a ? b.marker : a.marker) });
        const readRule = await scope.session.getToolByName("read").execute(`rule-${scope.label}`, { path: "rule://scope" });
        const ruleText = readRule.content.filter(block => block.type === "text").map(block => block.text).join("\n");
        check(`S5-A10 rule reads ${scope.label}`, ruleText.includes(scope.marker) && !ruleText.includes(scope === a ? b.marker : a.marker), { OwnMarker: ruleText.includes(scope.marker), ForeignMarker: ruleText.includes(scope === a ? b.marker : a.marker) });
    }
    // Keep the tool fixture honest: no model request, no provider key, no host enabled.
    assert.equal(await readFile(path.join(worktrees[0], "scope.txt"), "utf8"), "scope-A");
    for (const session of sessions.splice(0).reverse()) await session.dispose();
    for (const auth of authStores.splice(0)) auth.close();
    const isolated = cases.every(item => item.Passed);
    await writeFile(resultPath, JSON.stringify({ AuditCompleted: true, Runtime: Bun.version, SdkVersion: "18.19.0", At: new Date().toISOString(),
        ModelRequests: 0, Cases: cases, IsolationPassed: isolated, EligibleForHost: false,
        Verdict: isolated ? "limited-surface-passed-fault-and-benefit-gates-pending" : "unsupported-cross-session-state",
        Remaining: ["permissions", "environment-and-auth", "cancellation-and-late-results", "host-crash-and-pipe-loss", "backpressure", "corrupt-session-and-leases", "five-10-minute-benefit-rounds", "MCP-per-service-isolation"] }, null, 2));
    console.log(JSON.stringify({ AuditCompleted: true, IsolationPassed: isolated, EligibleForHost: false }));
} catch (error) {
    await writeFile(resultPath, JSON.stringify({ AuditCompleted: false, Runtime: Bun.version, SdkVersion: "18.19.0", EligibleForHost: false, Error: String(error), Stack: error.stack, Cases: cases }, null, 2));
    throw error;
} finally {
    for (const session of sessions.reverse()) await session.dispose();
    for (const auth of authStores) auth.close();
}
