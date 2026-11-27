using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

/// <summary>Task book persistence. Read-modify-write runs under one cross-process gate so two windows cannot lose updates.</summary>
public sealed partial class TaskStore
{
    private readonly string path;
    public TaskStore(string path) => this.path = System.IO.Path.GetFullPath(path);
    public string FilePath => path;
    public string SnapshotDirectory => path + ".snapshots";
    public string BackupFingerprint() => Convert.ToHexString(SHA256.HashData(ReadBoundedBytes(path + ".bak")));

    public TaskBook ReadBackup()
    {
        var book = Store.Read<TaskBook>(path + ".bak") ?? throw new IOException("没有可读取的任务备份。");
        if (book.SchemaVersion is 1 or 2 or 3 or 4) book.SchemaVersion = TaskSchema.Current;
        book.Validate();
        ValidateHistory(book);
        return book;
    }

    public TaskBook ReadSnapshot(string fingerprint)
    {
        var bytes = ReadBoundedBytes(SnapshotPath(fingerprint));
        if (Convert.ToHexString(SHA256.HashData(bytes)) != fingerprint.ToUpperInvariant()) throw new InvalidDataException("任务快照指纹不匹配。");
        var book = JsonSerializer.Deserialize<TaskBook>(bytes, Store.Json) ?? throw new InvalidDataException("任务快照为空。");
        if (book.SchemaVersion is 1 or 2 or 3 or 4) book.SchemaVersion = TaskSchema.Current;
        book.Validate(); ValidateHistory(book); return book;
    }

    private string SnapshotPath(string fingerprint)
    {
        if (fingerprint.Length != 64 || fingerprint.Any(ch => !Uri.IsHexDigit(ch))) throw new InvalidDataException("快照指纹无效。");
        return Path.Combine(SnapshotDirectory, fingerprint.ToUpperInvariant() + ".json");
    }

