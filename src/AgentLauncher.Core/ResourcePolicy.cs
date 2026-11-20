namespace AgentLauncher.Core;

public sealed class ResourceSettings
{
    public string Mode { get; set; } = "practical";
    public ulong JobMemoryLimit { get; set; } = ResourcePolicy.DefaultJobMemoryLimit;
    public ulong PlanningBytes { get; set; } = ResourcePolicy.DefaultPlanningBytes;
    public ulong? PhysicalFloorOverride { get; set; }
    public ulong? CommitFloorOverride { get; set; }
    public int CpuRate { get; set; } = ResourcePolicy.DefaultCpuRate;
    public string Pool { get; set; } = ResourcePolicy.PoolIndependent;
    public ulong? PoolMemoryLimit { get; set; }
    public ResourceSettings Copy() => new()
    {
        Mode = Mode,
        JobMemoryLimit = JobMemoryLimit,
        PlanningBytes = PlanningBytes,
        PhysicalFloorOverride = PhysicalFloorOverride,
        CommitFloorOverride = CommitFloorOverride,
        CpuRate = CpuRate,
        Pool = Pool,
        PoolMemoryLimit = PoolMemoryLimit
    };
}

public sealed record ResourcePolicySnapshot(
    string Mode, ulong JobMemoryLimit, ulong PlanningBytes,
    ulong PhysicalFloor, ulong CommitFloor, int CpuRate,
    ulong GitJobMemoryLimit, ulong GitPlanningBytes,
    string PoolMode = ResourcePolicy.PoolIndependent, ulong PoolMemoryLimit = 0);

// Usage credits come only from the monitor's live Job queries, never from launch requests or historical peaks.
public sealed record BudgetItem(Guid Id, string Kind, ulong PlanningBytes, ulong JobMemoryLimit, Guid? PoolId = null, ulong? PoolLimit = null,
    ulong? CurrentCommitBytes = null, ulong? CurrentPhysicalBytes = null, ulong? PoolCommitBytes = null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool Remote = false);

public enum AdmissionKind { Allow, Deny }

public sealed record AdmissionDecision(
    AdmissionKind Kind,
    string Reason,
    ulong PlanningRequested,
    ulong PlanningReserved,
    ulong CapSumRequested,
    ulong CapSumReserved,
    ulong SoftCap,
    ulong PhysicalFloor,
    ulong CommitFloor,
    bool SampleValid,
    bool Advisory = false)
{
    public bool Launchable => Kind is AdmissionKind.Allow;
}

public static class ConfigurationCompatibility
{
    public const int CurrentSchemaVersion = 2;

    public static Configuration Accept(Configuration config)
    {
        if (config is null) throw new InvalidDataException("配置版本或结构不支持，未修改原配置。");
        if (config.SchemaVersion is < 1 or > CurrentSchemaVersion) throw new InvalidDataException("配置版本或结构不支持，未修改原配置。");
        if (config.Draft is null) throw new InvalidDataException("配置版本或结构不支持，未修改原配置。");
        EnsureSupported(config.Draft);
        if (config.LastEdited != null) EnsureSupported(config.LastEdited);
        if (config.Templates != null)
        {
            foreach (var template in config.Templates.Values)
            {
                if (template is null) throw new InvalidDataException("配置版本或结构不支持，未修改原配置。");
                EnsureSupported(template);
            }
        }
        Upgrade(config.Draft);
        if (config.LastEdited != null) Upgrade(config.LastEdited);
        if (config.Templates != null) foreach (var template in config.Templates.Values) Upgrade(template);
        config.SchemaVersion = CurrentSchemaVersion;
        config.Draft.Validate();
        config.LastEdited?.Validate();
        if (config.Templates != null) foreach (var template in config.Templates.Values) template.Validate();
        return config;
    }

    private static void EnsureSupported(Draft draft)
    {
        if (draft.SchemaVersion is < 1 or > CurrentSchemaVersion) throw new InvalidDataException("配置版本或结构不支持，未修改原配置。");
    }

