# Repository Guidelines

## Project Overview

**多 Agent 启动器 (Multi-Agent Launcher), technical preview v0.6.** A native Windows WPF app that configures and launches AI CLI agents (Pi, Codex, Claude, etc.) into Windows Terminal split panes, with per-item Git worktree isolation, resource budgets (Win32 Job Objects), and fail-closed admission control. Default: one enabled agent; multi-item runs on the same project use independent worktrees or explicitly shared directories.

Status discipline: this is a technical preview, **not** a stable release. `docs/CONTRACTS.md` is the target contract; `docs/IMPLEMENTATION.md` records what is actually implemented and measured. Never claim a partially tested scenario passes; never present spec signatures as compiled behavior.

## Architecture & Data Flow

Four projects in a linear dependency chain — **Core ← Windows ← {Worker, App, Tests}** — plus a custom test harness:

| Project | Role | Forbidden |
|---|---|---|
| `AgentLauncher.Core` | Pure models/policy: `Draft`, `AgentProfile`, `ResourcePolicy.Check`, DTOs, schema validation | No WPF, disk, or process access |
| `AgentLauncher.Windows` | OS boundary: `Store` (atomic JSON), `Ipc` (named pipes), `Commands`, `Native`/`Job`/`ProtectedProcess`, `WorkspacePreparer` (git worktrees) | No zero-filled error values, no shell string concatenation |
| `AgentLauncher.Worker` | Console monitor + pane runner; single-instance pipe server; owns the control ledger; creates processes suspended inside Jobs | No timeout replay, no releasing unknown occupancy |
| `AgentLauncher.App` | Chinese WPF UI (code-behind, no MVVM); drag-drop, edit transactions, launch receipts | No synchronous waits on children; "started" ≠ "ready" |
| `AgentLauncher.Tests` | Console test runner (not xunit) + cross-process integration scenarios | Never writes to real user projects |

Data flow: App reads config → spawns Worker `monitor` via `Ipc.EnsureMonitorAsync` → user submits → monitor arbitrates `reserve` → `begin` → `resume` (exactly-once, epoch + plan digest) → Worker `pane <id>` claims the attempt, creates the process suspended in two Jobs (outer CPU 35% cap, per-item 2 GiB commit), writes receipt phases, waits for Job drain. Windows Terminal only hosts panes; the UI is not the agents' lifecycle owner.

Key invariants:
- **Fail-closed**: lost Job handle, boot-id change, or unexpected processes in the root Job block all new launches.
- **Atomic persistence**: temp file + flush + `File.Replace` with `.bak`, serialized by per-path named mutex. Corrupted/future-schema config blocks overwrite and new launches.
- **No kill-on-close, no auto-merge, no auto worktree cleanup.**
- Resource numbers: agent budget 2 GiB, git 1 GiB, total CPU 35%, deny at CPU ≥ 80%, samples stale after 3 s.

## Key Directories

- `src/AgentLauncher.Core/Models.cs` — all models, agent catalog, `ResourcePolicy`, JSON DTOs
- `src/AgentLauncher.Windows/` — `Services.cs` (Store/Ipc/Commands), `Native.cs` (P/Invoke, Jobs, sampler), `WorkspacePreparer.cs`
- `src/AgentLauncher.Worker/Program.cs` — monitor state machine + pane runner
- `src/AgentLauncher.App/` — `App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `UiChecks.cs` (test-only), `app.manifest`
- `tests/AgentLauncher.Tests/` — `Program.cs` (model/native tests), `Integration.cs` (`--integration` scenarios)
- `tests/prototype.test.cjs` — `node:test` suite against `docs/prototype-state.js`
- `scripts/` — `build.ps1`, `test.ps1`, `test-browser.mjs`
- `docs/` — versioned Chinese specs (see Important Files); `prototype.html` is sample-data-only
- `artifacts/` — generated outputs (preview publish, test runtime, result JSONs, screenshots); gitignored, never edit by hand

## Development Commands

```powershell
# Build preview (publishes Worker + App to artifacts/preview)
powershell -NoProfile -File scripts/build.ps1

# Full verification: model tests → integration → WPF UI checks → node tests → browser tests
powershell -NoProfile -File scripts/test.ps1            # add -SkipUi and/or -SkipBrowser to shorten

