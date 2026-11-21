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

    // 卡片把输入字节交给宿主转发给 ConPTY；卡片自己不创建进程、不碰 Job。
    public event Action<byte[]>? Input;

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
        if (!View.Apply(snapshot)) return false;
        InvalidateMeasure();
        InvalidateVisual();
        return true;
    }

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
        var cells = snapshot.Visible;
        for (var row = 0; row < snapshot.Rows; row++)
        {
            var column = 0;
            while (column < snapshot.Columns)
            {
                var index = row * snapshot.Columns + column;
                if (index >= cells.Length) break;
                var cell = cells[index];
                if (cell.IsContinuation) { column++; continue; }
                // 把连续同属性的格子合成一次绘制：颜色和加粗来自格子本身，不是纯文本。
                var run = 0;
                var text = new System.Text.StringBuilder();
                while (column + run < snapshot.Columns)
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
        if (snapshot.CursorVisible)
        {
            var cursor = new Rect(snapshot.CursorColumn * CellWidth, snapshot.CursorRow * CellHeight, CellWidth, CellHeight);
            drawing.DrawRectangle(null, new Pen(CursorBrush, 1), cursor);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var applicationCursor = Applied?.ApplicationCursor == true;
        byte[]? bytes = e.Key switch
        {
            Key.Escape => [0x1b],
            Key.Up => TerminalInput.Key(TerminalKey.Up, applicationCursor),
            Key.Down => TerminalInput.Key(TerminalKey.Down, applicationCursor),
            Key.Left => TerminalInput.Key(TerminalKey.Left, applicationCursor),
            Key.Right => TerminalInput.Key(TerminalKey.Right, applicationCursor),
            Key.Home => TerminalInput.Key(TerminalKey.Home, applicationCursor),
            Key.End => TerminalInput.Key(TerminalKey.End, applicationCursor),
            Key.Delete => TerminalInput.Key(TerminalKey.Delete, applicationCursor),
            Key.Back => TerminalInput.Key(TerminalKey.Backspace, applicationCursor),
            Key.Enter => TerminalInput.Key(TerminalKey.Enter, applicationCursor),
            Key.Tab => TerminalInput.Key(TerminalKey.Tab, applicationCursor),
            _ => null
        };
        // Ctrl+C 只写 0x03；它不是停止命令，也不结束 Job 或 ssh.exe。
        if(bytes is null&&Keyboard.Modifiers==ModifierKeys.Control&&e.Key is >=Key.A and <=Key.Z && e.Key!=Key.V)
            bytes=[(byte)(1+(int)e.Key-(int)Key.A)];
        if (bytes is null) return;
        e.Handled = true;
        Input?.Invoke(bytes);
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        // 只有已提交的文本会到这里；组合中的预编辑不上屏到管道（输入法试验仍为 NOT_RUN）。
        if (string.IsNullOrEmpty(e.Text)) return;
        var sanitized = TerminalInput.Paste(e.Text);
        if (sanitized is null || sanitized.Length == 0) { e.Handled = true; return; }
        e.Handled = true;
        Input?.Invoke(sanitized);
    }

    // 多行粘贴整段拒绝，一个字节也不发送。
    public bool Paste(string text)
    {
        var sanitized = TerminalInput.Paste(text);
        if (sanitized is null) { RejectedPaste = TerminalInput.MultilinePasteRejected; return false; }
        RejectedPaste = null;
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