    private static void Upgrade(Draft draft)
    {
        if (draft.SchemaVersion < CurrentSchemaVersion)
        {
            draft.Resource ??= new ResourceSettings();
            if (string.IsNullOrWhiteSpace(draft.Resource.Mode)) draft.Resource.Mode = "practical";
            if (draft.Resource.JobMemoryLimit == 0) draft.Resource.JobMemoryLimit = ResourcePolicy.DefaultJobMemoryLimit;
            if (draft.Resource.PlanningBytes == 0) draft.Resource.PlanningBytes = ResourcePolicy.DefaultPlanningBytes;
            if (draft.Resource.CpuRate == 0) draft.Resource.CpuRate = ResourcePolicy.DefaultCpuRate;
        }
        draft.SchemaVersion = CurrentSchemaVersion;
    }
}

public static class LedgerMigration
{
    public const int CurrentSchemaVersion = 3;
    public const int MaxTaskClaims = TaskSchema.MaxAttempts + TaskSchema.MaxQueueEntries;

    // Version 1 had no pool records: every entry was independently launched, which is still a valid reading.
    public static Ledger Accept(Ledger ledger)
    {
        if (ledger is null) throw new InvalidDataException("控制账本损坏或版本不支持。");
        if (ledger.SchemaVersion is < 1 or > CurrentSchemaVersion) throw new InvalidDataException("控制账本损坏或版本不支持。");
        ledger.Entries ??= [];
        ledger.Pools ??= [];
        if (ledger.Entries.Any(e => e is null || e.Plan is null)) throw new InvalidDataException("控制账本损坏或版本不支持。");
        if (ledger.Pools.Any(p => p is null || p.PolicyHash is null)) throw new InvalidDataException("控制账本损坏或版本不支持。");
        if (ledger.Pools.Select(p => p.Id).Distinct().Count() != ledger.Pools.Count) throw new InvalidDataException("控制账本损坏或版本不支持。");
        // Claims are execution authority, not display history; retain them across pruning and boot rollover.
        // Versions 1/2 deserialize missing claims as an empty list. Version 3 introduced the field and must never be written by an older worker.
        if (ledger.TaskClaims is null) throw new InvalidDataException("队列领取记录损坏或超出边界，已停止启动与回收。");
        // Bind presence to the version that introduced claims, not the current version, so a later schema bump cannot excuse a missing field.
        if (ledger.SchemaVersion >= 3 && ledger.TaskClaimsAbsentFromJson)
            throw new InvalidDataException("控制账本缺少队列领取记录，已停止启动与回收。");
        if (ledger.TaskClaims.Count > MaxTaskClaims
            || ledger.TaskClaims.Any(claim => claim is null || claim.EntryId == Guid.Empty || claim.AttemptId == Guid.Empty)
            || ledger.TaskClaims.Select(claim => claim.EntryId).Distinct().Count() != ledger.TaskClaims.Count
            || ledger.TaskClaims.Select(claim => claim.AttemptId).Distinct().Count() != ledger.TaskClaims.Count)
            throw new InvalidDataException("队列领取记录损坏或超出边界，已停止启动与回收。");
        foreach (var entry in ledger.Entries)
        {
            // Finished history keeps the pool id for display; only an entry that can still launch needs frozen pool metadata.
            if (entry.Plan.PoolId is { } id && !ControlEntryStates.IsTerminal(entry))
            {
                var pool = ledger.Pools.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("控制账本缺少池记录，已停止启动与回收。");
                if (pool.MemoryLimit == 0) throw new InvalidDataException("控制账本中的池上限不可用。");
            }
        }
        ledger.SchemaVersion = CurrentSchemaVersion;
        ledger.ConfirmTaskClaimsMigrated();
        return ledger;
    }

    // A boot id change proves every process from the previous boot is gone, so pool handles and charges may be retired.
    public static bool Rollover(Ledger ledger, string boot)
    {
        if (ledger is null) throw new InvalidDataException("控制账本损坏或版本不支持。");
        if (ledger.BootId == boot) return false;
        foreach (var entry in ledger.Entries.Where(e => !ControlEntryStates.IsTerminal(e)))
        {
            if (entry.Plan.RemoteExecution is not null && entry.State != "Pending")
            {
                entry.State = "Disconnected";
                entry.Detail = "本机重启不能证明远程进程结束，请核对远程主机；未重放旧请求。";
                continue;
            }
            entry.State = "Settled";
            entry.Detail = "系统已重新启动，未重放旧请求";
        }
        ledger.Pools.Clear();
        ledger.BootId = boot;
        return true;
    }
}

