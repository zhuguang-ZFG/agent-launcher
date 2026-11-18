namespace AgentLauncher.Core;

// 本启动器新建 Mutagen 会话时写入的模式。界面只引用这个常量。
// 不从 mutagen sync list 的全文猜测当前会话是哪一种模式。
public static class MutagenCreateMode
{
    public const string SyncMode = "two-way-resolved";
}

// 主路径上要给人看的短句。WPF 只负责摆放，不在界面里另写一套说法。
public static class LaunchSurface
{
    public const string LocalPlace = "本机";
    public const string ManyPlaces = "多个目标";
    public const string NotPeer = "这次不是同权。各机只看自己的目录。仍可启动。";
    public const string RemoteConnection = "云端通过 SSH 连接，终端里可以实时输入和看到输出。文件同步不是实时一致。";
    public const string ReattachIsNewPane = "重新挂接打开的是新的 SSH 窗格，不是原来的交互终端。";
    public const string ReattachEffect = "恢复到同一远程目录，打开新的 SSH 窗格，名额继续占用。不会自动重连，也不会释放名额。";
    public const string ReleaseEffect = "只交还这一条名额。不会结束远端进程，也不会重新挂接。";
    public const string LocalReconcileNote = "这是本机预约核对，不是远程名额释放，也不会结束远端进程。";
    public const string SharedDirectoryRisk = "大家在同一个文件夹里修改，能看到彼此的改动，也可能互相覆盖。";
    public const string ChangesAreLocalGit = "这是本机工作目录的 git 状态。能看到文件，只说明本机目录里有改动，不能证明京云、阿里云和本机已经同权。";

    public static string MutagenConflictText =>
        "本启动器新建的 Mutagen 会话使用 " + MutagenCreateMode.SyncMode + "。这不是已收敛：冲突时本机一侧会赢得修改，包括本机删除盖过另一端的修改。";

    public static string PlaceName(string? hostName) => string.IsNullOrWhiteSpace(hostName) ? LocalPlace : hostName.Trim();

    public static string TargetPhrase(IReadOnlyList<string?> hostNames)
    {
        var names = (hostNames ?? Array.Empty<string?>()).Select(PlaceName).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return LocalPlace;
        return names.Length == 1 ? names[0] : ManyPlaces;
    }

    public static string TargetTooltip(IReadOnlyList<string?> hostNames)
    {
        var names = (hostNames ?? Array.Empty<string?>()).Select(PlaceName).Distinct(StringComparer.Ordinal).ToArray();
        return names.Length <= 1 ? "" : "本次同时包含：" + string.Join("、", names);
    }

    public static string LaunchButtonText(int count, IReadOnlyList<string?> hostNames, bool busy, bool previewOnly)
    {
        if (busy) return "正在处理…";
        var verb = previewOnly ? "检查资源并启动" : "启动";
        return verb + " " + count + " 个 · " + TargetPhrase(hostNames);
    }

    // 只按主机上保存的同步模式取文案。调用方不要把 mutagen list 的原文传进来。
    public static string SyncHonesty(WorkspaceSyncMode mode) => mode switch
    {
        WorkspaceSyncMode.None => NotPeer + " " + RemoteConnection + " 这次没有文件同步。",
        WorkspaceSyncMode.Mutagen => RemoteConnection + " " + MutagenConflictText,
        WorkspaceSyncMode.SshfsMount => RemoteConnection + " SSHFS 是挂载读写，不是已收敛，也不是三台同权。",
        _ => RemoteConnection + " 同步方式无法确认，不把它标成已收敛。"
    };

    public static string TerminalFlushDetail(DateTimeOffset at) =>
        "已同步只表示该次结束时 flush 成功（sync-final.json " + at.ToString("u") + "）。这不表示当前文件已经一致。";

    public static string TerminalProblemDetail(DateTimeOffset at) =>
        "该次结束时 flush 没有成功（sync-final.json " + at.ToString("u") + "）。这不表示当前文件已经一致。";
}
