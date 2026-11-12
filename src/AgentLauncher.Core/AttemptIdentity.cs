namespace AgentLauncher.Core;

public static class AttemptIdentity
{
    // A same-named file, an old receipt, or a result from another batch is not this task's completion evidence.
    public static bool Matches(TaskRun run,ControlEntry entry)=>entry.Plan.Id==run.AttemptId&&entry.Plan.BatchId==run.BatchId
        &&entry.Plan.SourceSlotId==run.TaskId&&entry.Plan.Workspace.Key==run.WorkspaceKey&&!string.IsNullOrWhiteSpace(entry.Digest);
}
