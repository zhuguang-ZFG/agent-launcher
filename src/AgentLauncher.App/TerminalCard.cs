using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AgentLauncher.Core;

namespace AgentLauncher.App;

// 嵌入式终端卡片：只应用版本化的完整快照，不持有 ConPTY、不解析命令、不保存可续接的原始字节流。
// 开关为 false 时这个控件不会出现在窗口里（ET18）。
public sealed class TerminalCard : FrameworkElement
{
    public const double CellWidth = 8;
    public const double CellHeight = 18;
    public const double FontSize = 13;

    // SGR 30-37 / 90-97 的 16 色；索引 1 是红色。画格子只用这张表，不画纯文本。
    private static readonly Brush[] Palette =
    [
        Frozen(0x1E, 0x1E, 0x1E), Frozen(0xCD, 0x31, 0x31), Frozen(0x0D, 0xBC, 0x79), Frozen(0xE5, 0xE5, 0x10),
        Frozen(0x24, 0x72, 0xC8), Frozen(0xBC, 0x3F, 0xBC), Frozen(0x11, 0xA8, 0xCD), Frozen(0xE5, 0xE5, 0xE5),
        Frozen(0x66, 0x66, 0x66), Frozen(0xF1, 0x4C, 0x4C), Frozen(0x23, 0xD1, 0x8B), Frozen(0xF5, 0xF5, 0x43),
        Frozen(0x3B, 0x8E, 0xEA), Frozen(0xD6, 0x70, 0xD6), Frozen(0x29, 0xB8, 0xDB), Frozen(0xFF, 0xFF, 0xFF)
    ];