# Fastest loop: model/native tests only
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
```

- There is **no `dotnet test`** — tests are console executables gated by exit code.
- Builds are serialized (`-m:1`) and `--nologo`; preserve these flags in new scripts.
- There is **no CI** (no `.github/`), no `.editorconfig`, no `global.json`.

## Code Conventions & Common Patterns

- **C# 12, `Nullable=enable`, `ImplicitUsings`, `TreatWarningsAsErrors=true`** solution-wide (in `Directory.Build.props`). Fix warnings; never `#pragma`/`NoWarn` them away.
- **Zero third-party NuGet packages** — BCL + WPF only. Adding a `PackageReference` changes the project's stated posture; don't do it casually.
- **Dense formatting is deliberate**: multiple statements per line, one-line `if`/`foreach`, inline `try/catch`, collection expressions `[...]`, pattern matching (`args is ["monitor"]`). Match this style; do not reformat.
- **Errors**: exceptions only (no `Result` type). User-facing strings are Chinese and actionable. Catch filters `when (e is not OutOfMemoryException)` — OOM is never swallowed. UI funnels async errors through `GuardAsync` → `ErrorText`; Worker maps failures to exit codes 1/2.
- **Concurrency**: no `lock`/`Interlocked`/DI container. Coordination = named `Mutex`es (treat `AbandonedMutexException` as ownership) + one monitor thread + WPF Dispatcher. `Task.Run` for blocking IO; `async void` only on event handlers; `CancellationToken` flows through workspace prep only.
- **JSON**: one shared `Store.Json` (`JsonStringEnumConverter`, PascalCase keys, unindented); reads capped at 8 MiB; DTOs are `sealed record`, mutable config objects are `sealed class` with hand-written `Copy()`/`Validate()` and `SchemaVersion` gates.
- **`#if INTEGRATION_TESTS` hooks in production source are load-bearing**: `Store.Root` override (`AGENT_LAUNCHER_TEST_ROOT`), fake sampler (`AGENT_LAUNCHER_FAKE_SAMPLE`), crash injection (`AGENT_LAUNCHER_FAIL_AT`), `--ui-check/--idle-check/--screenshot` CLI modes. Do not delete them; do not compile them into the preview build (the manifest asserts `testHooksAbsent: true`).
- Test names embed acceptance IDs from `docs/ACCEPTANCE.md` (e.g. `"R12 receipt atomic replacement concurrent reader"`, `"G1 native Job creation + exact argv/cwd"`). Reuse existing IDs for new tests.

## Important Files

- `AgentLauncher.sln`, `Directory.Build.props` — build topology; csproj files are intentionally minified single-line XML
- `src/AgentLauncher.App/app.manifest` — `asInvoker`, PerMonitorV2 DPI, `longPathAware`
- `docs/SPEC.md` — product spec and out-of-scope list
- `docs/CONTRACTS.md` — C1–C5 release contracts (target, not necessarily implemented)
- `docs/ARCHITECTURE.md` — per-project responsibility/prohibition table, persistence/recovery rules, UI information contract
- `docs/ACCEPTANCE.md` — F01–F17 / R01–R16 / P01–P09 / V01–V09 acceptance matrices; status vocabulary is only 未执行/通过/失败/受阻
- `docs/IMPLEMENTATION.md` — actual measured results and remaining release blockers
- `docs/UI-DESIGN.md`, `docs/UI-REDESIGN-v0.7.md` — UI design tokens and redesign direction

## Runtime/Tooling Preferences

- **.NET SDK 8.0.419**, target `net8.0-windows`, `PlatformTarget=x64`, framework-dependent deployment → running the preview requires .NET 8 **Desktop** Runtime x64. Do not retarget to .NET 10 or AnyCPU; that migration is explicit future work requiring re-validation.
- **Node 24** for `tests/prototype.test.cjs` and `scripts/test-browser.mjs` (raw CDP; Edge path is hardcoded to `C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe` — use `-SkipBrowser` where absent).
- **PowerShell 5.1** scripts; no Bun, no package managers besides dotnet/node built-ins.
- External programs (Git, Windows Terminal, agent CLIs) are user-installed and never bundled or copied.
- Runtime state lives in `%LOCALAPPDATA%\AgentLauncher` (config, backups, grants, prepared dirs); no auto-delete policy. Never put long-lived secrets in launch arguments.
- No `.git` directory in this workspace — don't assume VCS operations are available.

## Testing & QA

- **Single supported gate**: `scripts/test.ps1`. Phases: (1) in-process model/Win32 tests via `dotnet run`; (2) republish with `-p:DefineConstants=INTEGRATION_TESTS` to `artifacts/test-runtime`; (3) `--integration` scenarios under a fresh `%TEMP%\AgentLauncher-tests-integration-<guid>` root; (4) WPF `--ui-check` (30 s timeout, expects `artifacts/ui/ui-checks.json`); (5) `node --test tests/prototype.test.cjs`; (6) headless-Edge CDP checks.
- **Never run test-build exes from `artifacts/test-runtime` interactively** (README warning); integration tests require `AGENT_LAUNCHER_TEST_ROOT` containing `AgentLauncher-tests-` as a hard guard.
- No coverage tooling, no mocking framework; the test exe doubles as a child-process fixture (`--echo`, `--hold`, `--allocate`).
- New tests go in `tests/AgentLauncher.Tests/Program.cs` (`Test("ID name", …)`), `Integration.cs` (`await Test(...)`), or `tests/prototype.test.cjs` (`test('ID: name', …)`), keyed to `docs/ACCEPTANCE.md` IDs.
- Evidence discipline: record test ID, build, machine, expected vs actual, and artifact paths in `docs/IMPLEMENTATION.md`; never weaken tests, edit thresholds, or fabricate results to go green (`docs/ACCEPTANCE.md` §gates).