public static class ResourcePolicy
{
    public const ulong GiB = 1024UL * 1024 * 1024;
    public const ulong MiB = 1024UL * 1024;
    public const ulong DefaultJobMemoryLimit = 2 * GiB;
    public const ulong MinJobMemoryLimit = 1 * GiB;
    public const ulong MaxJobMemoryLimit = 8 * GiB;
    public const ulong DefaultPlanningBytes = 768 * MiB;
    public const ulong MinPlanningBytes = 256 * MiB;
    public const ulong GitJobMemoryLimit = GiB;
    public const ulong DefaultGitPlanningBytes = 256 * MiB;
    public const int DefaultCpuRate = 3500;
    public const int MinCpuRate = 1500;
    public const int MaxCpuRate = 8000;
    public const int CpuBusyPercent = 80;
    public const int SampleTtlSeconds = 3;
    public const int IdleTimeoutSeconds = 15;
    public const int CommandLineLimit = 24000;
    public const int MaxSlots = 16;
    // Ray-style fixed concurrency pool: cap how many agent panes run at once instead of
    // refusing launches when readings dip; the kernel Job cap stays the hard wall per process.
    public const int MaxConcurrentAgents = 8;
    public const string PoolIndependent = "independent";
    public const string PoolShared = "shared";
    public const ulong AgentBudget = 2 * GiB;
    public const ulong GitBudget = GiB;
    // In conservative mode floors refuse new work before the machine thrashes; in other modes they only advise.
    // References: earlyoom acts at ~10% RAM free; Kubernetes admits on requests while limits are kernel-enforced.
    public static ulong DefaultPhysicalFloor(ulong total) => Math.Max(3 * GiB / 2, total / 12);
    public static ulong DefaultCommitFloor(ulong commitLimit) => Math.Max(3 * GiB / 2, commitLimit / 24);
    public static ulong SoftCap(ulong total)
    {
        var portion = total / 100 * 35 + total % 100 * 35 / 100;
        return Math.Min(8 * GiB, portion);
    }
    public static ulong MinCustomPhysicalFloor => 3 * GiB / 2;
    public const ulong MaxCustomPhysicalFloor = 8 * GiB;
    public static ulong MinCustomCommitFloor => GiB;
    public const ulong MaxCustomCommitFloor = 16 * GiB;
    public static ulong Reserve(ulong total) => Math.Max(3 * GiB, total / 5);

    public static string PoolModeOf(ResourceSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Pool) ? PoolIndependent : settings.Pool;

    public static ulong PoolLimitOf(ResourceSettings settings) =>
        settings.PoolMemoryLimit is { } limit && limit > 0 ? limit : settings.JobMemoryLimit;

    public static ResourcePolicySnapshot SnapshotFor(ResourceSettings settings, ulong totalPhysical, ulong commitLimit)
    {
        settings ??= new ResourceSettings();
        return new(
            settings.Mode,
            settings.JobMemoryLimit,
            settings.PlanningBytes,
            settings.PhysicalFloorOverride ?? DefaultPhysicalFloor(totalPhysical),
            settings.CommitFloorOverride ?? DefaultCommitFloor(commitLimit),
            settings.CpuRate,
            GitJobMemoryLimit,
            DefaultGitPlanningBytes,
            PoolModeOf(settings),
            PoolLimitOf(settings));
    }

