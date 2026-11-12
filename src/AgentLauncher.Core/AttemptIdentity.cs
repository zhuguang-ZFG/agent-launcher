namespace AgentLauncher.Core;

public static class AttemptIdentity
{
    // Generic retries must never detach a queue attempt from its concurrency and run history.
    // Check durable claims and legacy task/run evidence along the whole retry chain. Missing ancestry is not proof of a quick launch.
    public static bool CanRetryOutsideQueue(LaunchItem plan, IEnumerable<LaunchItem> history, TaskBook book, IEnumerable<TaskReservationClaim>? claims = null)
    {
        var plans = history.ToDictionary(item => item.Id);
        var claimed = (claims ?? []).Select(claim => claim.AttemptId).ToHashSet();
        var seen = new HashSet<Guid>();
        var current = plan;
        while (seen.Add(current.Id))
        {
            if (claimed.Contains(current.Id) || book.Attempts.Any(run => run.AttemptId == current.Id)
                || book.Tasks.Any(task => task.Id == current.SourceSlotId)) return false;
            if (current.RetryOf is not { } parent) return true;
            if (claimed.Contains(parent) || book.Attempts.Any(run => run.AttemptId == parent)) return false;
            if (!plans.TryGetValue(parent, out var ancestor)) return false;
            current = ancestor;
        }
        return false;
    }

    // A same-named file, an old receipt, or a result from another batch is not this task's completion evidence.
    public static bool Matches(TaskRun run,ControlEntry entry)=>entry.Plan.Id==run.AttemptId&&entry.Plan.BatchId==run.BatchId
        &&entry.Plan.SourceSlotId==run.TaskId&&entry.Plan.Workspace.Key==run.WorkspaceKey&&!string.IsNullOrWhiteSpace(entry.Digest);
}
