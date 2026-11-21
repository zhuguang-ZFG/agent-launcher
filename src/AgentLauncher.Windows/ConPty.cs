using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

// ConPTY 宿主。HPCON 属于 pane Worker，不属于 WPF：Worker 创建、读、写、关闭伪控制台，
// 关闭顺序按 protocol.md 的六步（取消读循环 → 关输入写句柄 → 有上限地等读循环 → ClosePseudoConsole
// → 关输出读句柄 → 子进程由调用方决定）。ReadOutput 在 wait 到期时返回 0；取消时抛
// OperationCanceledException；挂起的读在超时后保持挂起，不丢已经读到的字节。
public sealed class ConPtyHost : IDisposable
{
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint PipeAccessInbound = 0x00000001;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const int ErrorBrokenPipe = 109;
    private const int ErrorNoData = 232;
    private const int ErrorIoPending = 997;
    private const int ErrorPipeConnected = 535;
    private const uint WaitTimeout = 258;

    private readonly IntPtr _pseudoConsole;
    private readonly SafeFileHandle _inputWrite;
    private readonly SafeFileHandle _outputRead;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private readonly object _gate = new();
    private GCHandle _pinned;
    private IntPtr _overlapped;   // OVERLAPPED 必须放在非托管内存：托管对象会被 GC 搬走，重叠读的地址随之失效。
    private IntPtr _event;
    private static readonly int OverlappedEventOffset = Marshal.OffsetOf<Native.Overlapped>(nameof(Native.Overlapped.Event)).ToInt32();
    private bool _pending;
    private int _available;
    private int _offset;
    private bool _disposed;
    private bool _outputClosed;