    public static AdmissionDecision Evaluate(
        MemorySnapshot sample,
        ResourcePolicySnapshot policy,
        IReadOnlyList<BudgetItem> reserved,
        IReadOnlyList<BudgetItem> requested,
        bool includeSoftBudget = true)
    {
        reserved ??= [];
        requested ??= [];
        var planningRequested = SumPlanning(policy, requested);
        var planningReserved = SumPlanning(policy, reserved);
        var capRequested = SumCap(policy, requested);
        var capReserved = SumCap(policy, reserved);
        var soft = sample is not null && sample.Total > 0 ? SoftCap(sample.Total) : 0;
        var floor = policy.PhysicalFloor;
        var commitFloor = policy.CommitFloor;

        var strict = policy.Mode == "conservative";

        AdmissionDecision Result(AdmissionKind kind, string reason, bool sampleValid, bool advisory = false) =>
            new(kind, reason, planningRequested, planningReserved, capRequested, capReserved, soft, floor, commitFloor, sampleValid, advisory);

        if (!IsSampleValid(sample) || sample is null)
            return Result(AdmissionKind.Deny, "系统采样无效，请稍后重试。", false);

        try { ValidateSnapshot(policy, sample.Total, sample.CommitLimit); }
        catch (InvalidDataException ex) { return Result(AdmissionKind.Deny, ex.Message, true); }

        // Accounting fails closed before any advisory return: a saturating cap sum cannot be budgeted in any mode.
        var unionCap = requested.Count == 0 ? capReserved : SumCap(policy, [.. reserved, .. requested]);
        if (unionCap == ulong.MaxValue)
            return Result(AdmissionKind.Deny, "各项保护上限加总溢出，无法核算预算；请检查资源设置中的上限值。", true);

        var physicalNeed = AddSaturating(AddSaturating(planningRequested, RemainingPhysical(policy, reserved)), policy.PhysicalFloor);
        if (sample.Available < physicalNeed)
        {
            var text = $"可用内存 {GiBText(sample.Available)} GiB，本次启动需要 {GiBText(physicalNeed)} GiB（含系统保留 {GiBText(floor)} GiB），还差约 {ShortfallGiB(physicalNeed, sample.Available)} GiB。";
            return strict
                ? Result(AdmissionKind.Deny, text + "保存并退出不用的 Agent 或应用，或用「按资源依次启动」等待资源。", true)
                : Result(AdmissionKind.Allow, text + "实用模式不拦截，进程内存硬上限仍然生效；建议保存并退出不用的 Agent 或应用。", true, true);
        }
        var commitNeed = AddSaturating(RemainingCommit(policy, reserved, requested), policy.CommitFloor);
        if (sample.CommitAvailable < commitNeed)
        {
            var text = $"提交余量 {GiBText(sample.CommitAvailable)} GiB，本次需要 {GiBText(commitNeed)} GiB，还差约 {ShortfallGiB(commitNeed, sample.CommitAvailable)} GiB。";
            return strict
                ? Result(AdmissionKind.Deny, text + "保存并退出不用的 Agent、减少并发，或用「按资源依次启动」等待资源。", true)
                : Result(AdmissionKind.Allow, text + "实用模式不拦截，进程内存硬上限仍然生效；建议保存并退出不用的 Agent 或减少并发。", true, true);
        }
        if (sample.CpuPercent >= CpuBusyPercent)
            return strict
                ? Result(AdmissionKind.Deny, "整机 CPU 正忙，稍等片刻再启动。", true)
                : Result(AdmissionKind.Allow, "整机 CPU 正忙，启动后响应可能偏慢；实用模式不拦截。", true, true);

        if(includeSoftBudget)
        {
            var combined=reserved.Concat(requested).Where(item=>item.Kind!="git"&&item.PoolId!=null&&item.PoolLimit is >0).GroupBy(item=>item.PoolId);
            foreach(var pool in combined)
            {
                if(!requested.Any(item=>item.PoolId==pool.Key))continue;
                if(SumPlanning(policy,pool.ToArray())<=pool.Min(item=>item.PoolLimit!.Value))continue;
                var text="这批 Agent 的预计总量超过共享池上限，同时运行时可能被系统拒绝分配。";
                return strict
                    ? Result(AdmissionKind.Deny, text + "请减少同时启动数；每项及整池的保护不会放宽。", true)
                    : Result(AdmissionKind.Allow, text + "实用模式不拦截，每项及整池的保护不会放宽。", true, true);
            }
        }
        if (includeSoftBudget && unionCap > soft)
        {
            if (strict)
                return Result(AdmissionKind.Deny, "本次要用的内存超过总预算，请少勾选几个 Agent。", true);
            return Result(AdmissionKind.Allow, "按 Job 上限加总已超过软预算，同时运行可能互相挤压；实用模式不拦截，硬保护仍然有效。", true, true);
        }
        return Result(AdmissionKind.Allow, "资源充足", true);
    }

