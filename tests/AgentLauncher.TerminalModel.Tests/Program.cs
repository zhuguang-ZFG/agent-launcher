using System.Text;
using AgentLauncher.Core;

// 阶段 1 的模型检查。每个 ET 编号来自 acceptance-checklist.md；本程序不打开 ConPTY，
// 也不证明 SSH、DRAIN、关窗口或 OMP 的任何一项。
var failures = 0;
var executed = 0;

void Check(string id, string what, Func<(bool Ok, string Detail)> test)
{
    executed++;
    try
    {
        var (ok, detail) = test();
        if (ok) Console.WriteLine($"PASS {id} {what} {detail}");
        else { failures++; Console.WriteLine($"FAIL {id} {what} {detail}"); }
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"FAIL {id} {what} {error.GetType().Name}: {error.Message}");
    }
}

Check("ET01", "宽度和 中文Ag 占 6 列", () =>
{
    var screen = new TerminalScreen();
    screen.Feed("中文Ag");
    var ok = screen.CurrentLineCells == 6
        && screen.CellAt(0, 0) == new TerminalCell('中', 2, 7, 0, false)
        && screen.CellAt(1, 0).IsContinuation
        && screen.CellAt(2, 0) == new TerminalCell('文', 2, 7, 0, false)
        && screen.CellAt(4, 0) == new TerminalCell('A', 1, 7, 0, false)
        && screen.CellAt(5, 0) == new TerminalCell('g', 1, 7, 0, false);
    return (ok, $"cells={screen.CurrentLineCells} 中Width={screen.CellAt(0, 0).Width} 文Width={screen.CellAt(2, 0).Width}");
});

Check("ET02", "SGR 颜色和加粗进格子", () =>
{
    var screen = new TerminalScreen();
    screen.Feed("\u001b[1;31mX");
    var cell = screen.CellAt(0, 0);
    var snapshot = screen.Snapshot();
    snapshot.Primary[0] = TerminalCell.Empty;  // 快照数组是副本，改它不能改屏幕
    var copy = screen.CellAt(0, 0);
    return (cell is { Bold: true, Foreground: 1, Codepoint: 'X' } && copy.Codepoint == 'X', $"Bold={cell.Bold} Foreground={cell.Foreground} copy={copy.Codepoint}");
});

Check("ET03", "UTF-8 跨调用拼成一个汉字", () =>
{
    var screen = new TerminalScreen();
    var bytes = Encoding.UTF8.GetBytes("中");
    screen.Feed(bytes[..1]);
    var first = screen.CurrentLineCells;
    screen.Feed(bytes[1..2]);
    var second = screen.CurrentLineCells;
    screen.Feed(bytes[2..]);
    var cell = screen.CellAt(0, 0);
    return (first == 0 && second == 0 && cell.Codepoint == '中' && cell.Width == 2 && screen.CurrentLineCells == 2,
        $"half={first}/{second} codepoint=U+{cell.Codepoint:X4} width={cell.Width} cells={screen.CurrentLineCells}");
});

Check("ET04", "无换行也不超过列宽", () =>
{
    var screen = new TerminalScreen(20, 4);
    screen.Feed(new string('a', 40));
    var snapshot = screen.Snapshot();
    var widest = 0;
    for (var row = 0; row < 4; row++)
    {
        var last = -1;
        for (var column = 0; column < 20; column++) if (snapshot.Primary[row * 20 + column].Codepoint != 0) last = column;
        widest = Math.Max(widest, last + 1);
    }
    return (screen.CurrentLineCells == 20 && widest == 20 && snapshot.Primary.Length == 80,
        $"CurrentLineCells={screen.CurrentLineCells} widest={widest} visibleArray={snapshot.Primary.Length}");
});

Check("ET05", "CSI 光标和擦除", () =>
{
    var screen = new TerminalScreen();
    screen.Feed("ABCD");
    screen.Feed("\u001b[3G");
    screen.Feed("\u001b[K");
    var ok = screen.CellAt(0, 0).Codepoint == 'A' && screen.CellAt(1, 0).Codepoint == 'B'
        && screen.CellAt(2, 0) == TerminalCell.Empty && screen.CellAt(3, 0) == TerminalCell.Empty
        && screen.CurrentLineCells == 2;
    return (ok, $"cells={screen.CurrentLineCells} [2]={screen.CellAt(2, 0).Codepoint} [3]={screen.CellAt(3, 0).Codepoint}");
});

