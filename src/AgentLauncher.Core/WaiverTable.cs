namespace AgentLauncher.Core;

public sealed class WaiverTable
{
    private readonly Func<DateTimeOffset> now;
    private readonly Dictionary<Guid, Grant> grants = [];

    private sealed record Grant(Guid Id, DateTimeOffset ExpiresAt, long DraftRevision, string PolicyHash, string PlanDigest, Guid[] EnabledSlotIds);

    public WaiverTable(Func<DateTimeOffset> now) => this.now = now ?? throw new ArgumentNullException(nameof(now));

    public Response Issue(AdmissionDecision current, long draftRevision, string policyHash, string planDigest, IReadOnlyList<Guid> enabledSlotIds, DateTimeOffset sampleAt)
    {
        if (current.Kind != AdmissionKind.RequireWaiver)
            return new(false, current.Reason, Decision: current.Kind.ToString());
        var issuedAt = now();
        var grant = new Grant(Guid.NewGuid(), issuedAt.AddSeconds(ResourcePolicy.WaiverTtlSeconds), draftRevision, policyHash ?? "", planDigest ?? "", [.. enabledSlotIds ?? []]);
        grants[grant.Id] = grant;
        return new(true, current.Reason, WaiverId: grant.Id, WaiverExpiresAt: grant.ExpiresAt, Decision: current.Kind.ToString());
    }

    public bool Consume(Guid id, long draftRevision, string policyHash, string planDigest, IReadOnlyList<Guid> enabledSlotIds)
    {
        if (!grants.Remove(id, out var grant)) return false;
        if (now() > grant.ExpiresAt) return false;
        if (grant.DraftRevision != draftRevision) return false;
        if (!string.Equals(grant.PolicyHash, policyHash ?? "", StringComparison.Ordinal)) return false;
        if (!string.Equals(grant.PlanDigest, planDigest ?? "", StringComparison.Ordinal)) return false;
        return grant.EnabledSlotIds.SequenceEqual(enabledSlotIds ?? []);
    }
}
