using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentLauncher.ManualRelay;

public static class RelaySchema
{
    public const int Current = 1;
}

public static class RelayJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    public static readonly JsonDocumentOptions Document = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32
    };
}

public sealed record TaskRecord(int SchemaVersion, string TaskId, string IdempotencyKey, string Kind, string PayloadCanonical, string PayloadSha256);
public sealed record LeaseRecord(string AttemptId, string HolderId, long StateVersion, DateTimeOffset ExpiresAt);
public sealed record TaskState(int SchemaVersion, string Status, long Version, LeaseRecord? Lease, string? ResultAttemptId, string? RejectionCode);
public sealed record RelayEvent(int SchemaVersion, DateTimeOffset At, string TaskId, string Type, string? AttemptId, string Detail);
public sealed record TaskView(string TaskId, string Status, long Version, string? AttemptId, string? RejectionCode, string Kind);
public sealed record FileDigest(string Path, string Sha256, long Bytes);
public sealed record ResultManifest(int SchemaVersion, string TaskId, string AttemptId, long StateVersion, FileDigest[] Files);
public sealed record CommitMarker(int SchemaVersion, string TaskId, string AttemptId, string ManifestSha256);
public sealed record LeaseOutcome(bool Accepted, string Code, TaskView View);
public sealed record SubmitOutcome(bool Accepted, string Code, TaskView View);

public sealed class KeyIndex
{
    public int SchemaVersion { get; set; } = RelaySchema.Current;
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
}

public static class Digests
{
    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

public static class PayloadRules
{
    static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "shell", "command", "args", "argv", "cmd", "exec", "password", "secret", "token",
        "privatekey", "private_key", "apikey", "api_key", "authorization", "credential", "credentials", "pem", "bearer"
    };
    static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    public static string Canonicalize(string json, string kind)
    {
        if (json.Length > 64 * 1024) throw new InvalidDataException("载荷超出大小边界。");
        JsonNode? node;
        try { node = JsonNode.Parse(json, documentOptions: RelayJson.Document); }
        catch (JsonException) { throw new InvalidDataException("载荷不是 JSON。"); }
        if (node is not JsonObject obj) throw new InvalidDataException("载荷必须是 JSON 对象。");
        try
        {
            RejectForbidden(node);
            ValidateShape(kind, obj);
            return Canonical(node);
        }
        catch (ArgumentException) { throw new InvalidDataException("拒绝重复字段。"); }
    }

    static void RejectForbidden(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (Forbidden.Contains(kv.Key)) throw new InvalidDataException("拒绝 shell 或凭据字段。");
                if (kv.Value is not null) RejectForbidden(kv.Value);
            }
            return;
        }
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (item is not null) RejectForbidden(item);
            return;
        }
        if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Contains("-----BEGIN", StringComparison.Ordinal))
            throw new InvalidDataException("拒绝凭据载荷。");
    }

    static void ValidateShape(string kind, JsonObject obj)
    {
        if (kind == "file_verify")
        {
            if (obj["files"] is not JsonArray files || files.Count is < 1 or > 20) throw new InvalidDataException("file_verify 需要 1 到 20 个文件。");
            foreach (var item in files)
            {
                if (item is not JsonObject file) throw new InvalidDataException("文件项无效。");
                if (file["path"] is not JsonValue pathValue || !pathValue.TryGetValue<string>(out var path)) throw new InvalidDataException("缺少路径。");
                if (file["sha256"] is not JsonValue shaValue || !shaValue.TryGetValue<string>(out var sha) || !Sha256Hex.IsMatch(sha)) throw new InvalidDataException("校验和格式无效。");
                SafePath.RejectRelative(path);
            }
            return;
        }
        if (kind == "result_data")
        {
            if (obj["data"] is not JsonObject) throw new InvalidDataException("result_data 需要 data 对象。");
            return;
        }
        if (kind == "agent")
        {
            if (obj.Count == 0) throw new InvalidDataException("agent 载荷为空。");
            return;
        }
        throw new InvalidDataException("任务类型不在白名单。");
    }

    static string Canonical(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var parts = obj.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => JsonSerializer.Serialize(kv.Key) + ":" + (kv.Value is null ? "null" : Canonical(kv.Value)));
            return "{" + string.Join(",", parts) + "}";
        }
        if (node is JsonArray arr)
            return "[" + string.Join(",", arr.Select(item => item is null ? "null" : Canonical(item))) + "]";
        return node.ToJsonString();
    }
}

