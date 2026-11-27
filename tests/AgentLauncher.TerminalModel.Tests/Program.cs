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

Check("ET09", "未协商且未确认的多行粘贴拒绝", () =>
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

Check("ET01", "中文横纵缩放保留完整宽字符并清除截断半格", () =>
{
    var screen = new TerminalScreen(20, 4);screen.Feed("中文Ag");
    screen.Resize(40, 4);var wider=screen.VisibleText().TrimEnd()=="中文Ag"&&screen.CellAt(1,0).IsContinuation;
    screen.Resize(40, 8);var taller=screen.VisibleText().TrimEnd()=="中文Ag"&&screen.CellAt(3,0).IsContinuation;
    screen.Resize(3,8);var clipped=screen.VisibleText().TrimEnd()=="中"&&screen.CellAt(2,0)==TerminalCell.Empty;
    return(wider&&taller&&clipped,$"wider={wider} taller={taller} clipped={clipped}");
});

Check("ET09", "bracketed paste 协商、取消和确认多行粘贴", () =>
{
    const string text="中文\r\nnext\rthird\n\u001b[201~\u001b]52;c;secret\u0007\u009b31mDONE\u0003";
    var screen=new TerminalScreen();screen.Feed("\u001b[?2004h");var enabled=screen.Snapshot().BracketedPaste;
    var wrapped=Encoding.UTF8.GetString(TerminalInput.Paste(text,enabled)!);
    screen.Feed("\u001b[?2004l");var disabled=!screen.Snapshot().BracketedPaste;
    var denied=TerminalInput.Paste(text) is null&&TerminalInput.RequiresPasteConfirmation(text,false);
    var confirmed=Encoding.UTF8.GetString(TerminalInput.Paste(text,false,true)!);
    var ok=enabled&&disabled&&denied&&wrapped=="\u001b[200~中文\nnext\nthird\nDONE\u001b[201~"&&confirmed=="中文\rnext\rthird\rDONE";
    return(ok,$"enabled={enabled} disabled={disabled} denied={denied} wrapped={wrapped.Replace("\u001b","ESC")} confirmedBytes={confirmed.Length}");
});

Check("ET11", "Shift+Tab、Ctrl+方向键和标签导航不降级", () =>
{
    var shifted=Encoding.ASCII.GetString(TerminalInput.Key(TerminalKey.Tab,false,TerminalModifiers.Shift));
    var word=Encoding.ASCII.GetString(TerminalInput.Key(TerminalKey.Left,true,TerminalModifiers.Control));
    var combined=Encoding.ASCII.GetString(TerminalInput.Key(TerminalKey.Right,false,TerminalModifiers.Control|TerminalModifiers.Shift));
    var tabs=TerminalInput.Key(TerminalKey.Tab,false,TerminalModifiers.Control).Length;
    return(shifted=="\u001b[Z"&&word=="\u001b[1;5D"&&combined=="\u001b[1;6C"&&tabs==0,$"shiftTab={shifted.Length} ctrlLeft={word.Length} ctrlTabBytes={tabs}");
});

Check("ET08", "历史按需一屏读取、搜索与快照大小无关", () =>
{
    var screen=new TerminalScreen(20,4);
    for(var i=0;i<2050;i++)screen.Feed($"line-{i:D4}\r\n");
    var snapshot=screen.Snapshot();
    var page=screen.Readback(new(1,0));
    var historyText=TerminalText.ExtractLine(page.Cells.AsSpan(0,20));
    var match=screen.Readback(new(2,page.FirstLine,"LINE-2000"));
    var missing=screen.Readback(new(3,page.FirstLine,"absent"));
    var wire=TerminalWire.Decode(TerminalWire.EncodeReadback(match));
    var request=TerminalWire.Decode(TerminalWire.EncodeRead(new(7,page.FirstLine,"line-2000",true)));
    var ok=page.IsWellFormed&&page.Cells.Length==80&&page.FirstLine==47&&historyText=="line-0047"&&snapshot.HistoryLines==2000&&
        match.MatchLine==2000&&match.MatchColumn==0&&missing.MatchLine==-1&&wire?.Readback?.Cells.SequenceEqual(match.Cells)==true&&request?.Request?.Backward==true&&
        TerminalWire.EncodeSnapshot(snapshot).Length<2048&&TerminalWire.Decode(TerminalWire.EncodeRead(new(9,0,new string('x',257)))) is null;
    screen.Feed("\u001b[?1049hALT");var alternate=screen.Readback(new(4,0,"line-2000"));
    ok&=alternate.HistoryLines==0&&alternate.MatchLine==-1&&TerminalText.ExtractLine(alternate.Cells.AsSpan(0,20))=="ALT";
    return(ok,$"first={page.FirstLine} history={snapshot.HistoryLines} pageCells={page.Cells.Length} matchLine={match.MatchLine} alternateHistory={alternate.HistoryLines}");
});

Check("ET01", "选择中文半格和多行空白可准确复制", () =>
{
    var screen=new TerminalScreen(20,4);screen.Feed("中文Ag\r\n\r\n尾行");
    var snapshot=screen.Snapshot();
    var half=TerminalText.Selection(snapshot.Visible,20,1,3);
    var all=TerminalText.Selection(snapshot.Visible,20,0,44);
    var reversed=TerminalText.Selection(snapshot.Visible,20,44,0);
    return(half=="中文"&&all=="中文Ag"+Environment.NewLine+Environment.NewLine+"尾行"&&reversed==all,$"half={half} allLength={all.Length} reversed={reversed==all}");
});

Check("ET08", "同一行多处中文搜索双向循环", () =>
{
    var screen=new TerminalScreen(20,4);screen.Feed("中文 xx 中文\r\nnext");
    var first=screen.Readback(new(1,0,"中文"));
    var second=screen.Readback(new(2,first.MatchLine,"中文",false,first.MatchColumn));
    var wrapped=screen.Readback(new(3,second.MatchLine,"中文",false,second.MatchColumn));
    var previous=screen.Readback(new(4,second.MatchLine,"中文",true,second.MatchColumn));
    return(first.MatchColumn==0&&second.MatchColumn==8&&wrapped.MatchColumn==0&&previous.MatchColumn==0&&first.MatchLength==4,
        $"columns={first.MatchColumn}/{second.MatchColumn}/{wrapped.MatchColumn}/{previous.MatchColumn} width={first.MatchLength}");
});

Check("ET12", "拒绝畸形历史匹配与字符，窄窗搜索不返回隐藏列", () =>
{
    var screen=new TerminalScreen(20,4);screen.Feed("中文 xx 中文\r\nline2\r\nline3\r\nline4\r\n");
    var page=screen.Readback(new(1,0,"中文"));
    var bad= new[]{page with{MatchLength=-1},page with{MatchColumn=20},page with{MatchLine=page.TopLine+page.Rows},page with{Active=(ScreenId)99}};
    var cells=(TerminalCell[])page.Cells.Clone();cells[0]=cells[0] with{Codepoint=0xd800};
    var rejected=bad.All(value=>!value.IsWellFormed&&TerminalWire.Decode(TerminalWire.EncodeReadback(value)) is null)&&!(page with{Cells=cells}).IsWellFormed;
    screen.Resize(3,4);var narrow=screen.Readback(new(2,0,"中文"));var visible=screen.Readback(new(3,0,"中"));
    return(rejected&&narrow.IsWellFormed&&narrow.MatchLine==-1&&visible.IsWellFormed&&visible.MatchLength==2,$"rejected={rejected} narrowMatch={narrow.MatchLine} visibleWidth={visible.MatchLength}");
});

Console.WriteLine($"SUMMARY executed={executed} failed={failures}");
return failures == 0 ? 0 : 1;