    /// <summary>Explicit restore of a verified immutable snapshot. Preserve the current primary and .bak; callers keep dispatch paused.</summary>
    public TaskBook RestoreSnapshot(string fingerprint, long? expectedRevision = null)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];
        using var mutex = new Mutex(false, @"Local\AgentLauncher-tasks-" + Store.Scope + "-" + key);
        bool owned;
        try { owned = mutex.WaitOne(5000); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new IOException("任务正在被其他窗口修改，请稍后重试。");
        try
        {
            TaskBook? current = null;
            try { current = Load(); } catch (Exception e) when (e is IOException or JsonException or InvalidDataException) { }
            if (current is not null && current.Revision != expectedRevision) throw new IOException("任务主文件已变化，请刷新并明确选择要恢复的快照。");
            var book = ReadSnapshot(fingerprint);
            SnapshotPrimary();
            book.Revision = Math.Max(checked(Math.Max(book.Revision, current?.Revision ?? 0) + 1), DateTimeOffset.UtcNow.UtcTicks);
            var staged = path + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Store.Save(staged, book);
                if (File.Exists(path)) File.Replace(staged, path, null); else File.Move(staged, path);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
            return book;
        }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>Explicit recovery of an unreadable primary. Preserve the original and the verified backup; dispatch remains paused.</summary>
    public TaskBook RestoreBackup(string expectedFingerprint)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];
        using var mutex = new Mutex(false, @"Local\AgentLauncher-tasks-" + Store.Scope + "-" + key);
        bool owned;
        try { owned = mutex.WaitOne(5000); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new IOException("任务正在被其他窗口修改，请稍后重试。");
        try
        {
            try { Load(); throw new InvalidOperationException("任务主文件可正常读取，无需恢复备份。"); }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException) { }
            var backupBytes = ReadBoundedBytes(path + ".bak");
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(backupBytes)), expectedFingerprint, StringComparison.Ordinal)) throw new IOException("任务备份已变化，请刷新并重新核对。");
            var book = JsonSerializer.Deserialize<TaskBook>(backupBytes, Store.Json) ?? throw new InvalidDataException("任务备份为空。");
            if (book.SchemaVersion is 1 or 2 or 3 or 4) book.SchemaVersion = TaskSchema.Current;
            book.Validate();
            ValidateHistory(book);
            book.Revision = Math.Max(checked(book.Revision + 1), DateTimeOffset.UtcNow.UtcTicks);
            SnapshotPrimary();
            if (File.Exists(path))
            {
                var original = ReadBoundedBytes(path);
                var saved = path + ".unreadable-" + Guid.NewGuid().ToString("N") + ".json";
                using var stream = new FileStream(saved, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                stream.Write(original); stream.Flush(true);
            }
            var staged = path + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Store.Save(staged, book);
                if (File.Exists(path)) File.Replace(staged, path, null); else File.Move(staged, path);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
            return book;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static byte[] ReadBoundedBytes(string file)
    {
        using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("记录超出大小边界。");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    // Content names make repeated saves idempotent; no existing snapshot is overwritten or automatically pruned.
    private void SnapshotPrimary()
    {
        if (!File.Exists(path)) return;
        var bytes = ReadBoundedBytes(path);
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        Directory.CreateDirectory(SnapshotDirectory);
        var snapshotPath = Path.Combine(SnapshotDirectory, digest + ".json");
        if (File.Exists(snapshotPath))
        {
            if (bytes.SequenceEqual(ReadBoundedBytes(snapshotPath))) return;
            // Preserve interrupted legacy snapshots without letting a partial final name block all future saves.
            File.Move(snapshotPath, snapshotPath + ".incomplete-" + Guid.NewGuid().ToString("N"));
        }
        var staged = snapshotPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(staged, snapshotPath);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
    public TaskBook Load()
    {
        var book = Store.Read<TaskBook>(path);
        if (book is null && File.Exists(path + ".bak"))
            throw new InvalidDataException("任务主文件缺失，但仍有 .bak 备份；队列已停用。请先人工核对并恢复主文件，不会自动覆盖备份。");
        if (book is null && Directory.Exists(SnapshotDirectory) && Directory.EnumerateFiles(SnapshotDirectory).Any())
            throw new InvalidDataException("任务主文件缺失，但仍有历史快照；队列已停用。请在恢复与诊断中核对并恢复快照，不会创建空任务文件。");
        book ??= new TaskBook();
        // Old books have neither archived cards nor deferred intents. Upgrade only in memory until a successful edit.
        if (book.SchemaVersion is 1 or 2 or 3 or 4) book.SchemaVersion = TaskSchema.Current;
        book.Validate();
        ValidateHistory(book);
        return book;
    }
    /// <summary>Single-dispatch gate across app windows. Returns null when another window is already dispatching.</summary>
    public IDisposable? TryAcquireQueueGate()
    {
        var mutex = new Mutex(false, @"Local\AgentLauncher-queue-" + Store.Scope);
        bool owned;
        try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) { mutex.Dispose(); return null; }
        return new QueueGate(mutex);
    }
    private sealed class QueueGate(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
            mutex.Dispose();
        }
    }
    public TaskBook Update(long expectedRevision, Action<TaskBook> change)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];
        using var mutex = new Mutex(false, @"Local\AgentLauncher-tasks-" + Store.Scope + "-" + key);
        bool owned;
        try { owned = mutex.WaitOne(5000); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new IOException("任务文件正在被另一个窗口修改，请稍后重试。");
        try
        {
            var book = Load();
            if (book.Revision != expectedRevision) throw new IOException("任务文件已被其他窗口修改，请重新核对后再试。");
            change(book);
            book.Revision = checked(book.Revision + 1);
            book.Validate();
            SnapshotPrimary();
            Store.Save(path, book);
            return book;
        }
        finally { mutex.ReleaseMutex(); }
    }
}
