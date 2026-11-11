namespace AgentLauncher.Core;

public sealed class WaiverTable
{
    public const int MaxActive = 32;
    private readonly Func<DateTimeOffset> now;
    private readonly Dictionary<Guid, Grant> grants = [];

    private sealed record Grant(Guid Id, DateTimeOffset ExpiresAt, long DraftRevision, string PolicyHash, string PlanDigest, Guid[] EnabledSlotIds, DateTimeOffset SampleAt, string ConfigurationIdentity);

    public WaiverTable(Func<DateTimeOffset> now) => this.now = now ?? throw new ArgumentNullException(nameof(now));

    public int Count
    {
        get
        {
            PruneExpired();
            return grants.Count;
        }
    }

    public int PruneExpired()
    {
        var expired = grants.Where(pair => now() > pair.Value.ExpiresAt).Select(pair => pair.Key).ToArray();
        foreach (var id in expired) grants.Remove(id);
        return expired.Length;
    }

    public void InvalidateMismatched(long draftRevision, string policyHash, string? configurationIdentity = null)
    {
        PruneExpired();
        var stale = grants.Where(pair => pair.Value.DraftRevision != draftRevision || !string.Equals(pair.Value.PolicyHash, policyHash ?? "", StringComparison.Ordinal) || !string.Equals(pair.Value.ConfigurationIdentity, configurationIdentity ?? "", StringComparison.Ordinal)).Select(pair => pair.Key).ToArray();
        foreach (var id in stale) grants.Remove(id);
    }

    public void InvalidatePlan(string planDigest)
    {
        var related = grants.Where(pair => string.Equals(pair.Value.PlanDigest, planDigest ?? "", StringComparison.Ordinal)).Select(pair => pair.Key).ToArray();
        foreach (var id in related) grants.Remove(id);
    }

    public void Invalidate(Guid id) => grants.Remove(id);

    public Response Issue(AdmissionDecision current, long draftRevision, string policyHash, string planDigest, IReadOnlyList<Guid> enabledSlotIds, DateTimeOffset sampleAt, string? configurationIdentity = null)
    {
        PruneExpired();
        if (current.Kind != AdmissionKind.RequireWaiver)
            return new(false, current.Reason, Decision: current.Kind.ToString());
        if (grants.Count >= MaxActive)
            return new(false, "确认授权表已满，请稍后重试。", Decision: current.Kind.ToString());
        var issuedAt = now();
        var grant = new Grant(Guid.NewGuid(), issuedAt.AddSeconds(ResourcePolicy.WaiverTtlSeconds), draftRevision, policyHash ?? "", planDigest ?? "", [.. enabledSlotIds ?? []], sampleAt, configurationIdentity ?? "");
        grants[grant.Id] = grant;
        return new(true, current.Reason, WaiverId: grant.Id, WaiverExpiresAt: grant.ExpiresAt, Decision: current.Kind.ToString());
    }

    public bool Consume(Guid id, long draftRevision, string policyHash, string planDigest, IReadOnlyList<Guid> enabledSlotIds, string? configurationIdentity = null)
    {
        PruneExpired();
        if (!grants.Remove(id, out var grant)) return false;
        if (now() > grant.ExpiresAt) return false;
        if (grant.DraftRevision != draftRevision) return false;
        if (!string.Equals(grant.PolicyHash, policyHash ?? "", StringComparison.Ordinal)) return false;
        if (!string.Equals(grant.PlanDigest, planDigest ?? "", StringComparison.Ordinal)) return false;
        if (!string.Equals(grant.ConfigurationIdentity, configurationIdentity ?? "", StringComparison.Ordinal)) return false;
        return grant.EnabledSlotIds.SequenceEqual(enabledSlotIds ?? []);
    }
}