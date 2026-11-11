namespace AgentLauncher.Core;

public sealed class ResourceSettings
{
    public string Mode { get; set; } = "practical";
    public ulong JobMemoryLimit { get; set; } = ResourcePolicy.DefaultJobMemoryLimit;
    public ulong PlanningBytes { get; set; } = ResourcePolicy.DefaultPlanningBytes;
    public ulong? PhysicalFloorOverride { get; set; }
    public ulong? CommitFloorOverride { get; set; }
    public int CpuRate { get; set; } = ResourcePolicy.DefaultCpuRate;
    public ResourceSettings Copy() => new()
    {
        Mode = Mode,
        JobMemoryLimit = JobMemoryLimit,
        PlanningBytes = PlanningBytes,
        PhysicalFloorOverride = PhysicalFloorOverride,
        CommitFloorOverride = CommitFloorOverride,
        CpuRate = CpuRate
    };
}

public sealed record ResourcePolicySnapshot(
    string Mode, ulong JobMemoryLimit, ulong PlanningBytes,
    ulong PhysicalFloor, ulong CommitFloor, int CpuRate,
    ulong GitJobMemoryLimit, ulong GitPlanningBytes);

public sealed record BudgetItem(Guid Id, string Kind, ulong PlanningBytes, ulong JobMemoryLimit);

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
            DefaultGitPlanningBytes);
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

        if (includeSoftBudget && SumExceeds(capRequested, capReserved, soft))
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
        var payload = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{\"CommitFloor\":{policy.CommitFloor},\"CpuRate\":{policy.CpuRate},\"GitJobMemoryLimit\":{policy.GitJobMemoryLimit},\"GitPlanningBytes\":{policy.GitPlanningBytes},\"JobMemoryLimit\":{policy.JobMemoryLimit},\"Mode\":{mode},\"PhysicalFloor\":{policy.PhysicalFloor},\"PlanningBytes\":{policy.PlanningBytes}}}");
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

    public static BudgetItem BudgetOf(LaunchItem plan)
    {
        var git = plan.Kind == "git";
        return new(
            plan.Id,
            git ? "git" : (plan.Kind ?? "agent"),
            git ? DefaultGitPlanningBytes : plan.PlanningBytes is null or 0 ? DefaultPlanningBytes : plan.PlanningBytes.Value,
            git ? GitJobMemoryLimit : plan.Budget);
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
        foreach (var item in items)
        {
            var value = CapOf(policy, item);
            if (sum > ulong.MaxValue - value) return ulong.MaxValue;
            sum += value;
        }
        return sum;
    }

    static bool HasRoom(ulong available, ulong first, ulong second, ulong floor)
    {
        if (first > ulong.MaxValue - second) return false;
        var used = first + second;
        if (used > ulong.MaxValue - floor) return false;
        return available >= used + floor;
    }

    static bool SumExceeds(ulong first, ulong second, ulong cap)
    {
        if (first > ulong.MaxValue - second) return true;
        return first + second > cap;
    }

    static ulong Min(ulong left, ulong right) => left < right ? left : right;
}
