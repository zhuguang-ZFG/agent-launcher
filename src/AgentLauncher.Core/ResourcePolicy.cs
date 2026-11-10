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

    public static Admission Check(MemorySnapshot sample, ulong reserved, ulong requested)
    {
        // Conservatively reserve the full budget again. Never subtract paged-out commit from physical headroom.
        if (!double.IsFinite(sample.CpuPercent) || DateTimeOffset.UtcNow - sample.At > TimeSpan.FromSeconds(3)) return new(false, "资源读数暂时不可信，稍候会自动重新检查。");
        if (reserved + requested > Math.Min(8 * GiB, sample.Total * 35 / 100)) return new(false, "本次要用的内存超过总预算，请少勾选几个 Agent。");
        if (sample.Available < Reserve(sample.Total) + reserved + requested) return new(false, "现在可用内存不够：要给系统留余量。请关掉一些应用，或减少启动的 Agent 个数。");
        if (sample.CommitAvailable < Math.Max(2 * GiB, sample.CommitLimit / 20) + reserved + requested) return new(false, "系统内存余量不足，暂时不能新增任务。");
        return sample.CpuPercent >= 80 ? new(false, "整机 CPU 正忙，稍等片刻再启动。") : new(true, "资源充足");
    }

    public static AdmissionDecision Evaluate(
        MemorySnapshot sample,
        ResourcePolicySnapshot policy,
        IReadOnlyList<BudgetItem> reserved,
        IReadOnlyList<BudgetItem> requested) =>
        throw new NotImplementedException("Evaluate is implemented in Task 2.");

    public static string PolicyHash(ResourcePolicySnapshot policy) =>
        throw new NotImplementedException("PolicyHash is implemented in Task 2.");

    public static void ValidateSnapshot(ResourcePolicySnapshot policy, ulong totalPhysical, ulong commitLimit) =>
        throw new NotImplementedException("ValidateSnapshot is implemented in Task 2.");
}
