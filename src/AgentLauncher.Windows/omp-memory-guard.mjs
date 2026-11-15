import { createConnection } from "node:net";
import { totalmem } from "node:os";

const unavailable = "启动器内存保护连接不可用，已暂停新增工具。请核对启动器仍在运行后重试；已有工具可以继续结束。";

function exchange(pipeName, request, connect, accept) {
    if (!/^AgentLauncher-memory-[0-9a-f]{32}$/.test(pipeName ?? "")) return Promise.resolve(undefined);
    return new Promise(resolve => {
        let socket;
        let text = "";
        let settled = false;
        const finish = result => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            socket?.destroy();
            resolve(result);
        };
        const fail = () => finish(undefined);
        // Never wait for another tool to drain here: Omp prepares a batch before executing it.
        const timer = setTimeout(fail, 1500);
        try {
            socket = connect(`\\\\.\\pipe\\${pipeName}`);
            socket.once("connect", () => socket.write(request));
            socket.setEncoding("utf8");
            socket.on("data", chunk => {
                text += chunk;
                if (Buffer.byteLength(text, "utf8") > 4096) return fail();
                const end = text.indexOf("\n");
                if (end < 0) return;
                try { finish(accept(text.slice(0, end))); } catch { fail(); }
            });
            socket.once("error", fail);
            socket.once("close", fail);
        } catch { fail(); }
    });
}

export function checkMemory(pipeName, connect = createConnection) {
    return exchange(pipeName, Buffer.from([1]), connect, line => {
        const result = JSON.parse(line);
        if (typeof result.allowed !== "boolean" || typeof result.reason !== "string") throw new TypeError("invalid guard response");
        return result;
    }).then(result => result ?? { allowed: false, reason: unavailable });
}

export function announceLoaded(pipeName, connect = createConnection) {
    // A load hello only proves the extension ran; tool calls still query the endpoint below.
    return exchange(pipeName, Buffer.from([2]), connect, line => {
        const result = JSON.parse(line);
        return typeof result.allowed === "boolean" && typeof result.reason === "string";
    }).then(result => result === true);
}

export function restrictConcurrency(lookup, scope, physicalBytes = totalmem()) {
    const concurrency = lookup("task.maxConcurrency");
    if (!concurrency || !scope) return false;
    // Small hosts cannot sustain two growing subagents alongside the parent and MCPs.
    // Use total RAM (stable per run), not momentary free RAM that would oscillate the scheduler.
    const cap = !Number.isFinite(physicalBytes) || physicalBytes <= 16 * 1024 ** 3 ? 1 : 2;
    const current = concurrency.get(scope);
    if (!Number.isFinite(current) || current <= 0 || current > cap) concurrency.override(scope, cap);
    return concurrency.get(scope) > 0 && concurrency.get(scope) <= cap;
}

export default function memoryGuard(pi, connect = createConnection) {
    // Announce load immediately with the same pipe protocol, retrying once on a fresh short timeout.
    // Registration below never waits for it; a missing hello only costs the Runner's fail-open disclosure.
    const announce = () => announceLoaded(process.env.AGENT_LAUNCHER_MEMORY_PIPE, connect)
        .then(ok => ok || announceLoaded(process.env.AGENT_LAUNCHER_MEMORY_PIPE, connect));
    void announce().catch(() => {});
    // Omp rebinds this factory for child sessions, so the same Job gate covers their tools too.
    // The endpoint belongs to the pane; it queries live kernel counters on every call.
    pi.on("tool_call", async event => {
        // These built-ins drain/cancel existing jobs and must remain usable under pressure.
        if (event.toolName === "wait") return;
        // Use Omp's per-task scheduler; do not shrink the shared async job manager,
        // whose parent jobs may themselves await child jobs occupying its slots.
        let settingsReady = false;
        try {
            const { lookup } = await import("@oh-my-pi/pi-coding-agent/config/registry");
            settingsReady = restrictConcurrency(lookup, pi.pi?.settings);
        } catch { /* An unsupported host must not silently execute unguarded tools. */ }
        if (!settingsReady) return { block: true, reason: "Omp 内存保护的并发设置未能生效，请使用已验证的 Omp 18.20.0 或核对扩展加载错误后重试。" };
        const result = await checkMemory(process.env.AGENT_LAUNCHER_MEMORY_PIPE);
        if (!result.allowed) return { block: true, reason: result.reason };
    });
}