public sealed class LocalRelay
{
    readonly string _root;
    readonly Func<DateTimeOffset> _clock;
    static readonly Regex IdPattern = new("^[A-Za-z0-9._-]{1,80}$", RegexOptions.CultureInvariant);

    public LocalRelay(string storeRoot, Func<DateTimeOffset> clock)
    {
        _root = Path.GetFullPath(storeRoot);
        Directory.CreateDirectory(_root);
        _clock = clock;
    }

    public string? FaultAfter { get; set; }

    public TaskView Enqueue(string idempotencyKey, string kind, string payloadJson)
    {
        ValidateId(idempotencyKey);
        if (kind is not ("file_verify" or "result_data" or "agent")) throw new InvalidDataException("任务类型不在白名单。");
        var canonical = PayloadRules.Canonicalize(payloadJson, kind);
        var digest = Digests.Sha256(Encoding.UTF8.GetBytes(kind + "\n" + canonical));
        using var indexGate = Lock(Path.Combine(_root, "index.lock"));
        var index = ReadIndex();
        if (index.Keys.TryGetValue(idempotencyKey, out var existingId))
        {
            using var existingGate = Lock(TaskLock(existingId));
            var existing = ReadRecord(existingId);
            if (existing.PayloadSha256 == digest && existing.Kind == kind) return Snapshot(existingId);
            Append(existingId, "idempotency_conflict", null, "冲突载荷已拒绝，未新建任务。");
            throw new InvalidDataException("同一幂等键的载荷冲突，已拒绝第二条任务。");
        }
        var taskId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(TaskDir(taskId));
        using var taskGate = Lock(TaskLock(taskId));
        AtomicWrite(Path.Combine(TaskDir(taskId), "task.json"), new TaskRecord(RelaySchema.Current, taskId, idempotencyKey, kind, canonical, digest));
        AtomicWrite(StatePath(taskId), new TaskState(RelaySchema.Current, "queued", 1, null, null, null));
        Append(taskId, "enqueued", null, kind);
        index.Keys[idempotencyKey] = taskId;
        AtomicWrite(Path.Combine(_root, "index.json"), index);
        return Snapshot(taskId);
    }

    public TaskView Read(string taskId)
    {
        ValidateId(taskId);
        using var gate = Lock(TaskLock(taskId));
        return Snapshot(taskId);
    }

    public LeaseOutcome TryLease(string taskId, string holderId, TimeSpan ttl)
    {
        ValidateId(taskId);
        ValidateId(holderId);
        if (ttl < TimeSpan.FromSeconds(1) || ttl > TimeSpan.FromMinutes(30)) throw new InvalidDataException("租约时长无效。");
        using var gate = Lock(TaskLock(taskId));
        var record = ReadRecord(taskId);
        var state = ReadState(taskId);
        if (Expire(taskId, ref state)) return new LeaseOutcome(false, "uncertain", Snapshot(taskId));
        if (state.Status == "needs_agent") return new LeaseOutcome(false, "needs_agent", Snapshot(taskId));
        if (state.Status != "queued") return new LeaseOutcome(false, "not_queued", Snapshot(taskId));
        if (record.Kind == "agent")
        {
            AtomicWrite(StatePath(taskId), state with { Status = "needs_agent", Version = state.Version + 1, Lease = null, RejectionCode = "needs_agent" });
            Append(taskId, "needs_agent", null, "已归类，未执行自然语言。");
            return new LeaseOutcome(false, "needs_agent", Snapshot(taskId));
        }
        var attemptId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(StagingPath(taskId, attemptId));
        var version = state.Version + 1;
        var leased = new TaskState(RelaySchema.Current, "leased", version, new LeaseRecord(attemptId, holderId, version, _clock().Add(ttl)), null, null);
        AtomicWrite(StatePath(taskId), leased);
        Append(taskId, "leased", attemptId, holderId);
        return new LeaseOutcome(true, "leased", Snapshot(taskId));
    }

