using System.Text;
using System.Text.Json.Serialization;

namespace AgentLauncher.Core;

// 嵌入式终端的纯模型：只解析 design.md 第 7 节列出的 VT 子集，不做 IO。
// 读线程把每个字节交给 Feed；未完成的 UTF-8、CSI 收集和「丢弃直到终止」都是常数大小的状态，
// 不存在可以丢掉的原始字节流。GUI 只接收 Snapshot() 生成的完整快照。

public enum ScreenId { Primary, Alternate }

public readonly record struct TerminalCell(int Codepoint, int Width, byte Foreground, byte Background, bool Bold)
{
    public static TerminalCell Empty => new(0, 1, 7, 0, false);
    public bool IsContinuation => Width == 0;
}

public sealed record TerminalSnapshot(
    int Generation,
    ScreenId Active,
    int Columns,
    int Rows,
    int CursorColumn,
    int CursorRow,
    bool CursorVisible,
    bool ApplicationCursor,
    int TruncatedLines,
    TerminalCell[] Primary,
    TerminalCell[] Alternate,
    bool GenerationSaturated = false,
    bool BracketedPaste = false,
    int HistoryLines = 0,
    int? ExitCode = null)
{
    // 不满足这个条件的快照不得应用：卡片按 Columns*Rows 索引格子。
    [JsonIgnore]
    public bool IsWellFormed =>
        Columns is > 0 and <= TerminalScreen.MaxColumns && Rows is > 0 and <= TerminalScreen.MaxRows && Primary is not null && Alternate is not null &&
        Primary.Length == Columns * Rows && Alternate.Length == Columns * Rows &&
        HistoryLines is >= 0 and <= TerminalScreen.MaxScrollbackLines && TruncatedLines >= 0 &&
        CursorColumn >= 0 && CursorColumn <= Columns && CursorRow >= 0 && CursorRow < Rows;

    [JsonIgnore]
    public TerminalCell[] Visible => Active == ScreenId.Alternate ? Alternate : Primary;
}

public sealed class TerminalScreen
{
    public const int MinColumns = 20;
    public const int MinRows = 4;
    public const int MaxColumns = 200;
    public const int MaxRows = 60;
    public const int MaxScrollbackLines = 2000;
    public const long MaxScrollbackBytes = 2L * 1024 * 1024;
    public const int MaxSequenceBytes = 4096;
    public const int MaxUtf8Pending = 4;
    // 一个已存格子的托管大小（int + int + byte + byte + bool，含补齐）。只用于历史字节预算。
    private const int CellBytes = 12;

    private enum ParseState { Ground, Escape, Csi, Osc, DiscardCsi, DiscardOsc }

    private sealed class Screen
    {
        public TerminalCell[] Cells;
        public int CursorColumn;
        public int CursorRow;
        public byte Foreground = 7;
        public byte Background;
        public bool Bold;
        public Screen(int cells)
        {
            Cells = new TerminalCell[cells];
            Array.Fill(Cells, TerminalCell.Empty);
        }
    }

    private readonly Screen _primary;
    private readonly Screen _alternate;
    private readonly Queue<TerminalCell[]> _history = new();
    private readonly byte[] _utf8 = new byte[MaxUtf8Pending];
    private readonly byte[] _sequence = new byte[MaxSequenceBytes];
    private int _columns;
    private int _rows;
    private long _historyBytes;
    private int _truncatedLines;
    private ScreenId _active = ScreenId.Primary;
    private bool _autoWrap = true;
    private bool _applicationCursor;
    private bool _bracketedPaste;
    private bool _cursorVisible = true;
    private int _generation;
    private bool _generationSaturated;
    private ParseState _state = ParseState.Ground;
    private int _sequenceLength;
    private int _oscBytes;
    private bool _oscEscape;
    private int _utf8Length;
    private int _utf8Need;

    public TerminalScreen(int columns = 80, int rows = 24)
    {
        if (columns is < 1 or > MaxColumns) throw new ArgumentOutOfRangeException(nameof(columns), "列数超出模型上限。");
        if (rows is < 1 or > MaxRows) throw new ArgumentOutOfRangeException(nameof(rows), "行数超出模型上限。");
        _columns = columns;
        _rows = rows;
        _primary = new Screen(columns * rows);
        _alternate = new Screen(columns * rows);
    }