    private static readonly Typeface Regular = new(new FontFamily("Cascadia Mono, Consolas, Courier New"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldFace = new(new FontFamily("Cascadia Mono, Consolas, Courier New"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Brush CursorBrush = Frozen(0xE5, 0xE5, 0xE5);
    private static readonly Brush DefaultBackground = Frozen(0x12, 0x12, 0x12);

    public TerminalView View { get; } = new();
    public TerminalSnapshot? Applied => View.Current;
    public string? RejectedPaste { get; private set; }
    private TerminalReadback? page;
    private int requestId;
    private int? requestedTop;
    private TerminalReadRequest? pendingRequest;
    private TerminalCell[]? selectedCells;
    private int selectedColumns;
    private int selectedRows;
    private int selectionAnchor;
    private int selectionEnd;
    private bool selecting;
    public int FirstLine => Applied?.Active == ScreenId.Primary ? Applied.TruncatedLines : 0;
    public int HistoryLines => Applied?.HistoryLines ?? 0;
    public int TopLine => requestedTop ?? page?.TopLine ?? FirstLine + HistoryLines;
    public bool IsReadingHistory => requestedTop is not null || page is not null;
    public bool HasSelection => selectedCells is not null && selectionAnchor != selectionEnd;
    public string SelectionText => selectedCells is null ? "" : TerminalText.Selection(selectedCells, selectedColumns, selectionAnchor, selectionEnd);
    public string ViewportText => TerminalText.Selection(DisplayCells, DisplayColumns, 0, DisplayCells.Length);
    public string? SearchStatus { get; private set; }
    private TerminalCell[] DisplayCells => selectedCells ?? page?.Cells ?? Applied?.Visible ?? [];
    private int DisplayColumns => selectedCells is not null ? selectedColumns : page?.Columns ?? Applied?.Columns ?? 80;
    private int DisplayRows => selectedCells is not null ? selectedRows : page?.Rows ?? Applied?.Rows ?? 4;

    // 卡片把输入字节交给宿主转发给 ConPTY；卡片自己不创建进程、不碰 Job。
    public event Action<byte[]>? Input;
    public event Action<TerminalReadRequest>? ReadRequested;
    public event Action? ViewportChanged;
    public event Action? CopyRequested;
    public event Action? PasteRequested;
    public event Action? SearchRequested;
    public event Action<int>? NavigateTab;

    public TerminalCard()
    {
        Focusable = true;
        ClipToBounds = true;
        Background = DefaultBackground;
    }

    public Brush Background { get; set; }

    public static Brush BrushFor(byte foreground) => Palette[foreground & 0xF];

    public static Brush ColorOf(int foreground) => Palette[foreground & 0xF];

    // 只接受更新的世代；旧快照不会改动已经画出来的格子。
    public bool Show(TerminalSnapshot snapshot)
    {
        var previous = Applied;
        if (!View.Apply(snapshot)) return false;
        if (previous is not null && previous.Active != snapshot.Active) ReturnToLive();
        else if (previous is not null && (previous.Columns != snapshot.Columns || previous.Rows != snapshot.Rows))
        {
            var restore=IsReadingHistory;var top=TopLine;var search=pendingRequest?.Search??page?.Search;
            ReturnToLive();if(restore)RequestRead(top,search);
        }
        else if (page is not null && page.TopLine < FirstLine) RequestRead(FirstLine);
        ViewportChanged?.Invoke();
        InvalidateMeasure();
        InvalidateVisual();
        return true;
    }

    public bool ShowReadback(TerminalReadback readback)
    {
        if (Applied is not {} snapshot || !readback.IsWellFormed || readback.RequestId != requestId || readback.Active != snapshot.Active ||
            readback.Columns != snapshot.Columns || readback.Rows != snapshot.Rows) return false;
        page = readback;requestedTop = null;pendingRequest = null;ClearSelection();
        SearchStatus = readback.Search is null ? null : readback.MatchLine < 0 ? "未找到匹配输出" : $"找到：第 {readback.MatchLine + 1} 行（Enter 下一处，Shift+Enter 上一处）";
        InvalidateVisual();ViewportChanged?.Invoke();return true;
    }

    public void ReturnToLive()
    {
        requestId++;requestedTop = null;pendingRequest = null;page = null;SearchStatus = null;ClearSelection();InvalidateVisual();ViewportChanged?.Invoke();
    }

    public void ScrollLines(int lines) => ScrollTo(TopLine + lines);
    public void ScrollTo(int top)
    {
        top = Math.Clamp(top, FirstLine, FirstLine + HistoryLines);
        if (top == FirstLine + HistoryLines) ReturnToLive();
        else RequestRead(top);
    }

    public void Find(string query, bool backward = false)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        var repeat = page?.Search == query && page.MatchLine >= 0;
        RequestRead(repeat ? page!.MatchLine : TopLine, query, backward, repeat ? page!.MatchColumn : -1);
    }

    private void RequestRead(int top, string? search = null, bool backward = false, int searchColumn = -1)
    {
        if (Applied is null) return;
        ClearSelection();requestedTop = Math.Clamp(top, FirstLine, FirstLine + HistoryLines);
        pendingRequest = new TerminalReadRequest(++requestId, Math.Max(0, top), search, backward, searchColumn);
        ReadRequested?.Invoke(pendingRequest);ViewportChanged?.Invoke();
    }

    public void Select(int anchor, int end)
    {
        if (selectedCells is null) { selectedColumns = DisplayColumns;selectedRows = DisplayRows;selectedCells = DisplayCells; }
        selectionAnchor = Math.Clamp(anchor, 0, selectedCells.Length);selectionEnd = Math.Clamp(end, 0, selectedCells.Length);InvalidateVisual();
    }

    public void ClearSelection() { selectedCells = null;selectionAnchor = selectionEnd = 0;InvalidateVisual(); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var snapshot = Applied;
        if (snapshot is null) return new Size(CellWidth * 80, CellHeight * 4);
        return new Size(CellWidth * snapshot.Columns, CellHeight * snapshot.Rows);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var snapshot = Applied;
        var width = ActualWidth > 0 ? ActualWidth : CellWidth * 80;
        var height = ActualHeight > 0 ? ActualHeight : CellHeight * 4;
        drawing.DrawRectangle(Background, null, new Rect(0, 0, width, height));
        if (snapshot is null) return;
        var cells = DisplayCells;var columns = DisplayColumns;var rows = DisplayRows;
        for (var row = 0; row < rows; row++)
        {
            var column = 0;
            while (column < columns)
            {
                var index = row * columns + column;
                if (index >= cells.Length) break;
                var cell = cells[index];
                if (cell.IsContinuation) { column++; continue; }
                // 把连续同属性的格子合成一次绘制：颜色和加粗来自格子本身，不是纯文本。
                var run = 0;
                var text = new System.Text.StringBuilder();
                while (column + run < columns)
                {
                    var next = cells[index + run];
                    if (next.IsContinuation) { run++; continue; }
                    if (next.Foreground != cell.Foreground || next.Background != cell.Background || next.Bold != cell.Bold) break;
                    text.Append(next.Codepoint == 0 ? ' ' : char.ConvertFromUtf32(next.Codepoint));
                    run++;
                }
                var origin = new Rect(column * CellWidth, row * CellHeight, Math.Max(1, run) * CellWidth, CellHeight);
                if (cell.Background != 0) drawing.DrawRectangle(Palette[cell.Background & 0xF], null, origin);
                if (text.Length > 0)
                {
                    var formatted = new FormattedText(text.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        cell.Bold ? BoldFace : Regular, FontSize, BrushFor(cell.Foreground), VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    drawing.DrawText(formatted, new Point(column * CellWidth, row * CellHeight));
                }
                column += Math.Max(1, run);
            }
        }
        if (page is { MatchLine: >= 0, MatchColumn: >= 0 } match)
        {
            var row = match.MatchLine - match.TopLine;
            if (row >= 0 && row < rows) drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(80, 255, 210, 40)), null,
                new Rect(match.MatchColumn * CellWidth, row * CellHeight, Math.Min(match.MatchLength, columns - Math.Min(columns, match.MatchColumn)) * CellWidth, CellHeight));
        }
        if (HasSelection)
        {
            var start = Math.Min(selectionAnchor, selectionEnd);var end = Math.Max(selectionAnchor, selectionEnd);
            for (var row = start / columns; row <= (end - 1) / columns; row++)
            {
                var left = Math.Max(start, row * columns) - row * columns;var right = Math.Min(end, (row + 1) * columns) - row * columns;
                drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(110, 70, 130, 220)), null, new Rect(left * CellWidth, row * CellHeight, (right - left) * CellWidth, CellHeight));
            }
        }
        if (snapshot.CursorVisible && !IsReadingHistory && selectedCells is null)
        {
            var cursor = new Rect(snapshot.CursorColumn * CellWidth, snapshot.CursorRow * CellHeight, CellWidth, CellHeight);
            drawing.DrawRectangle(null, new Pen(CursorBrush, 1), cursor);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key == Key.Tab) { e.Handled = true;NavigateTab?.Invoke(modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);return; }
            if (e.Key == Key.F) { e.Handled = true;SearchRequested?.Invoke();return; }
            if (e.Key == Key.V) { e.Handled = true;PasteRequested?.Invoke();return; }
            if (e.Key == Key.C && (HasSelection || modifiers.HasFlag(ModifierKeys.Shift))) { e.Handled = true;CopyRequested?.Invoke();return; }
        }
        if (modifiers == ModifierKeys.Shift && e.Key is Key.PageUp or Key.PageDown)
        { e.Handled = true;ScrollLines((e.Key == Key.PageUp ? -1 : 1) * Math.Max(1, DisplayRows - 1));return; }
        if (e.Key == Key.Escape && (HasSelection || IsReadingHistory)) { e.Handled = true;ReturnToLive();return; }
        var terminalModifiers = (modifiers.HasFlag(ModifierKeys.Control) ? TerminalModifiers.Control : 0) |
            (modifiers.HasFlag(ModifierKeys.Shift) ? TerminalModifiers.Shift : 0) | (modifiers.HasFlag(ModifierKeys.Alt) ? TerminalModifiers.Alt : 0);
        var applicationCursor = Applied?.ApplicationCursor == true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        TerminalKey? special = key switch
        {
            Key.Up => TerminalKey.Up,Key.Down => TerminalKey.Down,Key.Left => TerminalKey.Left,Key.Right => TerminalKey.Right,
            Key.Home => TerminalKey.Home,Key.End => TerminalKey.End,Key.Delete => TerminalKey.Delete,Key.Back => TerminalKey.Backspace,
            Key.Enter => TerminalKey.Enter,Key.Tab => TerminalKey.Tab,Key.PageUp => TerminalKey.PageUp,Key.PageDown => TerminalKey.PageDown,
            _ => null
        };
        byte[]? bytes = special is {} terminalKey ? TerminalInput.Key(terminalKey, applicationCursor, terminalModifiers) : key == Key.Escape ? [0x1b] : null;
        // Ctrl+C 只写 0x03；它不是停止命令，也不结束 Job 或 ssh.exe。
        if(bytes is null&&Keyboard.Modifiers==ModifierKeys.Control&&e.Key is >=Key.A and <=Key.Z && e.Key!=Key.V)
            bytes=[(byte)(1+(int)e.Key-(int)Key.A)];
        if (bytes is null) return;
        e.Handled = true;
        ReturnToLive();
        Input?.Invoke(bytes);
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        // 只有已提交的文本会到这里；组合中的预编辑不上屏到管道（输入法试验仍为 NOT_RUN）。
        if (string.IsNullOrEmpty(e.Text)) return;
        var sanitized = TerminalInput.Paste(e.Text);
        if (sanitized is null || sanitized.Length == 0) { e.Handled = true; return; }
        e.Handled = true;
        ReturnToLive();
        Input?.Invoke(sanitized);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e) { e.Handled = true;ScrollLines(e.Delta > 0 ? -3 : 3); }
    private int CellBoundary(Point position) => Math.Clamp((int)(position.Y / CellHeight), 0, DisplayRows - 1) * DisplayColumns + Math.Clamp((int)Math.Round(position.X / CellWidth), 0, DisplayColumns);
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();ClearSelection();var index = CellBoundary(e.GetPosition(this));Select(index, index);selecting = true;CaptureMouse();e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (selecting && e.LeftButton == MouseButtonState.Pressed) { selectionEnd = CellBoundary(e.GetPosition(this));InvalidateVisual(); }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { selecting = false;ReleaseMouseCapture();if (!HasSelection) ClearSelection();e.Handled = true; }
    protected override void OnLostMouseCapture(MouseEventArgs e) { selecting = false;base.OnLostMouseCapture(e); }

    // 多行仅在应用协商括号粘贴或用户确认后发送。
    public bool Paste(string text, bool confirmedMultiline = false)
    {
        var sanitized = TerminalInput.Paste(text, Applied?.BracketedPaste == true, confirmedMultiline);
        if (sanitized is null) { RejectedPaste = TerminalInput.MultilinePasteRejected; return false; }
        RejectedPaste = null;
        ReturnToLive();
        if (sanitized.Length > 0) Input?.Invoke(sanitized);
        return true;
    }

    private static Brush Frozen(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