    public string StagingDirectory(string taskId, string attemptId)
    {
        ValidateId(taskId);
        ValidateId(attemptId);
        using var gate = Lock(TaskLock(taskId));
        var state = ReadState(taskId);
        if (Expire(taskId, ref state)) throw new InvalidDataException("租约已到期。");
        if (state.Status != "leased" || state.Lease is null || state.Lease.AttemptId != attemptId) throw new InvalidDataException("attempt 与当前租约不一致。");
        var path = StagingPath(taskId, attemptId);
        Directory.CreateDirectory(path);
        return path;
    }

    public SubmitOutcome Submit(string taskId, string attemptId, long expectedVersion, IReadOnlyList<FileDigest> files)
    {
        ValidateId(taskId);
        ValidateId(attemptId);
        using var gate = Lock(TaskLock(taskId));
        var state = ReadState(taskId);
        if (state.Status == "succeeded" && state.ResultAttemptId == attemptId)
        {
            var priorPath = ManifestPath(taskId, attemptId);
            if (!File.Exists(priorPath)) return new SubmitOutcome(false, "result_conflict", Snapshot(taskId));
            var prior = ReadJson<ResultManifest>(priorPath);
            if (SameFiles(prior.Files, files) && CheckFiles(taskId, attemptId, files) is null)
            {
                Append(taskId, "duplicate_submit", attemptId, "相同结果已存在。");
                return new SubmitOutcome(true, "duplicate", Snapshot(taskId));
            }
            Append(taskId, "submit_rejected", attemptId, "结果冲突。");
            return new SubmitOutcome(false, "result_conflict", Snapshot(taskId));
        }
        if (Expire(taskId, ref state)) return new SubmitOutcome(false, "lease_expired", Snapshot(taskId));
        if (state.Status != "leased" || state.Lease is null) return new SubmitOutcome(false, "not_leased", Snapshot(taskId));
        if (state.Lease.AttemptId != attemptId) return new SubmitOutcome(false, "attempt_mismatch", Snapshot(taskId));
        if (state.Version != expectedVersion) return new SubmitOutcome(false, "version_mismatch", Snapshot(taskId));
        if (CheckFiles(taskId, attemptId, files) is { } problem) return new SubmitOutcome(false, problem, Snapshot(taskId));
        var manifest = new ResultManifest(RelaySchema.Current, taskId, attemptId, state.Version, files.ToArray());
        AtomicWrite(ManifestPath(taskId, attemptId), manifest);
        Hit("before-commit");
        var marker = new CommitMarker(RelaySchema.Current, taskId, attemptId, Digests.Sha256(File.ReadAllBytes(ManifestPath(taskId, attemptId))));
        AtomicWrite(CommitPath(taskId, attemptId), marker);
        Hit("after-commit");
        AtomicWrite(StatePath(taskId), state with { Status = "succeeded", Version = state.Version + 1, ResultAttemptId = attemptId, RejectionCode = null });
        Append(taskId, "submitted", attemptId, "结果已提交。");
        return new SubmitOutcome(true, "submitted", Snapshot(taskId));
    }

    public TaskView Recover(string taskId)
    {
        ValidateId(taskId);
        using var gate = Lock(TaskLock(taskId));
        var state = ReadState(taskId);
        if (state.Status == "succeeded") return Snapshot(taskId);
        var attemptId = state.Lease?.AttemptId;
        if (attemptId is null) return Snapshot(taskId);
        if (ValidCommit(taskId, attemptId))
        {
            if (state.Status is "leased" or "uncertain")
            {
                AtomicWrite(StatePath(taskId), state with { Status = "succeeded", Version = state.Version + 1, ResultAttemptId = attemptId, RejectionCode = null });
                Append(taskId, "recovery_submitted", attemptId, "已按完整提交标记补记，未重跑。");
            }
            return Snapshot(taskId);
        }
        if (state.Status == "leased" && Incomplete(taskId, attemptId))
        {
            AtomicWrite(StatePath(taskId), state with { Status = "uncertain", Version = state.Version + 1, RejectionCode = "submit_interrupted" });
            Append(taskId, "uncertain", attemptId, "提交中断，未重跑。");
        }
        return Snapshot(taskId);
    }

