using System.Text;

namespace AgentLauncher.Core;

public enum TerminalKey { Up, Down, Right, Left, Home, End, Delete, Backspace, Enter, Tab, PageUp, PageDown }
[Flags]
public enum TerminalModifiers { None = 0, Shift = 1, Alt = 2, Control = 4 }

// 键盘字节、xterm 修饰键参数，以及协商/确认后的粘贴清洗规则。
public static class TerminalInput
{
    public const string MultilinePasteRejected = "多行粘贴已拒绝，避免一次送入多条命令";
    public const byte InterruptByte = 0x03;

    public static byte[] Key(TerminalKey key, bool applicationCursor, TerminalModifiers modifiers = TerminalModifiers.None)
    {
        if (key == TerminalKey.Tab && modifiers.HasFlag(TerminalModifiers.Control)) return []; // 标签导航由 UI 消费。
        if (key == TerminalKey.Tab && modifiers.HasFlag(TerminalModifiers.Shift)) return Esc('[', 'Z');
        if (modifiers != TerminalModifiers.None)
        {
            var final = key switch { TerminalKey.Up => 'A', TerminalKey.Down => 'B', TerminalKey.Right => 'C', TerminalKey.Left => 'D', TerminalKey.Home => 'H', TerminalKey.End => 'F', _ => '\0' };
            var parameter = 1 + (int)modifiers;
            if (final != '\0') return Encoding.ASCII.GetBytes($"\u001b[1;{parameter}{final}");
            var number = key switch { TerminalKey.Delete => 3, TerminalKey.PageUp => 5, TerminalKey.PageDown => 6, _ => 0 };
            if (number != 0) return Encoding.ASCII.GetBytes($"\u001b[{number};{parameter}~");
        }
        return key switch
        {
            TerminalKey.Up => applicationCursor ? Esc('O', 'A') : Esc('[', 'A'),
            TerminalKey.Down => applicationCursor ? Esc('O', 'B') : Esc('[', 'B'),
            TerminalKey.Right => applicationCursor ? Esc('O', 'C') : Esc('[', 'C'),
            TerminalKey.Left => applicationCursor ? Esc('O', 'D') : Esc('[', 'D'),
            TerminalKey.Home => applicationCursor ? Esc('O', 'H') : Esc('[', 'H'),
            TerminalKey.End => applicationCursor ? Esc('O', 'F') : Esc('[', 'F'),
            TerminalKey.Delete => [0x1B, (byte)'[', (byte)'3', (byte)'~'],
            TerminalKey.Backspace => [0x7F],
            TerminalKey.Enter => [0x0D],
            TerminalKey.Tab => [0x09],
            TerminalKey.PageUp => [0x1B, (byte)'[', (byte)'5', (byte)'~'],
            TerminalKey.PageDown => [0x1B, (byte)'[', (byte)'6', (byte)'~'],
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
    }

    // Ctrl+C 只写一个字节；它不结束 Job，也不结束 ssh.exe。
    public static byte[] Interrupt() => [InterruptByte];

    public static bool RequiresPasteConfirmation(string text, bool bracketedPaste) => !bracketedPaste && (text.Contains('\n') || text.Contains('\r'));

    // 未协商 bracketed paste 时，多行必须由用户确认；清洗后才添加可信包裹，剪贴板无法提前关闭它。
    public static byte[]? Paste(string text, bool bracketedPaste = false, bool confirmedMultiline = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (RequiresPasteConfirmation(text, bracketedPaste) && !confirmedMultiline) return null;
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var kept = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (value is '\u001b' or '\u009b' or '\u009d')
            {
                if (value == '\u009d' || index + 1 < text.Length && text[index + 1] == ']')
                {
                    index += value == '\u009d' ? 1 : 2;
                    while (index < text.Length)
                    {
                        if (text[index] is '\u0007' or '\u009c') break;
                        if (text[index] == '\u001b' && index + 1 < text.Length && text[index + 1] == '\\') { index++; break; }
                        index++;
                    }
                    continue;
                }
                if (value == '\u009b' || index + 1 < text.Length && text[index + 1] == '[')
                {
                    index += value == '\u009b' ? 1 : 2;
                    while (index < text.Length && text[index] is not (>= '\u0040' and <= '\u007e')) index++;
                    continue;
                }
                index++;  // 其他 ESC：丢掉 ESC 和下一字节
                continue;
            }
            if (value == '\n') { kept.Append(bracketedPaste ? '\n' : '\r'); continue; }
            if (value == '\t') { kept.Append(value); continue; }
            if (value < '\u0020' || value is >= '\u007F' and <= '\u009F') continue;
            kept.Append(value);
        }
        return Encoding.UTF8.GetBytes(bracketedPaste ? "\u001b[200~" + kept + "\u001b[201~" : kept.ToString());
    }

    private static byte[] Esc(char introducer, char final) => [0x1B, (byte)introducer, (byte)final];
}
