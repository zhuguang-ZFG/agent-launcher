# Review and pane-overflow repair implementation plan

Goal: preserve the existing WIP, close all six review gaps, and make memory refusals observable without weakening protection.
Architecture: monitor remains authority; use pre-admission serialized capacity accounting and immutable per-attempt terminal archives. Observe kernel Job completion messages without terminating any process. Pane Worker stays outside capped jobs and retains its own exit screen.
Constraints: Windows/.NET 8, CPU 35%, unchanged memory ceilings, no global Terminal changes, no real-agent stress, no push.

1. Preserve existing changes on a feature branch (9afe2d4); WIP cannot be claimed test-first.
2. Add isolated RepairChecks (reflection uses existing monitor boundary), run red before product edits.
3. Enforce missing-authority fail-closed consistently for status/reserve/waiver; verify focused checks.
4. Check exact serialized active ledger plus pools/claims and lifecycle reserve before changing authority or creating pools; latch persistence faults.
5. Archive terminal outcomes before pruning; add read-only attempt IPC with ID/digest validation; UI checks TaskId/BatchId/workspace instead of reading backups/receipts.
6. Observe Job memory completion messages, retain attempt-scoped notices, show exact refusal plus pool scope where known; preserve sibling processes and unchanged caps. Keep interactive errors visible.
7. Run focused repair checks, native/model gate, admission isolation gate, WPF checks where possible; self-review and record actual evidence/omissions.