    public static string PolicyHash(ResourcePolicySnapshot policy)
    {
        var mode = System.Text.Json.JsonSerializer.Serialize(policy.Mode);
        var poolMode = System.Text.Json.JsonSerializer.Serialize(policy.PoolMode);
        var payload = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{\"CommitFloor\":{policy.CommitFloor},\"CpuRate\":{policy.CpuRate},\"GitJobMemoryLimit\":{policy.GitJobMemoryLimit},\"GitPlanningBytes\":{policy.GitPlanningBytes},\"JobMemoryLimit\":{policy.JobMemoryLimit},\"Mode\":{mode},\"PhysicalFloor\":{policy.PhysicalFloor},\"PlanningBytes\":{policy.PlanningBytes},\"PoolMemoryLimit\":{policy.PoolMemoryLimit},\"PoolMode\":{poolMode}}}");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static string PlanDigest(IEnumerable<LaunchItem> items)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize((items ?? []).Select(item => new
        {
            Id = item.Id.ToString("N"),
            BatchId = item.BatchId.ToString("N"),
            Position = item.Position,
            SourceSlotId = item.SourceSlotId?.ToString("N"),
            AgentId = item.AgentId,
            RetryOf = item.RetryOf?.ToString("N"),
            Kind = item.Kind ?? "agent",
            Name = item.Name ?? "",
            Cwd = item.Cwd ?? "",
            WorkspaceKey = item.Workspace.Key ?? "",
            WorkspacePath = item.Workspace.Path ?? "",
            Shared = item.Shared,
            Executable = item.Command.Executable ?? "",
            Arguments = item.Command.Arguments ?? Array.Empty<string>(),
            Budget = item.Budget,
            Adapter = item.Command.Adapter,
            ResourceMode = item.ResourceMode,
            PoolId = item.PoolId?.ToString("N"),
            PlanningBytes = item.PlanningBytes ?? 0,
            // 远程执行身份（主机/用户/目录/同步模式）必须绑定到摘要：改主机或改同步方式会使旧计划失效
            Remote = item.RemoteExecution is null ? null : new
            {
                item.RemoteExecution.Host,
                item.RemoteExecution.SshPort,
                item.RemoteExecution.SshUser,
                item.RemoteExecution.AgentUser,
                item.RemoteExecution.AgentType,
                item.RemoteExecution.RemoteWorkingDirectory,
                Arguments = item.RemoteExecution.Arguments,
                item.RemoteExecution.MemoryLimitBytes,
                item.RemoteExecution.SshKeyPath,
                SyncMode = item.RemoteExecution.WorkspaceSync?.Mode,
                SyncLocal = item.RemoteExecution.WorkspaceSync?.LocalPath,
                SyncRemote = item.RemoteExecution.WorkspaceSync?.RemotePath
            }
        }));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static void ValidateSnapshot(ResourcePolicySnapshot policy, ulong totalPhysical, ulong commitLimit)
    {
        if (policy is null || policy.Mode is not ("conservative" or "practical" or "custom"))
            throw new InvalidDataException("资源配置不完整或不支持。");
        var maxJob = totalPhysical < MaxJobMemoryLimit ? totalPhysical : MaxJobMemoryLimit;
        if (policy.JobMemoryLimit < MinJobMemoryLimit)
            throw new InvalidDataException("Job 内存上限不能低于 1 GiB。");
        if (policy.JobMemoryLimit > maxJob)
            throw new InvalidDataException("Job 内存上限不能超过 8 GiB。");
        if (policy.PlanningBytes < MinPlanningBytes || policy.PlanningBytes > policy.JobMemoryLimit)
            throw new InvalidDataException("规划估算必须在允许范围内且不超过 Job 上限。");
        if (policy.CpuRate < MinCpuRate || policy.CpuRate > MaxCpuRate)
            throw new InvalidDataException("CPU 封顶必须在 15% 到 80% 之间。");
        if (policy.GitJobMemoryLimit != GitJobMemoryLimit || policy.GitPlanningBytes != DefaultGitPlanningBytes)
            throw new InvalidDataException("Git 资源上限不可自定义。");
        if (policy.PoolMode is not (PoolIndependent or PoolShared))
            throw new InvalidDataException("资源池模式不支持。");
        if (policy.PoolMode == PoolShared)
        {
            if (policy.PoolMemoryLimit < MinJobMemoryLimit || policy.PoolMemoryLimit > maxJob)
                throw new InvalidDataException("资源池上限不能低于 1 GiB 或超过 8 GiB。");
            if (policy.PoolMemoryLimit < policy.JobMemoryLimit)
                throw new InvalidDataException("资源池上限不能低于单项 Job 上限。");
        }
        if (policy.PhysicalFloor == 0 || policy.PhysicalFloor < MinCustomPhysicalFloor)
            throw new InvalidDataException("物理临界余量不能低于下限，也不能为 0。");
        if (policy.PhysicalFloor != DefaultPhysicalFloor(totalPhysical))
        {
            var maxPhysical = Min(totalPhysical / 2, MaxCustomPhysicalFloor);
            if (policy.PhysicalFloor > maxPhysical)
                throw new InvalidDataException("物理临界余量不能超过 8 GiB。");
        }
        if (policy.CommitFloor == 0 || policy.CommitFloor < MinCustomCommitFloor)
            throw new InvalidDataException("提交临界余量不能低于下限，也不能为 0。");
        if (policy.CommitFloor != DefaultCommitFloor(commitLimit))
        {
            var maxCommit = Min(commitLimit / 4, MaxCustomCommitFloor);
            if (policy.CommitFloor > maxCommit)
                throw new InvalidDataException("提交临界余量不能超过 16 GiB。");
        }
    }

    public static void ValidateSettings(ResourceSettings settings, ulong totalPhysical, ulong commitLimit)
    {
        settings ??= new ResourceSettings();
        if (settings.Mode is not ("conservative" or "practical" or "custom"))
            throw new InvalidDataException("资源配置不完整或不支持。");
        var maxJob = totalPhysical < MaxJobMemoryLimit ? totalPhysical : MaxJobMemoryLimit;
        if (settings.JobMemoryLimit < MinJobMemoryLimit)
            throw new InvalidDataException("Job 内存上限不能低于 1 GiB。");
        if (settings.JobMemoryLimit > maxJob)
            throw new InvalidDataException("Job 内存上限不能超过 8 GiB。");
        if (settings.PlanningBytes < MinPlanningBytes || settings.PlanningBytes > settings.JobMemoryLimit)
            throw new InvalidDataException("规划估算必须在允许范围内且不超过 Job 上限。");
        if (settings.CpuRate < MinCpuRate || settings.CpuRate > MaxCpuRate)
            throw new InvalidDataException("CPU 封顶必须在 15% 到 80% 之间。");
        if (PoolModeOf(settings) is not (PoolIndependent or PoolShared))
            throw new InvalidDataException("资源池模式不支持。");
        if (PoolModeOf(settings) == PoolShared)
        {
            var poolLimit = PoolLimitOf(settings);
            if (poolLimit < MinJobMemoryLimit || poolLimit > maxJob)
                throw new InvalidDataException("资源池上限不能低于 1 GiB 或超过 8 GiB。");
            if (poolLimit < settings.JobMemoryLimit)
                throw new InvalidDataException("资源池上限不能低于单项 Job 上限。");
        }
        if (settings.PhysicalFloorOverride is { } physical)
        {
            if (physical == 0 || physical < MinCustomPhysicalFloor)
                throw new InvalidDataException("物理临界余量不能低于下限，也不能为 0。");
            var maxPhysical = Min(totalPhysical / 2, MaxCustomPhysicalFloor);
            if (physical > maxPhysical)
                throw new InvalidDataException("物理临界余量不能超过 8 GiB。");
        }
        if (settings.CommitFloorOverride is { } commit)
        {
            if (commit == 0 || commit < MinCustomCommitFloor)
                throw new InvalidDataException("提交临界余量不能低于下限，也不能为 0。");
            var maxCommit = Min(commitLimit / 4, MaxCustomCommitFloor);
            if (commit > maxCommit)
                throw new InvalidDataException("提交临界余量不能超过 16 GiB。");
        }
    }

    public static void ValidateLaunchItem(LaunchItem item, ResourcePolicySnapshot policy, ulong totalPhysical)
    {
        if (item is null) throw new InvalidDataException("执行计划不支持。");
        if (item.RemoteExecution is {} remote)
        {
            remote.Validate();
            if(remote.WorkspaceSync is {Mode:not WorkspaceSyncMode.None} sync&&
                !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sync.LocalPath)),Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Cwd)),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("远程同步的本地目录必须与本次启动的工作目录一致。");
            if (item.Kind != "agent" || item.Budget != 0 || item.PoolId is not null || item.PlanningBytes is not null || item.ResourceMode is not null)
                throw new InvalidDataException("远程执行不接受本地 Job、资源池或内存预算。");
            return;
        }
        if (item.Kind == "git")
        {
            if (item.Budget != GitJobMemoryLimit)
                throw new InvalidDataException("执行计划不支持。");
            if (item.PlanningBytes is { } gitPlanning && gitPlanning != 0 && gitPlanning != DefaultGitPlanningBytes)
                throw new InvalidDataException("规划估算必须在允许范围内且不超过 Job 上限。");
            return;
        }
        if (item.Kind is not (null or "agent"))
            throw new InvalidDataException("执行计划不支持。");
        var maxJob = totalPhysical < MaxJobMemoryLimit ? totalPhysical : MaxJobMemoryLimit;
        if (item.Budget < MinJobMemoryLimit || item.Budget > maxJob)
            throw new InvalidDataException(item.Budget > totalPhysical ? "Job 内存上限不能超过可用机器总量。" : "Job 内存上限不能超过 8 GiB。");
        if (item.Budget != policy.JobMemoryLimit)
            throw new InvalidDataException("启动项 Job 上限与当前策略不一致。");
        if (policy.PoolMode == PoolShared && item.PoolId is null)
            throw new InvalidDataException("共享池批次缺少池标识，请重新准备启动计划。");
        if (policy.PoolMode == PoolIndependent && item.PoolId is not null)
            throw new InvalidDataException("独立模式不接受池标识，请重新准备启动计划。");
        if (item.PlanningBytes is { } planning && planning != 0)
        {
            if (planning < MinPlanningBytes || planning > item.Budget)
                throw new InvalidDataException("规划估算必须在允许范围内且不超过 Job 上限。");
            if (planning != policy.PlanningBytes)
                throw new InvalidDataException("启动项规划估算与当前策略不一致。");
        }
        else if (policy.PlanningBytes != DefaultPlanningBytes)
            throw new InvalidDataException("启动项缺少当前策略的规划估算，请重新准备启动计划。");
    }

    public static BudgetItem BudgetOf(LaunchItem plan, ulong? poolLimit = null)
    {
        if (plan.RemoteExecution is not null) return new(plan.Id, "agent", 0, 0, Remote: true);
        var git = plan.Kind == "git";
        return new(
            plan.Id,
            git ? "git" : (plan.Kind ?? "agent"),
            git ? DefaultGitPlanningBytes : plan.PlanningBytes is null or 0 ? DefaultPlanningBytes : plan.PlanningBytes.Value,
            git ? GitJobMemoryLimit : plan.Budget,
            git ? null : plan.PoolId,
            git || plan.PoolId is null ? null : poolLimit);
    }

    public static bool IsSampleValid(MemorySnapshot? sample)
    {
        if (sample is null) return false;
        if (DateTimeOffset.UtcNow - sample.At > TimeSpan.FromSeconds(SampleTtlSeconds)) return false;
        if (!double.IsFinite(sample.CpuPercent) || sample.CpuPercent < 0 || sample.CpuPercent > 100) return false;
        // Zero availability is exhaustion evidence, not a failed sample; only structural zeroes are invalid.
        if (sample.Total == 0 || sample.CommitLimit == 0) return false;
        if (sample.Available > sample.Total || sample.CommitAvailable > sample.CommitLimit) return false;
        return true;
    }

    // Status telemetry is advisory for previews; the monitor always remeasures before granting a launch.
    public static BudgetItem[] ReservedFor(Response status) => status.Budgets
        ?? (status.Entries ?? []).Where(e => !ControlEntryStates.IsTerminal(e))
            .Select(e => BudgetOf(e.Plan, e.PoolMemoryLimit)).ToArray();

    static ulong PlanningOf(ResourcePolicySnapshot policy, BudgetItem item) =>
        item.Remote ? 0 : item.Kind == "git" ? policy.GitPlanningBytes : item.PlanningBytes == 0 ? DefaultPlanningBytes : item.PlanningBytes;

    static ulong CapOf(ResourcePolicySnapshot policy, BudgetItem item) =>
        item.Remote ? 0 : item.Kind == "git" ? policy.GitJobMemoryLimit : item.JobMemoryLimit == 0 ? DefaultJobMemoryLimit : item.JobMemoryLimit;

    static ulong SumPlanning(ResourcePolicySnapshot policy, IReadOnlyList<BudgetItem> items)
    {
        ulong sum = 0;
        foreach (var item in items)
        {
            var value = PlanningOf(policy, item);
            if (sum > ulong.MaxValue - value) return ulong.MaxValue;
            sum += value;
        }
        return sum;
    }

    static ulong RemainingPhysical(ResourcePolicySnapshot policy, IReadOnlyList<BudgetItem> reserved)
    {
        ulong sum = 0;
        foreach (var item in reserved)
        {
            var planning = PlanningOf(policy, item);
            sum = AddSaturating(sum, planning - Math.Min(planning, item.CurrentPhysicalBytes ?? 0));
        }
        return sum;
    }

    // Admission reserves planning-based growth (requests); hard caps stay kernel-enforced limits.
    // Charging full hard-cap remainders made a busy 16 GiB machine unable to admit any further agent.
    static ulong RemainingCommit(ResourcePolicySnapshot policy, IReadOnlyList<BudgetItem> reserved, IReadOnlyList<BudgetItem> requested)
    {
        ulong sum = 0;
        var pools = new Dictionary<Guid, (ulong Planning, ulong? Used)>();
        void Add(BudgetItem item, bool active)
        {
            var planning = PlanningOf(policy, item);
            if (item.Kind != "git" && item.PoolId is { } id && item.PoolLimit is > 0)
            {
                // The same pool can appear in both sets. Charge member planning once and use only its own
                // live measurement; a requested item's own claimed usage never offsets the charge.
                var previous = pools.GetValueOrDefault(id);
                ulong? used = previous.Used;
                if (active && item.PoolCommitBytes is { } measured) used = used is { } known ? Math.Max(known, measured) : measured;
                pools[id] = (AddSaturating(previous.Planning, planning), used);
                return;
            }
            var hedge = !active ? planning : item.CurrentCommitBytes is { } usedCommit ? planning - Math.Min(planning, usedCommit) : planning;
            sum = AddSaturating(sum, hedge);
        }
        foreach (var item in reserved) Add(item, true);
        foreach (var item in requested) Add(item, false);
        foreach (var pool in pools.Values) sum = AddSaturating(sum, pool.Used is { } used ? pool.Planning - Math.Min(pool.Planning, used) : pool.Planning);
        return sum;
    }

    static string ShortfallGiB(ulong need, ulong available) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(need - Math.Min(need, available)) / (double)GiB:0.0}");

    static string GiBText(ulong bytes) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)GiB:0.0}");

    static ulong AddSaturating(ulong left, ulong right) => left > ulong.MaxValue - right ? ulong.MaxValue : left + right;

    static ulong SumCap(ResourcePolicySnapshot policy, IReadOnlyList<BudgetItem> items)
    {
        ulong sum = 0;
        var pools = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (item.Kind != "git" && item.PoolId is { } poolId && item.PoolLimit is > 0)
            {
                if (!pools.Add(poolId)) continue;
                if (sum > ulong.MaxValue - item.PoolLimit.Value) return ulong.MaxValue;
                sum += item.PoolLimit.Value;
                continue;
            }
            var value = CapOf(policy, item);
            if (sum > ulong.MaxValue - value) return ulong.MaxValue;
            sum += value;
        }
        return sum;
    }

    static ulong Min(ulong left, ulong right) => left < right ? left : right;
}
