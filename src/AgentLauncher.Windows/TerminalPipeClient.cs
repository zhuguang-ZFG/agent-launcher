using System.IO.Pipes;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

// 画面管道的 WPF 侧客户端。它只做三件事：连上 pane Worker 的当前用户命名管道、把收到的帧交给卡片、
// 把键盘字节和尺寸写回去。它不创建进程、不持有 Job、不解析输出成命令，也不保存可续接的原始字节流。
// 开关为 false 时不会有任何调用方创建它（ET18）；伪造证据 JSON 也不能让它被创建。
public sealed class TerminalPipeClient : IDisposable, IAsyncDisposable
{
    // protocol.md：输入方向最多 64 KiB，超过时暂停从键盘取新字节，不建无界列表。
    public const int MaxInput = 64 * 1024;
    private const int ConnectTimeoutMilliseconds = 5000;

    private readonly NamedPipeClientStream _stream;
    private readonly SemaphoreSlim _write = new(1, 1);
    private bool _disposed;

    private TerminalPipeClient(NamedPipeClientStream stream) => _stream = stream;

    public static string PipeName(Guid attemptId) => Store.Pipe + "-pty-" + attemptId.ToString("N");

    // 连接在同一次尝试的管道上。Worker 还没起来时先等一小段时间，而不是立刻放弃。
    public static async Task<TerminalPipeClient> ConnectAsync(Guid attemptId, TimeSpan timeout, CancellationToken cancellation)
    {
        if (attemptId == Guid.Empty) throw new ArgumentException("尝试标识不能为空。", nameof(attemptId));
        var stream = new NamedPipeClientStream(".", PipeName(attemptId), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await stream.ConnectAsync((int)Math.Clamp(timeout.TotalMilliseconds, 1, ConnectTimeoutMilliseconds), cancellation);
            return new TerminalPipeClient(stream);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    // 读到帧结束、长度非法或管道断开就返回：不重连、不补发历史。
    public async Task PumpAsync(Func<TerminalSnapshot, Task> onSnapshot, CancellationToken cancellation, Func<TerminalReadback, Task>? onReadback = null)
    {
        while (!cancellation.IsCancellationRequested)
        {
            byte[]? payload;
            try { payload = await TerminalWire.ReadFrameAsync(_stream, cancellation); }
            catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException) { return; }
            if (payload is null) return;
            var message = TerminalWire.Decode(payload);
            if (message?.Kind == TerminalWire.SnapshotKind && message.Snapshot is { } snapshot && snapshot.IsWellFormed)
                await onSnapshot(snapshot);
            else if (message?.Kind == TerminalWire.ReadbackKind && message.Readback is { } page && onReadback is not null)
                await onReadback(page);
        }
    }

    public Task SendReadAsync(TerminalReadRequest request, CancellationToken cancellation)
    {
        if (!request.IsWellFormed) throw new ArgumentException("历史读取请求超出边界。", nameof(request));
        return WriteAsync(TerminalWire.EncodeRead(request), cancellation);
    }

    public async Task SendInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation)
    {
        if (bytes.IsEmpty) return;
        // 超过上限时抛给调用方去暂停取键；这里不截断，也不排队。
        if (bytes.Length > MaxInput) throw new ArgumentOutOfRangeException(nameof(bytes), $"输入超过 {MaxInput} 字节上限。");
        await WriteAsync(TerminalWire.EncodeInput(bytes.Span), cancellation);
    }

    public async Task SendResizeAsync(int columns, int rows, CancellationToken cancellation)
    {
        if (columns is < TerminalScreen.MinColumns or > TerminalScreen.MaxColumns) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows is < TerminalScreen.MinRows or > TerminalScreen.MaxRows) throw new ArgumentOutOfRangeException(nameof(rows));
        await WriteAsync(TerminalWire.EncodeResize(columns, rows), cancellation);
    }

    private async Task WriteAsync(byte[] payload, CancellationToken cancellation)
    {
        var frame = TerminalWire.Frame(payload);
        await _write.WaitAsync(cancellation);
        try { await _stream.WriteAsync(frame, cancellation); await _stream.FlushAsync(cancellation); }
        finally { _write.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _stream.DisposeAsync();
    }
}
