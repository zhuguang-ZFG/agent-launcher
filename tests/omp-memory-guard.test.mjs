import test from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import memoryGuard, { checkMemory, restrictConcurrency } from "../src/AgentLauncher.Windows/omp-memory-guard.mjs";

const pipe = "AgentLauncher-memory-" + "a".repeat(32);
function transport(send) {
    return () => {
        const socket = new EventEmitter();
        socket.setEncoding = () => {};
        socket.destroy = () => {};
        socket.write = request => { assert.deepEqual(request, Buffer.from([1])); queueMicrotask(() => send(socket)); };
        queueMicrotask(() => socket.emit("connect"));
        return socket;
    };
}

test("ACC-R07 fresh allow and deny responses survive fragmented delivery", async () => {
    for (const allowed of [true, false]) {
        const result = await checkMemory(pipe, transport(socket => {
            socket.emit("data", '{"allowed":');
            socket.emit("data", `${allowed},"reason":"中文内存余量"}\n`);
        }));
        assert.deepEqual(result, { allowed, reason: "中文内存余量" });
    }
});
test("ACC-R07 missing endpoint, closed peer, malformed and oversized responses block", async () => {
    assert.equal((await checkMemory(undefined)).allowed, false);
    for (const send of [s => s.emit("close"), s => s.emit("error", new Error("gone")),
        s => s.emit("data", "{}\n"), s => s.emit("data", "x".repeat(4097)), s => s.emit("data", "not-json\n")]) {
        assert.equal((await checkMemory(pipe, transport(send))).allowed, false);
    }
});
test("ACC-R07 a silent endpoint has a bounded wait", async () => {
    const start = Date.now();
    assert.equal((await checkMemory(pipe, transport(() => {}))).allowed, false);
    assert.ok(Date.now() - start < 3000);
});
test("ACC-R07 scheduler cap preserves a user's lower concurrency and does not touch async jobs", () => {
    for (const physicalBytes of [15.2 * 1024 ** 3, 16 * 1024 ** 3, 32 * 1024 ** 3, NaN]) {
    const cap = !Number.isFinite(physicalBytes) || physicalBytes <= 16 * 1024 ** 3 ? 1 : 2;
    for (const initial of [0, 1, 2, 4, 32]) {
        let current = initial;
        let writes = 0;
        const scope = {};
        const lookup = id => {
            assert.equal(id, "task.maxConcurrency");
            return { get: s => { assert.equal(s, scope); return current; }, override: (s, value) => { assert.equal(s, scope); current = value; writes++; } };
        };
        assert.equal(restrictConcurrency(lookup, scope, physicalBytes), true);
        assert.equal(current, initial === 1 ? 1 : cap);
        assert.equal(writes, initial > 0 && initial <= cap ? 0 : 1);
    }
    }
    assert.equal(restrictConcurrency(() => undefined, {}), false);
});
test("ACC-R07 extension load announces a 0x02 hello before tool_call registration", async () => {
    const previous = process.env.AGENT_LAUNCHER_MEMORY_PIPE;
    process.env.AGENT_LAUNCHER_MEMORY_PIPE = pipe;
    try {
        const requests = [];
        const registered = [];
        const pi = { on: (name, handler) => registered.push({ name, handler }) };
        memoryGuard(pi, () => {
            const socket = new EventEmitter();
            socket.setEncoding = () => {};
            socket.destroy = () => {};
            socket.write = request => {
                requests.push(Buffer.from(request));
                queueMicrotask(() => socket.emit("data", '{"allowed":true,"reason":"内存保护已加载"}\n'));
            };
            queueMicrotask(() => socket.emit("connect"));
            return socket;
        });
        assert.deepEqual(registered.map(entry => entry.name), ["tool_call"]);
        assert.equal(typeof registered[0].handler, "function");
        await new Promise(resolve => setTimeout(resolve, 20));
        assert.deepEqual(requests, [Buffer.from([2])]);
    } finally {
        if (previous === undefined) delete process.env.AGENT_LAUNCHER_MEMORY_PIPE;
        else process.env.AGENT_LAUNCHER_MEMORY_PIPE = previous;
    }
});
test("ACC-R07 a failed 0x02 hello retries once and never blocks tool_call registration", async () => {
    const previous = process.env.AGENT_LAUNCHER_MEMORY_PIPE;
    process.env.AGENT_LAUNCHER_MEMORY_PIPE = pipe;
    try {
        let attempts = 0;
        const registered = [];
        memoryGuard({ on: name => registered.push(name) }, () => {
            attempts++;
            const socket = new EventEmitter();
            socket.setEncoding = () => {};
            socket.destroy = () => {};
            socket.write = () => queueMicrotask(() => socket.emit("error", new Error("guard endpoint gone")));
            queueMicrotask(() => socket.emit("connect"));
            return socket;
        });
        assert.deepEqual(registered, ["tool_call"]);
        await new Promise(resolve => setTimeout(resolve, 20));
        assert.ok(attempts >= 1 && attempts <= 2, `hello attempted ${attempts} times`);
    } finally {
        if (previous === undefined) delete process.env.AGENT_LAUNCHER_MEMORY_PIPE;
        else process.env.AGENT_LAUNCHER_MEMORY_PIPE = previous;
    }
});
