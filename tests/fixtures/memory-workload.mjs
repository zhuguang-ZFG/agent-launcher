import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync, appendFileSync, existsSync } from 'node:fs';
import { resolve } from 'node:path';
import assert from 'node:assert/strict';

// Repeated bounded read/hash/validation, with a strict sequential journal.
const [root, stepText] = process.argv.slice(2);
const step = Number(stepText);
assert.ok(root.includes('AgentLauncher-tests-') && Number.isInteger(step) && step > 0 && step <= 30);
const journal = resolve(root, 'workload.jsonl');
const previous = existsSync(journal) ? readFileSync(journal, 'utf8').trim().split('\n').filter(Boolean) : [];
assert.equal(step, previous.length + 1, 'duplicate or out-of-order workload call');
const data = Buffer.alloc(16 * 1024 * 1024, step);
const path = resolve(root, 'payload.bin');
writeFileSync(path, data);
const expected = createHash('sha256').update(data).digest('hex');
const started = performance.now();
let passes = 0;
do {
    assert.equal(createHash('sha256').update(readFileSync(path)).digest('hex'), expected);
    passes++;
    await new Promise(done => setTimeout(done, 500));
} while (performance.now() - started < 60000);
const result = { step, passes, seconds: (performance.now() - started) / 1000, at: new Date().toISOString() };
appendFileSync(journal, JSON.stringify(result) + '\n');
console.log(JSON.stringify({ ...result, remaining: 30 - step }));
