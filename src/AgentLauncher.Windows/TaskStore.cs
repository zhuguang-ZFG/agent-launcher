using System.Security.Cryptography;
using System.Text;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

/// <summary>Task book persistence. Read-modify-write runs under one cross-process gate so two windows cannot lose updates.</summary>
public sealed class TaskStore
{
    private readonly string path;
    public TaskStore(string path) => this.path = System.IO.Path.GetFullPath(path);
    public string FilePath => path;
    public TaskBook Load()
    {
        var book = Store.Read<TaskBook>(path) ?? new TaskBook();
        // Version 1 predates quick-launch metadata; an absent field means "not a quick launch", so upgrade in memory only.
        // The file is rewritten as version 2 exclusively by the normal Update path after the caller's expected revision matches.
        if (book.SchemaVersion == 1) book.SchemaVersion = TaskSchema.Current;
        book.Validate();
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
            Store.Save(path, book);
            return book;
        }
        finally { mutex.ReleaseMutex(); }
    }
}