    public int Columns => _columns;
    public int Rows => _rows;
    public ScreenId Active => _active;
    public bool ApplicationCursor => _applicationCursor;
    public int TruncatedLines => _truncatedLines;
    public int Generation => _generation;
    public int ScrollbackLines => _history.Count;
    public long ScrollbackBytes => _historyBytes;
    // 当前行的格子数（等于光标列）。列宽规则保证它永远不超过 Columns。
    public int CurrentLineCells => Current.CursorColumn;
    private Screen Current => _active == ScreenId.Alternate ? _alternate : _primary;

    public TerminalCell CellAt(int column, int row)
    {
        if ((uint)column >= (uint)_columns || (uint)row >= (uint)_rows) throw new ArgumentOutOfRangeException(nameof(column), "格子坐标超出屏幕。");
        return Current.Cells[row * _columns + column];
    }

    public static int DisplayWidth(int codepoint)
    {
        if (codepoint < 0x20 || codepoint is >= 0x7F and < 0xA0) return 0;
        return codepoint is
            (>= 0x1100 and <= 0x115F) or (>= 0x2E80 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3) or
            (>= 0xF900 and <= 0xFAFF) or (>= 0xFE10 and <= 0xFE19) or (>= 0xFE30 and <= 0xFE6F) or
            (>= 0xFF00 and <= 0xFF60) or (>= 0xFFE0 and <= 0xFFE6) or
            (>= 0x20000 and <= 0x2FFFD) or (>= 0x30000 and <= 0x3FFFD) ? 2 : 1;
    }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) FeedByte(value);
    }

    public void Feed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Feed(Encoding.UTF8.GetBytes(text));
    }

    public TerminalSnapshot Snapshot()
    {
        AdvanceGeneration();
        return new TerminalSnapshot(_generation, _active, _columns, _rows, Current.CursorColumn, Current.CursorRow,
            _cursorVisible, _applicationCursor, _truncatedLines, (TerminalCell[])_primary.Cells.Clone(), (TerminalCell[])_alternate.Cells.Clone(), _generationSaturated,
            _bracketedPaste, _active == ScreenId.Primary ? _history.Count : 0);
    }

    // 测试和诊断用：把当前屏幕渲染成文本，跳过宽字符占位格并去掉每行尾部空白。
    public string VisibleText()
    {
        var screen = Current;
        var text = new StringBuilder();
        for (var row = 0; row < _rows; row++)
        {
            if (row > 0) text.Append('\n');
            var line = new StringBuilder();
            for (var column = 0; column < _columns; column++)
            {
                var cell = screen.Cells[row * _columns + column];
                if (cell.IsContinuation) continue;
                line.Append(cell.Codepoint == 0 ? ' ' : char.ConvertFromUtf32(cell.Codepoint));
            }
            text.Append(line.ToString().TrimEnd());
        }
        return text.ToString();
    }

    public void Resize(int columns, int rows)
    {
        if (columns is < 1 or > MaxColumns) throw new ArgumentOutOfRangeException(nameof(columns), "列数超出模型上限。");
        if (rows is < 1 or > MaxRows) throw new ArgumentOutOfRangeException(nameof(rows), "行数超出模型上限。");
        if (columns == _columns && rows == _rows) return;
        ResizeScreen(_primary, columns, rows);
        ResizeScreen(_alternate, columns, rows);
        _columns = columns;
        _rows = rows;
    }

    private void ResizeScreen(Screen screen, int columns, int rows)
    {
        var resized = new TerminalCell[columns * rows];
        Array.Fill(resized, TerminalCell.Empty);
        for (var row = 0; row < Math.Min(rows, _rows); row++)
            Array.Copy(screen.Cells, row * _columns, resized, row * columns, Math.Min(columns, _columns));
        // 仅清除被截断的半个宽字符；保留有效的主体和续接格。
        NormalizeWideCells(resized, columns);
        screen.Cells = resized;
        screen.CursorColumn = Math.Min(screen.CursorColumn, columns);
        screen.CursorRow = Math.Min(screen.CursorRow, rows - 1);
    }

    private static void NormalizeWideCells(TerminalCell[] cells, int columns)
    {
        for (var index = 0; index < cells.Length; index++)
        {
            var column = index % columns;
            if (cells[index].IsContinuation && (column == 0 || cells[index - 1].Width != 2)) cells[index] = TerminalCell.Empty;
            else if (cells[index].Width == 2 && (column == columns - 1 || !cells[index + 1].IsContinuation)) cells[index] = TerminalCell.Empty;
        }
    }

    // 按需复制一屏。历史只在模型线程读取，不随每一帧克隆/发送。
    public TerminalReadback Readback(TerminalReadRequest request)
    {
        if (!request.IsWellFormed) throw new ArgumentException("历史读取请求超出边界。", nameof(request));
        var history = _active == ScreenId.Primary ? _history.ToArray() : [];
        var first = _active == ScreenId.Primary ? _truncatedLines : 0;
        var last = first + history.Length;
        var top = Math.Clamp(request.TopLine, first, last);
        var matchLine = -1;var matchColumn = -1;var matchLength = 0;
        ReadOnlySpan<TerminalCell> Line(int index) => index < history.Length ? history[index] : Current.Cells.AsSpan((index - history.Length) * _columns, _columns);
        if (!string.IsNullOrEmpty(request.Search))
        {
            var count = history.Length + _rows;
            var start = Math.Clamp(request.TopLine - first, 0, count - 1);
            for (var step = 0; step <= count; step++)
            {
                var index = (start + (request.Backward ? -step : step) + count) % count;
                var cells = Line(index);
                cells = cells[..Math.Min(cells.Length, _columns)];
                if (cells.Length > 0 && cells[^1].Width == 2) cells = cells[..^1];
                var text = TerminalText.ExtractLine(cells);
                var found = -1;
                // 首行从上次匹配的下一列继续；回绕后仍检查首行剩余部分。
                for (var offset = 0; offset <= text.Length - request.Search.Length; offset++)
                {
                    if (!text.AsSpan(offset, request.Search.Length).Equals(request.Search.AsSpan(), StringComparison.OrdinalIgnoreCase)) continue;
                    var column = TerminalText.ColumnAt(cells, offset);
                    if (step == 0 && request.SearchColumn >= 0 && (request.Backward ? column >= request.SearchColumn : column <= request.SearchColumn)) continue;
                    found = offset;if (!request.Backward) break;
                }
                if (found < 0) continue;
                matchLine = first + index;
                matchColumn = TerminalText.ColumnAt(cells, found);
                matchLength = Math.Max(1, TerminalText.ColumnAt(cells, found + request.Search.Length) - matchColumn);
                top = Math.Clamp(matchLine, first, last);break;
            }
        }
        var page = new TerminalCell[_columns * _rows];Array.Fill(page, TerminalCell.Empty);
        for (var row = 0; row < _rows; row++)
        {
            var line = Line(top - first + row);
            line[..Math.Min(line.Length, _columns)].CopyTo(page.AsSpan(row * _columns, _columns));
        }
        NormalizeWideCells(page, _columns);
        return new TerminalReadback(request.RequestId, _active, top, first, history.Length, _columns, _rows, page, matchLine, matchColumn, matchLength, request.Search);
    }

    private void AdvanceGeneration()
    {
        if (_generation < int.MaxValue - 1) _generation++;
        else _generationSaturated = true;
    }

    private void FeedByte(byte value)
    {
        if (_state == ParseState.Ground)
        {
            if (_utf8Need != 0 && TryContinueUtf8(value)) return;
            if (value < 0x80) { HandleAscii(value); return; }
            BeginUtf8(value);
            return;
        }
        HandleSequenceByte(value);
    }

    private void HandleAscii(byte value)
    {
        switch (value)
        {
            case 0x1B: _state = ParseState.Escape; return;
            case 0x0D: Current.CursorColumn = 0; return;
            case 0x0A: LineFeed(); return;
            case 0x08: if (Current.CursorColumn > 0) Current.CursorColumn--; return;
            case 0x09: Current.CursorColumn = Math.Min(_columns - 1, (Current.CursorColumn / 8 + 1) * 8); return;
        }
        if (value < 0x20 || value == 0x7F) return;
        PutCodepoint(value);
    }

    private void HandleSequenceByte(byte value)
    {
        switch (_state)
        {
            case ParseState.Escape:
                if (value is >= 0x20 and <= 0x2F) return;  // 中间字节：ESC ( B、ESC # 8 之类的字符集/表格选择
                if (value == (byte)'[') { _sequenceLength = 0; _state = ParseState.Csi; return; }
                if (value == (byte)']') { _oscBytes = 0; _oscEscape = false; _state = ParseState.Osc; return; }
                _state = ParseState.Ground;
                return;
            case ParseState.Csi:
                if (value is >= 0x20 and <= 0x3F)
                {
                    // 收集缓冲最多 4096 字节；还没看到终止字节就超限，整个序列丢掉。
                    if (_sequenceLength == MaxSequenceBytes) { _state = ParseState.DiscardCsi; return; }
                    _sequence[_sequenceLength++] = value;
                    return;
                }
                if (value is >= 0x40 and <= 0x7E) { DispatchCsi(value); _state = ParseState.Ground; return; }
                return;  // CSI 中的控制字节被忽略，不改变解析状态
            case ParseState.Osc:
                // 全部 OSC 收集后丢弃：不改剪贴板、不开 URL、不启动程序。
                if (_oscBytes == MaxSequenceBytes) { _state = ParseState.DiscardOsc; return; }
                _oscBytes++;
                if (value == 0x07) { _state = ParseState.Ground; _oscEscape = false; return; }
                if (_oscEscape && value == (byte)'\\') { _state = ParseState.Ground; _oscEscape = false; return; }
                _oscEscape = value == 0x1B;
                return;
            case ParseState.DiscardCsi:
                if (value is >= 0x40 and <= 0x7E) _state = ParseState.Ground;
                return;
            case ParseState.DiscardOsc:
                if (value == 0x07) { _state = ParseState.Ground; _oscEscape = false; return; }
                if (_oscEscape && value == (byte)'\\') { _state = ParseState.Ground; _oscEscape = false; return; }
                _oscEscape = value == 0x1B;
                return;
        }
    }

    private void DispatchCsi(byte final)
    {
        var index = 0;
        var isPrivate = false;
        if (index < _sequenceLength && _sequence[index] is (byte)'?' or (byte)'<' or (byte)'=' or (byte)'>')
        {
            isPrivate = _sequence[index] == '?';
            index++;
        }
        var parameters = new int[8];
        var count = 0;
        var current = 0;
        var hasDigits = false;
        for (; index < _sequenceLength; index++)
        {
            var value = _sequence[index];
            if (value is >= (byte)'0' and <= (byte)'9')
            {
                hasDigits = true;
                if (current < 100000) current = current * 10 + (value - '0');
                continue;
            }
            if (value == (byte)';')
            {
                if (count < parameters.Length) parameters[count++] = hasDigits ? current : 0;
                current = 0;
                hasDigits = false;
                continue;
            }
            break;  // 中间字节之后不再读取参数
        }
        if (hasDigits && count < parameters.Length) parameters[count++] = current;

        if (isPrivate)
        {
            // 未列出的 DEC 私有模式不执行。
            if (final is (byte)'h' or (byte)'l') SetPrivateModes(parameters, count, final == (byte)'h');
            return;
        }
        switch ((char)final)
        {
            case 'A': MoveCursor(0, -Param(parameters, count, 0, 1)); return;
            case 'B': MoveCursor(0, Param(parameters, count, 0, 1)); return;
            case 'C': MoveCursor(Param(parameters, count, 0, 1), 0); return;
            case 'D': MoveCursor(-Param(parameters, count, 0, 1), 0); return;
            case 'H':
            case 'f': SetCursor(Param(parameters, count, 0, 1), Param(parameters, count, 1, 1)); return;
            case 'G': SetCursor(Current.CursorRow + 1, Param(parameters, count, 0, 1)); return;
            case 'J': EraseInDisplay(Param(parameters, count, 0, 0)); return;
            case 'K': EraseInLine(Param(parameters, count, 0, 0)); return;
            case 'm': SetGraphics(parameters, count); return;
        }
    }

    private static int Param(int[] parameters, int count, int index, int fallback)
    {
        var value = index < count ? parameters[index] : 0;
        return value == 0 ? fallback : value;
    }

    private void SetPrivateModes(int[] parameters, int count, bool set)
    {
        for (var i = 0; i < count; i++)
        {
            switch (parameters[i])
            {
                case 1: _applicationCursor = set; break;
                case 2004: _bracketedPaste = set; break;
                case 1049:
                    if (set) EnterAlternate();
                    else LeaveAlternate();
                    break;
            }
        }
    }

    private void EnterAlternate()
    {
        if (_active == ScreenId.Alternate) return;
        Array.Fill(_alternate.Cells, TerminalCell.Empty);
        _alternate.CursorColumn = 0;
        _alternate.CursorRow = 0;
        // 主屏的光标、SGR 和格子都留在 _primary 里；离开备用屏即回到它们。
        _active = ScreenId.Alternate;
    }

    private void LeaveAlternate()
    {
        if (_active == ScreenId.Primary) return;
        Array.Fill(_alternate.Cells, TerminalCell.Empty);
        _alternate.CursorColumn = 0;
        _alternate.CursorRow = 0;
        _active = ScreenId.Primary;
    }

    private void SetGraphics(int[] parameters, int count)
    {
        var screen = Current;
        if (count == 0) { ResetGraphics(screen); return; }
        for (var i = 0; i < count; i++)
        {
            var value = parameters[i];
            switch (value)
            {
                case 0: ResetGraphics(screen); break;
                case 1: screen.Bold = true; break;
                case >= 30 and <= 37: screen.Foreground = (byte)(value - 30); break;
                case >= 90 and <= 97: screen.Foreground = (byte)(value - 90 + 8); break;
                case >= 40 and <= 47: screen.Background = (byte)(value - 40); break;
                case >= 100 and <= 107: screen.Background = (byte)(value - 100 + 8); break;
            }
        }
    }

    private static void ResetGraphics(Screen screen)
    {
        screen.Foreground = 7;
        screen.Background = 0;
        screen.Bold = false;
    }

    private void MoveCursor(int deltaColumns, int deltaRows)
    {
        var screen = Current;
        screen.CursorColumn = Math.Clamp(screen.CursorColumn + deltaColumns, 0, _columns - 1);
        screen.CursorRow = Math.Clamp(screen.CursorRow + deltaRows, 0, _rows - 1);
    }

    private void SetCursor(int row, int column)
    {
        var screen = Current;
        screen.CursorRow = Math.Clamp(row - 1, 0, _rows - 1);
        screen.CursorColumn = Math.Clamp(column - 1, 0, _columns - 1);
    }

    private void EraseInLine(int mode)
    {
        var screen = Current;
        var row = screen.CursorRow;
        var (start, end) = mode switch
        {
            0 => (screen.CursorColumn, _columns - 1),
            1 => (0, screen.CursorColumn),
            2 => (0, _columns - 1),
            _ => (-1, -1)
        };
        if (start < 0) return;
        for (var column = start; column <= end; column++) BlankAt(screen, row * _columns + column);
    }

    private void EraseInDisplay(int mode)
    {
        var screen = Current;
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (var row = screen.CursorRow + 1; row < _rows; row++) BlankRow(screen, row);
                return;
            case 1:
                EraseInLine(1);
                for (var row = 0; row < screen.CursorRow; row++) BlankRow(screen, row);
                return;
            case 2:
                Array.Fill(screen.Cells, TerminalCell.Empty);
                return;
        }
    }

    private void BlankRow(Screen screen, int row)
    {
        for (var column = 0; column < _columns; column++) BlankAt(screen, row * _columns + column);
    }

    private void BlankAt(Screen screen, int index)
    {
        if (index < 0 || index >= screen.Cells.Length) return;
        var existing = screen.Cells[index];
        var rowStart = index / _columns * _columns;
        // 覆盖宽字符的半格时，把另一半也落成空白，避免留下没有主体的占位格。
        if (existing.IsContinuation && index > rowStart && screen.Cells[index - 1].Width == 2) screen.Cells[index - 1] = TerminalCell.Empty;
        if (existing.Width == 2 && index + 1 < rowStart + _columns) screen.Cells[index + 1] = TerminalCell.Empty;
        screen.Cells[index] = TerminalCell.Empty;
    }

    private void LineFeed()
    {
        var screen = Current;
        if (screen.CursorRow + 1 < _rows) { screen.CursorRow++; return; }
        if (ReferenceEquals(screen, _primary)) CommitHistory(screen.Cells.AsSpan(0, _columns));
        Array.Copy(screen.Cells, _columns, screen.Cells, 0, (_rows - 1) * _columns);
        Array.Fill(screen.Cells, TerminalCell.Empty, (_rows - 1) * _columns, _columns);
    }

    private void CommitHistory(ReadOnlySpan<TerminalCell> row)
    {
        var length = row.Length;
        while (length > 0 && row[length - 1].Codepoint == 0 && row[length - 1].Width == 1) length--;
        var line = length > 0 ? row[..length].ToArray() : [];
        _history.Enqueue(line);
        _historyBytes += (long)line.Length * CellBytes;
        while (_history.Count > MaxScrollbackLines || _historyBytes > MaxScrollbackBytes)
        {
            var dropped = _history.Dequeue();
            _historyBytes -= (long)dropped.Length * CellBytes;
            _truncatedLines++;
        }
    }

    private void PutCodepoint(int codepoint)
    {
        var width = DisplayWidth(codepoint);
        if (width == 0) return;
        var screen = Current;
        if (_autoWrap && screen.CursorColumn + width > _columns)
        {
            screen.CursorColumn = 0;
            LineFeed();
        }
        // 自动换行关闭时，超出最后一格的字符在格子边界截掉；解析继续消费后面的字节。
        if (screen.CursorColumn + width > _columns) return;
        var index = screen.CursorRow * _columns + screen.CursorColumn;
        BlankAt(screen, index);
        if (width == 2) BlankAt(screen, index + 1);
        screen.Cells[index] = new TerminalCell(codepoint, width, screen.Foreground, screen.Background, screen.Bold);
        if (width == 2) screen.Cells[index + 1] = new TerminalCell(0, 0, screen.Foreground, screen.Background, screen.Bold);
        screen.CursorColumn += width;
    }

    private void BeginUtf8(byte lead)
    {
        _utf8[0] = lead;
        _utf8Length = 1;
        _utf8Need = lead switch
        {
            >= 0xC2 and <= 0xDF => 1,
            >= 0xE0 and <= 0xEF => 2,
            >= 0xF0 and <= 0xF4 => 3,
            _ => 0
        };
        if (_utf8Need != 0) return;
        // 0x80-0xC1、0xF5-0xFF 不是合法起始字节：这个非法序列记为一个 U+FFFD。
        _utf8Length = 0;
        PutCodepoint(0xFFFD);
    }

    private bool TryContinueUtf8(byte value)
    {
        if ((value & 0xC0) != 0x80)
        {
            _utf8Length = 0;
            _utf8Need = 0;
            PutCodepoint(0xFFFD);
            return false;
        }
        _utf8[_utf8Length++] = value;
        if (_utf8Length < _utf8Need + 1) return true;
        var codepoint = DecodeUtf8();
        _utf8Length = 0;
        _utf8Need = 0;
        PutCodepoint(codepoint);
        return true;
    }

    private int DecodeUtf8()
    {
        int codepoint;
        switch (_utf8Length)
        {
            case 2: codepoint = ((_utf8[0] & 0x1F) << 6) | (_utf8[1] & 0x3F); break;
            case 3: codepoint = ((_utf8[0] & 0x0F) << 12) | ((_utf8[1] & 0x3F) << 6) | (_utf8[2] & 0x3F); break;
            case 4: codepoint = ((_utf8[0] & 0x07) << 18) | ((_utf8[1] & 0x3F) << 12) | ((_utf8[2] & 0x3F) << 6) | (_utf8[3] & 0x3F); break;
            default: return 0xFFFD;
        }
        var minimal = _utf8Length switch { 2 => 0x80, 3 => 0x800, _ => 0x10000 };
        if (codepoint < minimal || codepoint > 0x10FFFF || codepoint is >= 0xD800 and <= 0xDFFF) return 0xFFFD;
        return codepoint;
    }
}
