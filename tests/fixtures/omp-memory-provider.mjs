import { appendFileSync, writeFileSync } from "node:fs";
import { AssistantMessageEventStream } from "@oh-my-pi/pi-ai";
import { lookup } from "@oh-my-pi/pi-coding-agent/config/registry";

// Model-free provider: exercise Omp's real agent-loop dispatch, not its bypassing RPC bash endpoint.
export default function fixture(pi) {
    pi.registerTool({
        name: "launcher_memory_probe", label: "Memory guard probe", description: "Isolated test marker",
        parameters: pi.typebox.Type.Object({}),
        async execute() {
            appendFileSync(process.env.LAUNCHER_GUARD_EXECUTED, "executed\n");
            return { content: [{ type: "text", text: "probe ran" }], details: {} };
        }
    });
    pi.registerProvider("launcher-memory-fixture", {
        baseUrl: "https://fixture.invalid/", apiKey: "unused-fixture", api: "launcher-memory-fixture-api",
        models: [{ id: "probe", name: "Guard probe", reasoning: false, input: ["text"],
            cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 32768, maxTokens: 1024 }],
        streamSimple(model, context) {
            const stream = new AssistantMessageEventStream();
            const results = context.messages.filter(message => message.role === "toolResult");
            const parent = process.env.LAUNCHER_GUARD_SCOPE === "sub" && !JSON.stringify(context.systemPrompt).includes("LAUNCHER_MEMORY_CHILD");
            const finished = results.length >= (parent ? 1 : 2);
            const call = parent
                ? { type: "toolCall", id: "spawn-probe", name: "task", arguments: { agent: "memory-child", task: "Run the memory guard fixture.", solutionSpace: "Only the registered probe tool." } }
                : { type: "toolCall", id: `probe-${results.length}`, name: "launcher_memory_probe", arguments: {} };
            const message = { role: "assistant", api: model.api, provider: model.provider, model: model.id,
                content: finished ? [{ type: "text", text: "fixture complete" }] : [call],
                usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0,
                    cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
                stopReason: finished ? "stop" : "toolUse", timestamp: Date.now() };
            queueMicrotask(() => {
                stream.push({ type: "start", partial: message });
                if (!finished) {
                    stream.push({ type: "toolcall_start", contentIndex: 0, partial: message });
                    stream.push({ type: "toolcall_delta", contentIndex: 0, delta: "{}", partial: message });
                    stream.push({ type: "toolcall_end", contentIndex: 0, toolCall: call, partial: message });
                }
                if (finished) writeFileSync(process.env.LAUNCHER_GUARD_RESULTS + (parent ? ".parent" : ""), JSON.stringify({ results, concurrency: lookup("task.maxConcurrency").get(pi.pi.settings) }));
                stream.push({ type: "done", reason: message.stopReason, message });
                stream.end(message);
            });
            return stream;
        }
    });
}
