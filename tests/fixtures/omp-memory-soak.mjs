import { AssistantMessageEventStream } from '@oh-my-pi/pi-ai';
import { spawn } from 'node:child_process';

// Uses the real Omp agent loop; only model generation is deterministic and offline.
export default function fixture(pi) {
    pi.registerTool({
        name: 'launcher_memory_workload', label: 'Memory workload', description: 'Run one minute of isolated file validation.',
        parameters: pi.typebox.Type.Object({ step: pi.typebox.Type.Number() }),
        async execute(_id, args) {
            const child = spawn(process.env.LAUNCHER_SOAK_NODE, [process.env.LAUNCHER_SOAK_WORKLOAD, process.env.LAUNCHER_SOAK_ROOT, String(args.step)], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
            let text = '';child.stdout.on('data', b => { text += b; });child.stderr.on('data', b => { text += b; });
            const code = await new Promise((done, reject) => { child.once('error', reject);child.once('close', done); });
            if (code !== 0) throw new Error('Workload failed: ' + text);
            return { content: [{ type: 'text', text }], details: {} };
        }
    });
    pi.registerProvider('launcher-soak', {
        baseUrl: 'https://fixture.invalid/', apiKey: 'unused-fixture', api: 'launcher-soak-api',
        models: [{ id: 'probe', name: 'Soak', reasoning: false, input: ['text'], cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 65536, maxTokens: 1024 }],
        streamSimple(model, context) {
            const stream = new AssistantMessageEventStream();
            const results = context.messages.filter(m => m.role === 'toolResult');
            const last = results.at(-1);
            const pressure = last?.isError && JSON.stringify(last.content).includes('内存');
            const failed = last?.isError && !pressure;
            const completed = results.filter(r => !r.isError).length;
            const finished = failed || completed >= 30;
            // Only a confirmed pre-execution guard refusal can retry. An executing
            // workload failure has unknown effects and ends the acceptance run.
            const call = { type: 'toolCall', id: 'attempt-' + (results.length + 1), name: 'launcher_memory_workload', arguments: { step: completed + 1 } };
            const message = { role: 'assistant', api: model.api, provider: model.provider, model: model.id,
                content: finished ? [{ type: 'text', text: failed ? 'FAILED' : 'DONE' }] : [call],
                usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
                stopReason: finished ? 'stop' : 'toolUse', timestamp: Date.now() };
            const emit = () => {
                stream.push({ type: 'start', partial: message });
                if (!finished) {
                    stream.push({ type: 'toolcall_start', contentIndex: 0, partial: message });
                    stream.push({ type: 'toolcall_delta', contentIndex: 0, delta: JSON.stringify(call.arguments), partial: message });
                    stream.push({ type: 'toolcall_end', contentIndex: 0, toolCall: call, partial: message });
                }
                stream.push({ type: 'done', reason: message.stopReason, message });stream.end(message);
            };
            if (pressure) setTimeout(emit, 15000);else queueMicrotask(emit);
            return stream;
        }
    });
}
