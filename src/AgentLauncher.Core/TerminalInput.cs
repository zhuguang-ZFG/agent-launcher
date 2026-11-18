using System.Text;

namespace AgentLauncher.Core;

public enum TerminalKey { Up, Down, Right, Left, Home, End, Delete, Backspace, Enter, Tab }

// 键盘字节和粘贴规则。全部按 design.md 第 7 节的表；未列出的键不在这里。
public static class TerminalInput
{
    public const string MultilinePasteRejected = "多行粘贴已拒绝，避免一次送入多条命令";
    public const byte InterruptByte = 0x03;

    public static byte[] Key(TerminalKey key, bool applicationCursor) => key switch
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
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    // Ctrl+C 只写一个字节；它不结束 Job，也不结束 ssh.exe。
    public static byte[] Interrupt() => [InterruptByte];

    // 多行粘贴整段拒绝，一个字节也不发送。单行去掉完整 ESC、CSI、OSC 与 C0/C1（制表符保留）。
    public static byte[]? Paste(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\n') || text.Contains('\r')) return null;
        var kept = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (value == '\u001b')
            {
                if (index + 1 < text.Length && text[index + 1] == ']')
                {
                    index += 2;
                    while (index < text.Length)
                    {
                        if (text[index] == '\u0007') break;
                        if (text[index] == '\u001b' && index + 1 < text.Length && text[index + 1] == '\\') { index++; break; }
                        index++;
                    }
                    continue;
                }
                if (index + 1 < text.Length && text[index + 1] == '[')
                {
                    index += 2;
                    while (index < text.Length && text[index] is not (>= '\u0040' and <= '\u007e')) index++;
                    continue;
                }
                index++;  // 其他 ESC：丢掉 ESC 和下一字节
                continue;
            }
            if (value == '\t') { kept.Append(value); continue; }
            if (value < '\u0020' || value is >= '\u007F' and <= '\u009F') continue;
            kept.Append(value);
        }
        return Encoding.UTF8.GetBytes(kept.ToString());
    }

    private static byte[] Esc(char introducer, char final) => [0x1B, (byte)introducer, (byte)final];
}