Check("ET06", "备用屏幕 1049 来回", () =>
{
    var screen = new TerminalScreen();
    screen.Feed("主屏");
    screen.Feed("\u001b[?1049h");
    screen.Feed("备用");
    var inAlternate = screen.Active == ScreenId.Alternate && screen.VisibleText().TrimEnd() == "备用";
    screen.Feed("\u001b[?1049l");
    var ok = inAlternate && screen.Active == ScreenId.Primary && screen.VisibleText().TrimEnd() == "主屏";
    return (ok, $"alternate={inAlternate} primary={screen.VisibleText().TrimEnd()} active={screen.Active}");
});

Check("ET07", "OSC 52 不执行；超长 CSI 丢到终止字节", () =>
{
    var osc = new TerminalScreen();
    osc.Feed("可见");
    osc.Feed("\u001b]52;c;SGVsbG8=\u0007");
    osc.Feed("后");
    var oscText = osc.VisibleText().TrimEnd();
    var oscOk = oscText == "可见后" && !oscText.Contains("SGVsbG8", StringComparison.Ordinal);

    var csi = new TerminalScreen();
    csi.Feed("可见");
    csi.Feed("\u001b[" + new string('1', 5000) + "m");
    csi.Feed("后");
    var csiText = csi.VisibleText().TrimEnd();
    var csiOk = csiText == "可见后";
    return (oscOk && csiOk, $"osc=\"{oscText}\" csi=\"{csiText}\"");
});

Check("ET08", "2000 逻辑行和 8 MiB 消费", () =>
{
    var screen = new TerminalScreen(20, 4);
    var line = Encoding.UTF8.GetBytes(new string('x', 78) + "\r\n");
    var chunk = new byte[line.Length * 512];
    for (var index = 0; index < 512; index++) line.CopyTo(chunk, index * line.Length);
    var total = 8L * 1024 * 1024;
    var fed = 0L;
    while (fed < total) { screen.Feed(chunk); fed += chunk.Length; }
    var snapshot = screen.Snapshot();
    var ok = snapshot.TruncatedLines > 0 && snapshot.Primary.Length == 80
        && screen.ScrollbackLines <= TerminalScreen.MaxScrollbackLines && screen.ScrollbackBytes <= TerminalScreen.MaxScrollbackBytes;
    return (ok, $"fed={fed} truncated={snapshot.TruncatedLines} visibleArray={snapshot.Primary.Length} historyLines={screen.ScrollbackLines} historyBytes={screen.ScrollbackBytes}");
});

Check("ET09", "多行粘贴整段拒绝", () =>
{
    var line = TerminalInput.Paste("npm install\nrm -rf /");
    var carriage = TerminalInput.Paste("a\rb");
    var single = TerminalInput.Paste("echo 中文");
    var ok = line is null && carriage is null && single is not null
        && TerminalInput.MultilinePasteRejected == "多行粘贴已拒绝，避免一次送入多条命令"
        && Encoding.UTF8.GetString(single) == "echo 中文";
    return (ok, $"lf={(line is null ? "null" : "bytes")} cr={(carriage is null ? "null" : "bytes")} message=\"{TerminalInput.MultilinePasteRejected}\"");
});

Check("ET10", "单行 ESC[31m 整段剥离", () =>
{
    var sanitized = TerminalInput.Paste("A\u001b[31m\tB");
    var text = sanitized is null ? "<null>" : Encoding.UTF8.GetString(sanitized);
    var ok = sanitized is not null && text == "A\tB";
    return (ok, $"result=\"{text.Replace("\t", "\\t")}\"");
});

