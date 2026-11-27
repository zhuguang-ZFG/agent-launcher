using System.Security.Cryptography;
using System.Text.Json;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

public sealed record TaskHistoryArchive(int SchemaVersion, DateTimeOffset At, TaskCard[] Tasks, TaskRun[] Attempts, ControlEntry[] Evidence);

public sealed partial class TaskStore
{
    private string HistoryPath(Guid id) => Path.Combine(path + ".history", id.ToString("N") + ".json");

    public IReadOnlyList<TaskHistoryArchive> ReadHistoryArchives(TaskBook book)
    {
        var archives = new List<TaskHistoryArchive>();
        foreach (var reference in book.HistoryArchives)
        {
            var bytes = ReadBoundedBytes(HistoryPath(reference.Id));
            if (Convert.ToHexString(SHA256.HashData(bytes)) != reference.Sha256) throw new InvalidDataException("任务历史归档指纹不匹配，保留全部记录并停止派发。");
            var archive = JsonSerializer.Deserialize<TaskHistoryArchive>(bytes, Store.Json) ?? throw new InvalidDataException("任务历史归档为空。");
            if (archive.SchemaVersion != 1 || archive.Tasks is null || archive.Attempts is null || archive.Evidence is null || archive.Attempts.Length != reference.Count
                || archive.Tasks.Any(task => task is null) || archive.Tasks.Select(task => task.Id).Distinct().Count() != archive.Tasks.Length
                || archive.Attempts.Any(run => run is null || !run.Terminal) || archive.Attempts.Select(run => run.AttemptId).Distinct().Count() != archive.Attempts.Length
                || archive.Evidence.Any(entry => entry?.Plan is null) || archive.Evidence.Select(entry => entry.Plan.Id).Distinct().Count() != archive.Evidence.Length || archive.Evidence.Length != archive.Attempts.Length)
                throw new InvalidDataException("任务历史归档结构无效。");
            foreach (var task in archive.Tasks) task.Validate();
            foreach (var run in archive.Attempts)
            {
                var evidence = archive.Evidence.SingleOrDefault(entry => entry?.Plan?.Id == run.AttemptId);
                if (evidence is null || evidence.Digest != Store.Digest(evidence.Plan) || !Store.IsTerminal(evidence) || !AttemptIdentity.Matches(run, evidence))
                    throw new InvalidDataException("任务历史归档的退出与身份记录无法核对。");
            }
            archives.Add(archive);
        }
        return archives;
    }

    private void ValidateHistory(TaskBook book)
    {
        var ids = book.Attempts.Select(run => run.AttemptId).ToHashSet();
        foreach (var archive in ReadHistoryArchives(book))
            foreach (var run in archive.Attempts)
                if (!ids.Add(run.AttemptId)) throw new InvalidDataException("活动与归档尝试身份重复，请核对任务文件。");
    }

    /// <summary>Write and verify immutable evidence before removing old rows from the active book. Never edits control claims or occupancy.</summary>
    public TaskBook ArchiveHistory(long expectedRevision, Func<Guid, ControlEntry?> readEvidence)
    {
        return Update(expectedRevision, book =>
        {
            var retained = book.Attempts.GroupBy(run => run.TaskId).Select(group => group.Last().AttemptId).ToHashSet();
            // Completed dependency evidence and in-flight resume parents remain available to the scheduler.
            retained.UnionWith(book.Attempts.Where(run => run.State == "Completed").GroupBy(run => run.TaskId).Select(group => group.Last().AttemptId));
            retained.UnionWith(book.Queue.Where(entry => entry.ResumeOf.HasValue).Select(entry => entry.ResumeOf!.Value));
            retained.UnionWith(book.Attempts.Where(run => !run.Terminal && run.ResumeOf.HasValue).Select(run => run.ResumeOf!.Value));
            var candidates = new List<(TaskRun Run, ControlEntry Evidence)>();
            foreach (var run in book.Attempts.Where(run => (run.State is "Completed" or "Failed" or "Cancelled") && !retained.Contains(run.AttemptId)))
            {
                var evidence = readEvidence(run.AttemptId);
                if (evidence is null || !Store.IsTerminal(evidence) || evidence.Digest != Store.Digest(evidence.Plan) || !AttemptIdentity.Matches(run, evidence)) continue;
                candidates.Add((run, evidence));
                if (candidates.Count >= 200) break;
            }
            if (candidates.Count == 0) throw new InvalidDataException("没有可安全迁移的旧尝试。最近尝试、验收中、续接引用以及无法证明已退出的记录会保留；请先核对历史与诊断。");
            var ids = candidates.Select(item => item.Run.AttemptId).ToHashSet();
            var taskIds = candidates.Select(item => item.Run.TaskId).ToHashSet();
            var archive = new TaskHistoryArchive(1, DateTimeOffset.UtcNow, book.Tasks.Where(task => taskIds.Contains(task.Id)).Select(task => task.Copy()).ToArray(),
                candidates.Select(item => item.Run.Copy()).ToArray(), candidates.Select(item => item.Evidence).ToArray());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(archive, Store.Json);
            if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("本批历史归档超过 8 MiB，未修改活动记录；请联系维护者分批迁移。");
            var reference = new TaskHistoryReference(Guid.NewGuid(), Convert.ToHexString(SHA256.HashData(bytes)), candidates.Count);
            var archivePath = HistoryPath(reference.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            using (var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            // A crash before the active-book update leaves an extra archive, never lost evidence.
            var probe = new TaskBook { HistoryArchives = [reference] };
            _ = ReadHistoryArchives(probe);
            book.HistoryArchives.Add(reference);
            book.Attempts.RemoveAll(run => ids.Contains(run.AttemptId));
        });
    }
}
