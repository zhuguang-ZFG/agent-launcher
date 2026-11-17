using System.Text.Json;
using System.Text.Encodings.Web;

namespace AgentLauncher.Core;

public sealed record CleanupFile(string Category,string Path,string? OriginalPath=null)
{
    public string Display=>Category+"  "+JsonSerializer.Serialize(Path,new JsonSerializerOptions{Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping})+
        (OriginalPath is null?"":" ← "+JsonSerializer.Serialize(OriginalPath));
}

public static class CleanupInventory
{
    public static IReadOnlyList<CleanupFile> Parse(string porcelain)
    {
        var parts=porcelain.Split('\0');var rows=new List<CleanupFile>();
        for(var i=0;i<parts.Length;i++)
        {
            var entry=parts[i];if(entry.Length==0)continue;
            if(entry.Length<4||entry[2]!=' ')throw new InvalidDataException("Git 文件清单结构不完整，请重新检查。");
            string? original=null;
            if(entry[0] is 'R' or 'C'||entry[1] is 'R' or 'C')
            {if(++i>=parts.Length||parts[i].Length==0)throw new InvalidDataException("Git 重命名清单不完整。");original=parts[i];}
            rows.Add(new(entry.StartsWith("!!",StringComparison.Ordinal)?"忽略内容":entry.StartsWith("??",StringComparison.Ordinal)?"未跟踪":"已跟踪修改",entry[3..],original));
        }
        return rows;
    }
}
