namespace AgentLauncher.ManualRelay;

public static class SafePath
{
    public static void RejectRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("路径为空。");
        if (relative.Contains(':') || Path.IsPathRooted(relative)) throw new InvalidDataException("拒绝绝对路径。");
        foreach (var segment in relative.Replace('\\', '/').Split('/'))
        {
            if (segment is "" or "." or "..") throw new InvalidDataException("拒绝越权路径。");
        }
    }

    public static string ResolveExisting(string root, string relative)
    {
        RejectRelative(relative);
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot)) throw new InvalidDataException("隔离目录不存在。");
        var cursor = fullRoot;
        foreach (var segment in relative.Replace('\\', '/').Split('/'))
        {
            cursor = Path.Combine(cursor, segment);
            if (!Exists(cursor)) throw new InvalidDataException("文件不存在。");
            if (IsLink(cursor)) throw new InvalidDataException("拒绝符号链接。");
            if (!IsUnder(fullRoot, Path.GetFullPath(cursor))) throw new InvalidDataException("路径越出隔离目录。");
        }
        if (!File.Exists(cursor)) throw new InvalidDataException("不是普通文件。");
        return Path.GetFullPath(cursor);
    }

    public static bool IsUnder(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root);
        var candidateFull = Path.GetFullPath(candidate);
        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidateFull.Equals(rootFull, StringComparison.Ordinal) || candidateFull.StartsWith(prefix, StringComparison.Ordinal);
    }

    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public static bool IsLink(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return true;
        return File.ResolveLinkTarget(path, returnFinalTarget: false) is not null;
    }
}
