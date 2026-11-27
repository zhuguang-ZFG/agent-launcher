using System.Text;
using System.Text.Json.Serialization;

namespace AgentLauncher.Core;

public sealed record TerminalReadRequest(int RequestId, int TopLine, string? Search = null, bool Backward = false, int SearchColumn = -1)
{
    [JsonIgnore]
    public bool IsWellFormed => RequestId > 0 && TopLine >= 0 && SearchColumn is >= -1 and <= TerminalScreen.MaxColumns && (Search is null || Search.Length <= 256);
}

public sealed record TerminalReadback(int RequestId, ScreenId Active, int TopLine, int FirstLine, int HistoryLines,
    int Columns, int Rows, TerminalCell[] Cells, int MatchLine = -1, int MatchColumn = -1, int MatchLength = 0, string? Search = null)
{
    [JsonIgnore]
    public bool IsWellFormed => RequestId > 0 && Active is ScreenId.Primary or ScreenId.Alternate && Columns is > 0 and <= TerminalScreen.MaxColumns && Rows is > 0 and <= TerminalScreen.MaxRows &&
        HistoryLines is >= 0 and <= TerminalScreen.MaxScrollbackLines && FirstLine >= 0 && TopLine >= FirstLine && TopLine <= (long)FirstLine + HistoryLines &&
        Cells is not null && Cells.Length == Columns * Rows && Cells.All(cell => cell.Codepoint is >= 0 and <= 0x10ffff and not (>= 0xd800 and <= 0xdfff) &&
            cell.Width is >= 0 and <= 2 && cell.Foreground < 16 && cell.Background < 16 && (!cell.IsContinuation || cell.Codepoint == 0)) &&
        (Search is null || Search.Length <= 256) &&
        (MatchLine == -1 ? MatchColumn == -1 && MatchLength == 0 : !string.IsNullOrEmpty(Search) && MatchLine >= TopLine && MatchLine < (long)TopLine + Rows &&
            MatchColumn >= 0 && MatchColumn < Columns && MatchLength > 0 && MatchLength <= Columns - MatchColumn);
}

public static class TerminalText
{
    public static string ExtractLine(ReadOnlySpan<TerminalCell> cells)
    {
        var text = new StringBuilder();
        foreach (var cell in cells) if (!cell.IsContinuation) text.Append(cell.Codepoint == 0 ? " " : char.ConvertFromUtf32(cell.Codepoint));
        return text.ToString().TrimEnd();
    }

    public static int ColumnAt(ReadOnlySpan<TerminalCell> cells, int textOffset)
    {
        var offset = 0;
        for (var column = 0; column < cells.Length; column++)
        {
            var cell = cells[column];if (cell.IsContinuation) continue;
            if (offset >= textOffset) return column;
            offset += cell.Codepoint > 0xffff ? 2 : 1;
        }
        return cells.Length;
    }

    // UTF-16 长度与终端列数不同；选中任意半个中文格都复制完整字符。
    public static string Selection(TerminalCell[] cells, int columns, int anchor, int end)
    {
        var start = Math.Clamp(Math.Min(anchor, end), 0, cells.Length);
        var limit = Math.Clamp(Math.Max(anchor, end), 0, cells.Length);
        if (start == limit) return "";
        var text = new StringBuilder();
        for (var row = start / columns; row <= (limit - 1) / columns; row++)
        {
            var from = Math.Max(start, row * columns);var to = Math.Min(limit, (row + 1) * columns);
            if (from % columns > 0 && cells[from].IsContinuation) from--;
            if (row > start / columns) text.AppendLine();
            text.Append(ExtractLine(cells.AsSpan(from, to - from)));
        }
        return text.ToString();
    }
}