    public ConPtyHost(short columns, short rows)
    {
        Validate(columns, rows);
        Columns = columns;
        Rows = rows;
        var inputRead = IntPtr.Zero;
        var inputWrite = IntPtr.Zero;
        var outputWrite = IntPtr.Zero;
        var outputClient = new SafeFileHandle(IntPtr.Zero, true);
        var server = new SafeFileHandle(IntPtr.Zero, true);
        try
        {
            var security = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), InheritHandle = 1 };
            Native.Check(Native.CreatePipe(out inputRead, out inputWrite, ref security, 0));
            // 输出端用可重叠的命名管道，ReadOutput 才能用事件实现真正的超时而不丢数据。
            var name = @"\\.\pipe\AgentLauncher-pty-" + Guid.NewGuid().ToString("N");
            server = Native.CreateNamedPipe(name, PipeAccessInbound | FileFlagOverlapped, PipeRejectRemoteClients, 1, 0, 65536, 0, IntPtr.Zero);
            if (server.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            // 保住 SafeFileHandle 实例直到 CreatePseudoConsole 返回：只取句柄值的临时包装会被终结器提前关闭。
            outputClient = Native.CreateFile(name, GenericWrite, 0, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (outputClient.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            outputWrite = outputClient.DangerousGetHandle();
            if (!Native.ConnectNamedPipe(server, IntPtr.Zero) && Marshal.GetLastWin32Error() != ErrorPipeConnected) throw new Win32Exception(Marshal.GetLastWin32Error());
            // 交给 CreatePseudoConsole 的两端必须可继承；Worker 自己保留的两端在后面关掉继承标记。
            Native.Check(Native.SetHandleInformation(inputRead, 1, 1));
            Native.Check(Native.SetHandleInformation(outputWrite, 1, 1));
            Native.Check(Native.SetHandleInformation(inputWrite, 1, 0));
            var result = Native.CreatePseudoConsole(new Native.Coord { X = columns, Y = rows }, inputRead, outputWrite, 0, out var pseudoConsole);
            if (result != 0 || pseudoConsole == IntPtr.Zero) Marshal.ThrowExceptionForHR(result != 0 ? result : unchecked((int)0x80004005));
            _pseudoConsole = pseudoConsole;
            _inputWrite = new SafeFileHandle(inputWrite, true);
            _outputRead = server;
            inputWrite = IntPtr.Zero;
            _event = Native.CreateEvent(IntPtr.Zero, true, false, "");
            if (_event == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Native.Overlapped>());
            Marshal.StructureToPtr(default(Native.Overlapped), _overlapped, false);
            Marshal.WriteIntPtr(_overlapped, OverlappedEventOffset, _event);
            _pinned = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        }
        catch
        {
            if (inputRead != IntPtr.Zero) Native.CloseHandle(inputRead);
            if (inputWrite != IntPtr.Zero) Native.CloseHandle(inputWrite);
            outputClient.Dispose();
            server.Dispose();
            // 已经建好 HPCON 之后再失败：不关它就会泄漏 conhost 和伪控制台。
            if (_pseudoConsole != IntPtr.Zero) Native.ClosePseudoConsole(_pseudoConsole);
            if (_overlapped != IntPtr.Zero) { Marshal.FreeHGlobal(_overlapped); _overlapped = IntPtr.Zero; }
            if (_event != IntPtr.Zero) { Native.CloseHandle(_event); _event = IntPtr.Zero; }
            throw;
        }
        // ConPTY 复制了它需要的两端；我们自己的副本必须关掉，否则管道永远不会看到 EOF。
        Native.CloseHandle(inputRead);
        outputClient.Dispose();
    }

    public short Columns { get; private set; }
    public short Rows { get; private set; }
    public bool OutputClosed { get { lock (_gate) return _outputClosed; } }

    public static void Validate(short columns, short rows)
    {
        if (columns is < TerminalScreen.MinColumns or > TerminalScreen.MaxColumns) throw new ArgumentOutOfRangeException(nameof(columns), $"列数必须在 {TerminalScreen.MinColumns}..{TerminalScreen.MaxColumns} 之间。");
        if (rows is < TerminalScreen.MinRows or > TerminalScreen.MaxRows) throw new ArgumentOutOfRangeException(nameof(rows), $"行数必须在 {TerminalScreen.MinRows}..{TerminalScreen.MaxRows} 之间。");
    }

    // 尺寸变化在调用方合并；这里只做一次 ResizePseudoConsole。
    public void Resize(short columns, short rows)
    {
        Validate(columns, rows);
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConPtyHost));
            if (columns == Columns && rows == Rows) return;
            var result = Native.ResizePseudoConsole(_pseudoConsole, new Native.Coord { X = columns, Y = rows });
            if (result != 0) Marshal.ThrowExceptionForHR(result);
            Columns = columns;
            Rows = rows;
        }
    }

    // 本机 Agent 必须带 Job 链；ssh.exe 传空链，伪控制台是唯一属性。
    public ProtectedProcess Start(CommandSpec command, string cwd, IReadOnlyList<Job> chain, IReadOnlyDictionary<string, string>? environment = null) =>
        new(command, cwd, chain, _pseudoConsole, environment);

    public void WriteInput(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(ConPtyHost)); }
        var chunk = bytes.ToArray();
        var pinned = GCHandle.Alloc(chunk, GCHandleType.Pinned);
        try
        {
            var offset = 0;
            while (offset < chunk.Length)
            {
                if (!Native.WriteFile(_inputWrite, pinned.AddrOfPinnedObject() + offset, (uint)(chunk.Length - offset), out var written, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is ErrorBrokenPipe or ErrorNoData) throw new IOException("终端输入已关闭，子进程可能已退出。");
                    throw new Win32Exception(error);
                }
                if (written == 0) throw new IOException("终端输入没有写入任何字节。");
                offset += (int)written;
            }
        }
        finally { pinned.Free(); }
    }

    public int ReadOutput(Span<byte> buffer, TimeSpan wait, CancellationToken cancellation)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConPtyHost));
            if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
            if (_offset < _available) return Drain(buffer);
            if (_outputClosed) return 0;
            if (!_pending && !BeginRead()) return 0;
            if (_offset < _available) return Drain(buffer);
            if (_outputClosed) return 0;
            var milliseconds = (uint)Math.Clamp(wait.TotalMilliseconds, 0, int.MaxValue);
            var signalled = Native.WaitForSingleObject(_event, milliseconds);
            if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
            if (signalled == WaitTimeout) return 0;  // 读仍挂起：超时不是数据丢失，也不取消这次读
            if (!Native.GetOverlappedResult(_outputRead, _overlapped, out var transferred, false))
            {
                var error = Marshal.GetLastWin32Error();
                _pending = false;
                if (error is ErrorBrokenPipe or ErrorNoData) { _outputClosed = true; return 0; }
                throw new Win32Exception(error);
            }
            _pending = false;
            _available = (int)transferred;
            _offset = 0;
            if (transferred == 0) { _outputClosed = true; return 0; }
            return Drain(buffer);
        }
    }

    // 关闭顺序第 2 步：关闭输入写句柄，让还在读输入的子进程能看到 EOF。
    public void CloseInput()
    {
        lock (_gate) { _inputWrite.Dispose(); }
    }

    private bool BeginRead()
    {
        Native.ResetEvent(_event);
        if (Native.ReadFile(_outputRead, _pinned.AddrOfPinnedObject(), (uint)_buffer.Length, out var read, _overlapped))
        {
            _available = (int)read;
            _offset = 0;
            if (read == 0) { _outputClosed = true; return false; }
            return true;
        }
        var error = Marshal.GetLastWin32Error();
        if (error == ErrorIoPending) { _pending = true; return true; }
        if (error is ErrorBrokenPipe or ErrorNoData) { _outputClosed = true; return false; }
        throw new Win32Exception(error);
    }

    private int Drain(Span<byte> buffer)
    {
        var count = Math.Min(buffer.Length, _available - _offset);
        _buffer.AsSpan(_offset, count).CopyTo(buffer);
        _offset += count;
        if (_offset >= _available) { _offset = 0; _available = 0; }
        return count;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // ClosePseudoConsole 不能在还有挂起的重叠读时调用：先取消并等这次读真正完成。
            if (_pending)
            {
                Native.CancelIoEx(_outputRead, _overlapped);
                if (Native.WaitForSingleObject(_event, 2000) == 0)
                    Native.GetOverlappedResult(_outputRead, _overlapped, out _, false);
                _pending = false;
            }
            // ClosePseudoConsole 会等 conhost 把输出写完，所以先做一次有上限的排空。它不是「跳过等待」，
            // 也不改关闭顺序；读循环已经按调用方的上限退出，这里只清掉管道里剩下的字节。
            DrainRemaining(TimeSpan.FromMilliseconds(500));
            Native.ClosePseudoConsole(_pseudoConsole);   // 第 4 步
            _outputRead.Dispose();                        // 第 5 步
            _inputWrite.Dispose();
            if (_pinned.IsAllocated) _pinned.Free();
            if (_overlapped != IntPtr.Zero) { Marshal.FreeHGlobal(_overlapped); _overlapped = IntPtr.Zero; }
            if (_event != IntPtr.Zero) { Native.CloseHandle(_event); _event = IntPtr.Zero; }
        }
    }

    private void DrainRemaining(TimeSpan budget)
    {
        var limit = DateTime.UtcNow + budget;
        var sink = new byte[16 * 1024];
        while (DateTime.UtcNow < limit && !_outputClosed)
        {
            if (_offset < _available) { Drain(sink); continue; }
            if (!_pending)
            {
                if (!BeginRead()) return;
                if (_offset < _available) continue;
            }
            var signalled = Native.WaitForSingleObject(_event, 50);
            if (signalled == WaitTimeout) return;
            if (!Native.GetOverlappedResult(_outputRead, _overlapped, out var transferred, false)) { _pending = false; _outputClosed = true; return; }
            _pending = false;
            _available = (int)transferred;
            _offset = 0;
            if (transferred == 0) { _outputClosed = true; return; }
        }
    }
}