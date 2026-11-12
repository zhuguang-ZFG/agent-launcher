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

public sealed record BudgetItem(Guid Id, string Kind, ulong PlanningBytes, ulong JobMemoryLimit, Guid? PoolId = null, ulong? PoolLimit = null);

public enum AdmissionKind { Allow, RequireWaiver, Deny }

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
    bool SampleValid)
{
    public bool Launchable => Kind is AdmissionKind.Allow or AdmissionKind.RequireWaiver;
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
            if (entry.Plan.PoolId is { } id && entry.State is not ("Revoked" or "Settled" or "FailedBeforeStart"))
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
        foreach (var entry in ledger.Entries.Where(e => e.State is not ("Revoked" or "Settled" or "FailedBeforeStart")))
        {
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
    public const int WaiverTtlSeconds = 45;
    public const int IdleTimeoutSeconds = 15;
    public const int CommandLineLimit = 24000;
    public const int MaxSlots = 16;
    public const string PoolIndependent = "independent";
    public const string PoolShared = "shared";
    public const ulong AgentBudget = 2 * GiB;
    public const ulong GitBudget = GiB;
    public static ulong DefaultPhysicalFloor(ulong total) => Math.Max(2 * GiB, total / 8);
    public static ulong DefaultCommitFloor(ulong commitLimit) => Math.Max(2 * GiB, commitLimit / 20);
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

        AdmissionDecision Result(AdmissionKind kind, string reason, bool sampleValid) =>
            new(kind, reason, planningRequested, planningReserved, capRequested, capReserved, soft, floor, commitFloor, sampleValid);

        if (!IsSampleValid(sample) || sample is null)
            return Result(AdmissionKind.Deny, "系统采样无效，请稍后重试。", false);

        try { ValidateSnapshot(policy, sample.Total, sample.CommitLimit); }
        catch (InvalidDataException ex) { return Result(AdmissionKind.Deny, ex.Message, true); }

        if (!HasRoom(sample.Available, planningRequested, planningReserved, policy.PhysicalFloor))
            return Result(AdmissionKind.Deny, "现在可用内存不够：要给系统留余量。请关掉一些应用，或减少启动的 Agent 个数。", true);
        if (!HasRoom(sample.CommitAvailable, planningRequested, planningReserved, policy.CommitFloor))
            return Result(AdmissionKind.Deny, "系统内存余量不足，暂时不能新增任务。", true);
        if (sample.CpuPercent >= CpuBusyPercent)
            return Result(AdmissionKind.Deny, "整机 CPU 正忙，稍等片刻再启动。", true);

        if (includeSoftBudget && CombinedCapExceeds(policy, reserved, requested, soft))
        {
            if (policy.Mode == "conservative")
                return Result(AdmissionKind.Deny, "本次要用的内存超过总预算，请少勾选几个 Agent。", true);
            return Result(AdmissionKind.RequireWaiver, "按 Job 上限加总已超过软预算，需要一次性确认授权后才能继续。硬保护仍然有效。", true);
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
            PlanningBytes = item.PlanningBytes ?? 0
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
        if (sample.Total == 0 || sample.Available == 0 || sample.CommitLimit == 0 || sample.CommitAvailable == 0) return false;
        if (sample.Available > sample.Total || sample.CommitAvailable > sample.CommitLimit) return false;
        return true;
    }

    static ulong PlanningOf(ResourcePolicySnapshot policy, BudgetItem item) =>
        item.Kind == "git" ? policy.GitPlanningBytes : item.PlanningBytes == 0 ? DefaultPlanningBytes : item.PlanningBytes;

    static ulong CapOf(ResourcePolicySnapshot policy, BudgetItem item) =>
        item.Kind == "git" ? policy.GitJobMemoryLimit : item.JobMemoryLimit == 0 ? DefaultJobMemoryLimit : item.JobMemoryLimit;

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

    static bool CombinedCapExceeds(ResourcePolicySnapshot policy, IReadOnlyList<BudgetItem> reserved, IReadOnlyList<BudgetItem> requested, ulong cap)
    {
        var union = new List<BudgetItem>(reserved.Count + requested.Count);
        union.AddRange(reserved);
        union.AddRange(requested);
        return SumCap(policy, union) > cap;
    }

    static bool HasRoom(ulong available, ulong first, ulong second, ulong floor)
    {
        if (first > ulong.MaxValue - second) return false;
        var used = first + second;
        if (used > ulong.MaxValue - floor) return false;
        return available >= used + floor;
    }

    static ulong Min(ulong left, ulong right) => left < right ? left : right;
}
