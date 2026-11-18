namespace AgentLauncher.Core;

// 操作员确认的断线重新挂接（R33）。自动重连不在这里。
// 探针与挂接命令都是纯字符串：监视器负责真正执行 ssh，测试不需要真实 VPS。
public static class RemoteReattach
{
    public const string ProbeMarker = "agent-launcher-reattach-probe";
    public const string AttachMarker = "agent-launcher-reattach-attach";

    public static string AttemptToken(Guid attemptId) => "AGENT_LAUNCHER_ATTEMPT=" + attemptId.ToString("N");

    // None 不是双向同步。只有原来启用了 Mutagen 或 SSHFS 才在重新挂接时恢复同步。
    public static bool RestoresSync(WorkspaceSyncMode? mode) => mode is WorkspaceSyncMode.Mutagen or WorkspaceSyncMode.SshfsMount;

    public static string ProbeShell(Guid attemptId)
    {
        var token = AttemptToken(attemptId);
        return "echo " + ProbeMarker + "; "
            + "found=0; "
            + "for envf in /proc/[0-9]*/environ; do "
            + "if tr '\\0' '\\n' < \"$envf\" 2>/dev/null | grep -qx '" + token + "'; then found=1; break; fi; "
            + "done; "
            + "if [ \"$found\" = 1 ]; then echo ALIVE; else echo GONE; fi";
    }

    // 挂上同一远程目录的本地 SSH。先确认本次尝试的进程仍在，再进入该目录的 shell。
    // 不调用 agent 命令，避免在仍存活的远程进程旁边再启动一个。
    public static string AttachShell(Guid attemptId, string remoteDirectory)
    {
        var token = AttemptToken(attemptId);
        var dir = "'" + remoteDirectory.Replace("'", "'\\''") + "'";
        return "echo " + AttachMarker + "; "
            + "found=0; "
            + "for envf in /proc/[0-9]*/environ; do "
            + "if tr '\\0' '\\n' < \"$envf\" 2>/dev/null | grep -qx '" + token + "'; then found=1; break; fi; "
            + "done; "
            + "if [ \"$found\" != 1 ]; then echo GONE; exit 3; fi; "
            + "cd " + dir + " || exit 3; "
            + "echo '已重新挂接：远程进程仍在。此窗格是本地 SSH，不会另起 Agent。'; "
            + "exec bash --noprofile --norc";
    }

    public static string ClassifyProbe(int exitCode, bool timedOut, string? stdout)
    {
        if (timedOut) return "timeout";
        if (exitCode != 0) return "error";
        var alive = false;
        var gone = false;
        foreach (var line in (stdout ?? "").Split('\n'))
        {
            var text = line.Trim();
            if (text == "ALIVE") alive = true;
            else if (text == "GONE") gone = true;
        }
        if (alive && !gone) return "alive";
        if (gone && !alive) return "gone";
        return "unknown";
    }
}