    public TaskView ConfirmUncertain(string taskId, string confirmation)
    {
        ValidateId(taskId);
        if (confirmation != "human-confirmed") throw new InvalidDataException("需要明确的人工确认。");
        using var gate = Lock(TaskLock(taskId));
        var state = ReadState(taskId);
        if (state.Status != "uncertain") throw new InvalidDataException("只有 uncertain 可以确认新尝试。");
        AtomicWrite(StatePath(taskId), new TaskState(RelaySchema.Current, "queued", state.Version + 1, null, null, null));
        Append(taskId, "human_confirmed", state.Lease?.AttemptId, "人工确认后允许新的尝试。");
        return Snapshot(taskId);
    }

    void Hit(string phase)
    {
        if (string.Equals(FaultAfter, phase, StringComparison.Ordinal)) throw new IOException("注入的提交中断：" + phase);
    }

    string? CheckFiles(string taskId, string attemptId, IReadOnlyList<FileDigest> files)
    {
        if (files.Count is < 1 or > 20) return "path_rejected";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var staging = Path.GetFullPath(StagingPath(taskId, attemptId));
        foreach (var file in files)
        {
            if (!SingleName(file.Path) || !seen.Add(file.Path)) return "path_rejected";
            var full = Path.GetFullPath(Path.Combine(staging, file.Path));
            if (!SafePath.IsUnder(staging, full) || !File.Exists(full) || SafePath.IsLink(full)) return "path_rejected";
            if (new FileInfo(full).Length != file.Bytes || !string.Equals(Digests.Sha256File(full), file.Sha256, StringComparison.Ordinal)) return "checksum_mismatch";
        }
        return null;
    }

    bool ValidCommit(string taskId, string attemptId)
    {
        var commitPath = CommitPath(taskId, attemptId);
        var manifestPath = ManifestPath(taskId, attemptId);
        if (!File.Exists(commitPath) || !File.Exists(manifestPath)) return false;
        try
        {
            var marker = ReadJson<CommitMarker>(commitPath);
            var manifest = ReadJson<ResultManifest>(manifestPath);
            if (marker.SchemaVersion != RelaySchema.Current || manifest.SchemaVersion != RelaySchema.Current) return false;
            if (marker.TaskId != taskId || marker.AttemptId != attemptId || manifest.TaskId != taskId || manifest.AttemptId != attemptId) return false;
            if (!string.Equals(marker.ManifestSha256, Digests.Sha256(File.ReadAllBytes(manifestPath)), StringComparison.Ordinal)) return false;
            return manifest.Files is { Length: > 0 } && CheckFiles(taskId, attemptId, manifest.Files) is null;
        }
        catch (InvalidDataException) { return false; }
    }

    bool Incomplete(string taskId, string attemptId) =>
        !ValidCommit(taskId, attemptId) && (File.Exists(ManifestPath(taskId, attemptId)) || File.Exists(CommitPath(taskId, attemptId)) || File.Exists(Path.Combine(StagingPath(taskId, attemptId), "result.json")));

    static bool SameFiles(IReadOnlyList<FileDigest> prior, IReadOnlyList<FileDigest> files)
    {
        if (prior.Count != files.Count) return false;
        foreach (var file in files)
            if (!prior.Any(item => item.Path == file.Path && item.Sha256 == file.Sha256 && item.Bytes == file.Bytes)) return false;
        return true;
    }

