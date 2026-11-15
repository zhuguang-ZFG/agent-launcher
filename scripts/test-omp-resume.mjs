import { execFileSync } from "node:child_process";
import { mkdirSync, writeFileSync, readFileSync, readdirSync } from "node:fs";
import { resolve, join } from "node:path";
import { fileURLToPath } from "node:url";
import { randomUUID } from "node:crypto";
import assert from "node:assert/strict";

const repository = fileURLToPath(new URL("../", import.meta.url));
const omp = process.argv[2];
if (!omp) throw new Error("Pass the installed omp.exe path.");
const root = resolve(repository, "artifacts", "AgentLauncher-tests-omp-resume-" + randomUUID());
const config = join(root, "config"), sessions = join(root, "sessions");
mkdirSync(config, { recursive: true });mkdirSync(sessions);
writeFileSync(join(config, "config.yml"), "disabledProviders: [ollama, lm-studio, llama.cpp]\n");
// Omp's daemon runtime is separate from PI_CODING_AGENT_DIR. This offline fixture
// uses a disposable Windows profile so neither path touches the user's sessions.
const profile = join(root, "profile");mkdirSync(profile);
const env = { ...process.env, USERPROFILE: profile, PI_CODING_AGENT_DIR: config, OMP_PROFILE: "" };
const version = execFileSync(omp, ["--version"], { env, encoding: "utf8", windowsHide: true, timeout: 10000 }).trim();
assert.match(version, /\b18\.20\.0\b/);
const common = ["--no-extensions", "--no-skills", "--no-rules", "--no-context-files", "--no-lsp", "--no-title", "--no-tools",
    "--session-dir", sessions, "--extension", resolve(repository, "tests/fixtures/omp-resume-provider.mjs"),
    "--model", "launcher-resume-fixture/probe", "--mode", "json"];
const token = "resume-check-" + randomUUID().replaceAll("-", "");
const results = [];
let session;
for (const [index, mode] of ["new", "resume", "continue"].entries()) {
    const selector = mode === "new" ? [] : mode === "resume" ? ["--resume", session] : ["--continue"];
    const prompt = mode === "new" ? `Remember ${token}.` : "Return the verification token from our first user message.";
    const output = join(root, mode + ".result.json");
    const stdout = execFileSync(omp, [...common, ...selector, "-p", prompt], {
        cwd: root, env: { ...env, LAUNCHER_RESUME_RESULT: output }, encoding: "utf8", windowsHide: true, timeout: 60000, maxBuffer: 1024 * 1024
    });
    writeFileSync(join(root, mode + ".stdout.jsonl"), stdout);
    const actual = JSON.parse(readFileSync(output, "utf8"));
    assert.equal(actual.token, token, "saved conversation lost");
    assert.equal(actual.userTurns, index + 1, "resume silently started or forked a different conversation");
    const files = readdirSync(sessions, { recursive: true }).filter(file => file.endsWith(".jsonl"));
    assert.equal(files.length, 1, "unexpected new session");
    session = join(sessions, files[0]);results.push({ mode, ...actual });
}
writeFileSync(join(root, "result.json"), JSON.stringify({ passed: true, version, providerNetwork: false, results }));
console.log("PASS real Omp persisted resume and continue: " + root);
