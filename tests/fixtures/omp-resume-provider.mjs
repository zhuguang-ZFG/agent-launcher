import { writeFileSync } from "node:fs";
import { AssistantMessageEventStream } from "@oh-my-pi/pi-ai";

export default function fixture(pi) {
    pi.registerProvider("launcher-resume-fixture", {
        baseUrl: "https://fixture.invalid/", apiKey: "unused-fixture", api: "launcher-resume-fixture-api",
        models: [{ id: "probe", name: "Resume probe", reasoning: false, input: ["text"],
            cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 32768, maxTokens: 1024 }],
        streamSimple(model, context) {
            const users = context.messages.filter(message => message.role === "user");
            const token = JSON.stringify(users[0]).match(/resume-check-[0-9a-f]{32}/)?.[0] ?? "missing-token";
            writeFileSync(process.env.LAUNCHER_RESUME_RESULT, JSON.stringify({ token, userTurns: users.length }));
            const message = { role: "assistant", api: model.api, provider: model.provider, model: model.id,
                content: [{ type: "text", text: token }],
                usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0,
                    cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
                stopReason: "stop", timestamp: Date.now() };
            const stream = new AssistantMessageEventStream();
            queueMicrotask(() => {
                stream.push({ type: "start", partial: message });
                stream.push({ type: "done", reason: "stop", message });stream.end(message);
            });
            return stream;
        }
    });
}