    static bool SingleName(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('/') || path.Contains('\\') || path.Contains(':')) return false;
        try { SafePath.RejectRelative(path); return true; }
        catch (InvalidDataException) { return false; }
    }

    string ManifestPath(string taskId, string attemptId) => Path.Combine(TaskDir(taskId), "attempts", attemptId, "manifest.json");
    string CommitPath(string taskId, string attemptId) => Path.Combine(TaskDir(taskId), "attempts", attemptId, "commit.json");

    bool Expire(string taskId, ref TaskState state)
    {
        if (state.Status != "leased" || state.Lease is null || _clock() < state.Lease.ExpiresAt) return false;
        var attemptId = state.Lease.AttemptId;
        state = state with { Status = "uncertain", Version = state.Version + 1, RejectionCode = "lease_expired" };
        AtomicWrite(StatePath(taskId), state);
        Append(taskId, "uncertain", attemptId, "租约到期，未重跑。");
        return true;
    }

    string StagingPath(string taskId, string attemptId) => Path.Combine(TaskDir(taskId), "attempts", attemptId, "staging");

    public static void ValidateId(string id)
    {
        if (!IdPattern.IsMatch(id)) throw new InvalidDataException("标识无效。");
    }

    string TaskDir(string taskId) => Path.Combine(_root, "tasks", taskId);
    string StatePath(string taskId) => Path.Combine(TaskDir(taskId), "state.json");
    string TaskLock(string taskId) => Path.Combine(TaskDir(taskId), "writer.lock");

    KeyIndex ReadIndex()
    {
        var path = Path.Combine(_root, "index.json");
        if (!File.Exists(path)) return new KeyIndex();
        var index = ReadJson<KeyIndex>(path);
        EnsureSchema(index.SchemaVersion);
        index.Keys ??= new Dictionary<string, string>(StringComparer.Ordinal);
        return index;
    }

    public TaskRecord ReadRecord(string taskId)
    {
        ValidateId(taskId);
        var record = ReadJson<TaskRecord>(Path.Combine(TaskDir(taskId), "task.json"));
        EnsureSchema(record.SchemaVersion);
        return record;
    }

    public string? NextQueued()
    {
        using var gate = Lock(Path.Combine(_root, "index.lock"));
        var root = Path.Combine(_root, "tasks");
        if (!Directory.Exists(root)) return null;
        foreach (var dir in Directory.GetDirectories(root).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(dir);
            if (!File.Exists(StatePath(id))) continue;
            if (ReadState(id).Status == "queued") return id;
        }
        return null;
    }

    public TaskView Reject(string taskId, string attemptId, long expectedVersion, string code)
    {
        ValidateId(taskId);
        ValidateId(attemptId);
        if (code != "path_rejected") throw new InvalidDataException("拒绝码无效。");
        using var gate = Lock(TaskLock(taskId));
        var state = ReadState(taskId);
        if (state.Status != "leased" || state.Lease is null || state.Lease.AttemptId != attemptId || state.Version != expectedVersion) return Snapshot(taskId);
        AtomicWrite(StatePath(taskId), state with { Status = "rejected", Version = state.Version + 1, RejectionCode = code });
        Append(taskId, "rejected", attemptId, code);
        return Snapshot(taskId);
    }

    TaskState ReadState(string taskId)
    {
        var state = ReadJson<TaskState>(StatePath(taskId));
        EnsureSchema(state.SchemaVersion);
        return state;
    }

    TaskView Snapshot(string taskId)
    {
        var record = ReadRecord(taskId);
        var state = ReadState(taskId);
        return new TaskView(taskId, state.Status, state.Version, state.Lease?.AttemptId ?? state.ResultAttemptId, state.RejectionCode, record.Kind);
    }

    void Append(string taskId, string type, string? attemptId, string detail)
    {
        var line = JsonSerializer.Serialize(new RelayEvent(RelaySchema.Current, _clock(), taskId, type, attemptId, detail), RelayJson.Options) + "\n";
        using var stream = new FileStream(Path.Combine(TaskDir(taskId), "events.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(new UTF8Encoding(false).GetBytes(line));
        stream.Flush(true);
    }

    static void EnsureSchema(int version)
    {
        if (version != RelaySchema.Current) throw new InvalidDataException($"记录版本 {version} 不受支持，已拒绝覆盖。");
    }

    static T ReadJson<T>(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidDataException("记录不存在。");
        if (info.Length > 8 * 1024 * 1024) throw new InvalidDataException("记录超出大小边界。");
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<T>(stream, RelayJson.Options) ?? throw new InvalidDataException("记录为空。");
    }

    static void AtomicWrite(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, RelayJson.Options);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    static ExclusiveLock Lock(string path) => new(path);

    sealed class ExclusiveLock : IDisposable
    {
        readonly Mutex _mutex;
        readonly bool _owned;
        public ExclusiveLock(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var name = "almr-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..32];
            _mutex = new Mutex(false, name);
            try { _owned = _mutex.WaitOne(); }
            catch (AbandonedMutexException) { _owned = true; }
        }
        public void Dispose()
        {
            if (_owned) _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
