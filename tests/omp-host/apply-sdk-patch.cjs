const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const { execFileSync } = require("node:child_process");

const [prefix, evidenceDirectory] = process.argv.slice(2);
if (!prefix || !evidenceDirectory) throw new Error("Requires the isolated SDK prefix and evidence directory");

const relativeSource = "node_modules/@oh-my-pi/pi-coding-agent/src/internal-urls/skill-protocol.ts";
const sourcePath = path.join(prefix, relativeSource);
const sourceBefore = fs.readFileSync(sourcePath);
const patchLines = [
    "diff --git a/node_modules/@oh-my-pi/pi-coding-agent/src/internal-urls/skill-protocol.ts b/node_modules/@oh-my-pi/pi-coding-agent/src/internal-urls/skill-protocol.ts",
    "--- a/node_modules/@oh-my-pi/pi-coding-agent/src/internal-urls/skill-protocol.ts",
    "+++ b/node_modules/@oh-my-pi/pi-coding-agent/src/internal-urls/skill-protocol.ts",
    "@@ -384,6 +384,6 @@ export class SkillProtocolHandler implements ProtocolHandler {",
    "-\tasync complete(): Promise<UrlCompletion[]> {",
    "-\t\treturn getActiveSkills().map(skill => ({",
    "+\tasync complete(_query?: string, context?: ResolveContext): Promise<UrlCompletion[]> {",
    "+\t\treturn (context?.skills ?? getActiveSkills()).map(skill => ({",
    " \t\t\tvalue: skill.name,",
    " \t\t\t...(skill.description ? { description: skill.description } : {}),",
    " \t\t}));",
    " \t}",
];
const patch = `${patchLines.join("\n")}\n`;
const patchPath = path.join(prefix, ".agent-launcher-omp.patch");
fs.writeFileSync(patchPath, patch, { flag: "wx" });
try {
    execFileSync("git", ["-C", prefix, "apply", "--check", patchPath], { stdio: "inherit", windowsHide: true });
    execFileSync("git", ["-C", prefix, "apply", patchPath], { stdio: "inherit", windowsHide: true });
} finally {
    fs.rmSync(patchPath, { force: true });
}

const sourceAfter = fs.readFileSync(sourcePath);
fs.writeFileSync(path.join(evidenceDirectory, "skill-completion-context.patch"), patch, { flag: "wx" });
fs.writeFileSync(path.join(evidenceDirectory, "sdk-patch.json"), JSON.stringify({
    OriginalSdkSourceSha256: crypto.createHash("sha256").update(sourceBefore).digest("hex"),
    PatchSha256: crypto.createHash("sha256").update(patch).digest("hex"),
    PatchedSdkSourceSha256: crypto.createHash("sha256").update(sourceAfter).digest("hex"),
}, null, 2));