Check("ET11", "键序列和 0x03", () =>
{
    static string Text(byte[] bytes) => string.Concat(bytes.Select(value => value == 0x1B ? "ESC" : value.ToString("X2") + " "));
    var expectations = new (TerminalKey Key, bool Application, byte[] Bytes)[]
    {
        (TerminalKey.Up, false, [0x1B, (byte)'[', (byte)'A']),
        (TerminalKey.Down, false, [0x1B, (byte)'[', (byte)'B']),
        (TerminalKey.Right, false, [0x1B, (byte)'[', (byte)'C']),
        (TerminalKey.Left, false, [0x1B, (byte)'[', (byte)'D']),
        (TerminalKey.Up, true, [0x1B, (byte)'O', (byte)'A']),
        (TerminalKey.Down, true, [0x1B, (byte)'O', (byte)'B']),
        (TerminalKey.Right, true, [0x1B, (byte)'O', (byte)'C']),
        (TerminalKey.Left, true, [0x1B, (byte)'O', (byte)'D']),
        (TerminalKey.Home, false, [0x1B, (byte)'[', (byte)'H']),
        (TerminalKey.End, false, [0x1B, (byte)'[', (byte)'F']),
        (TerminalKey.Home, true, [0x1B, (byte)'O', (byte)'H']),
        (TerminalKey.End, true, [0x1B, (byte)'O', (byte)'F']),
        (TerminalKey.Delete, false, [0x1B, (byte)'[', (byte)'3', (byte)'~']),
        (TerminalKey.Delete, true, [0x1B, (byte)'[', (byte)'3', (byte)'~']),
        (TerminalKey.Backspace, false, [0x7F]),
        (TerminalKey.Backspace, true, [0x7F]),
        (TerminalKey.Enter, false, [0x0D]),
        (TerminalKey.Tab, false, [0x09])
    };
    var mismatches = expectations.Where(entry => !TerminalInput.Key(entry.Key, entry.Application).SequenceEqual(entry.Bytes)).ToArray();
    var interrupt = TerminalInput.Interrupt();
    var ok = mismatches.Length == 0 && interrupt.Length == 1 && interrupt[0] == 0x03;
    return (ok, $"mismatches={mismatches.Length} ctrlC={Text(interrupt)}");
});

Check("ET12", "旧世代丢弃，新世代整表替换", () =>
{
    TerminalCell[] Cells(byte foreground) =>
    [
        new('A', 1, foreground, 0, false), TerminalCell.Empty,
        TerminalCell.Empty, TerminalCell.Empty
    ];
    var view = new TerminalView();
    var red = new TerminalSnapshot(4, ScreenId.Primary, 2, 2, 0, 0, true, false, 0, Cells(1), Cells(7));
    var stale = new TerminalSnapshot(3, ScreenId.Primary, 2, 2, 0, 0, true, false, 0, Cells(2), Cells(7));
    var next = new TerminalSnapshot(5, ScreenId.Primary, 2, 2, 0, 0, true, false, 0, Cells(4), Cells(7));
    var acceptedRed = view.Apply(red);
    var rejectedStale = view.Apply(stale);
    var redKept = view.Current?.Primary[0].Foreground == 1;
    var acceptedNext = view.Apply(next);
    var replaced = view.Current?.Primary[0].Foreground == 4;
    var ok = acceptedRed && !rejectedStale && redKept && acceptedNext && replaced;
    return (ok, $"red={acceptedRed} stale={(rejectedStale ? "accepted" : "rejected")} redKept={redKept} next={acceptedNext} replaced={replaced}");
});

Check("CODEC", "快照编解码、帧和上限", () =>
{
    var screen = new TerminalScreen(20, 4);
    screen.Feed("中文Ag\u001b[1;31m红");
    var snapshot = screen.Snapshot();
    var payload = TerminalWire.EncodeSnapshot(snapshot);
    var decoded = TerminalWire.Decode(payload);
    var roundTrip = decoded?.Snapshot is { } back && back.Generation == snapshot.Generation && back.Columns == 20
        && back.Primary.SequenceEqual(snapshot.Primary) && back.Alternate.SequenceEqual(snapshot.Alternate);

    var worst = new TerminalCell[TerminalScreen.MaxColumns * TerminalScreen.MaxRows];
    for (var index = 0; index < worst.Length; index++) worst[index] = new TerminalCell(0x4E00 + index % 0x1000, 2, 7, 0, false);
    var worstSnapshot = new TerminalSnapshot(1, ScreenId.Primary, TerminalScreen.MaxColumns, TerminalScreen.MaxRows, 0, 0, true, false, 0, worst, worst);
    var worstPayload = TerminalWire.EncodeSnapshot(worstSnapshot);

    var frame = TerminalWire.Frame(payload);
    using var stream = new MemoryStream(frame);
    var reframed = TerminalWire.ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
    var input = TerminalWire.Decode(TerminalWire.EncodeInput([0x03, 0x41]));
    var ok = roundTrip && worstPayload.Length <= TerminalWire.MaxPayload && reframed is not null && reframed.AsSpan().SequenceEqual(payload)
        && input?.Input is { Length: 2 } bytes && bytes[0] == 0x03;
    return (ok, $"roundTrip={roundTrip} worstCase={worstPayload.Length}/{TerminalWire.MaxPayload} frame={frame.Length} input={input?.Input?.Length}");
});

Console.WriteLine($"SUMMARY executed={executed} failed={failures}");
return failures == 0 ? 0 : 1;