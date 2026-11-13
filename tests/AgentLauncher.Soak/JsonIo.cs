using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoakRunner;

internal static class JsonIo
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Write<T>(string path, T value)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(true);
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(full)) File.Replace(temp, full, full + ".bak");
                    else File.Move(temp, full);
                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(10);
                }
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    public static T? TryRead<T>(string path) where T : class
    {
        try
        {
            return Read<T>(path);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    public static T? Read<T>(string path) where T : class
    {
        var full = Path.GetFullPath(path);
        try
        {
            for(var attempt=0;;attempt++)
            {
                try
                {
                    using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length > 8L * 1024 * 1024) throw new InvalidDataException("记录超出大小边界。");
                    return JsonSerializer.Deserialize<T>(stream, Options) ?? throw new InvalidDataException("记录为空。");
                }
                catch(IOException e)when(attempt<20&&(e.HResult&0xffff) is 32 or 33){Thread.Sleep(10);}
            }
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static string Sha256File(string path)
    {
        using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Sha256Text(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static bool IsUnder(string root, string path)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = rootFull.EndsWith(":", StringComparison.Ordinal) ? rootFull + "\\" : rootFull + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string Bounded(string? text, int maximum)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var clean = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= maximum ? clean : clean[..maximum];
    }

    public static string RelativeOrFull(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? Path.GetFullPath(path) : relative;
        }
        catch (ArgumentException)
        {
            return Path.GetFullPath(path);
        }
    }
}
