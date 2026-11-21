using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentLauncher.Core;

// 画面管道的线上格式：4 字节小端长度 + UTF-8 JSON。长度不在 1..262144 之间时调用方直接断开，不解析正文。
// 快照里的格子数组用「行程 + 打包格子」的 base64 承载：每个格子 4 字节（码点 21 位、宽度 2 位、
// 加粗 1 位、前景 4 位、背景 4 位），相同格子的行程只写一次。两块 200x60 屏幕全部不同也只有约 160 KiB，
// 因此快照本身不会撞上帧上限，历史行也从不进入快照。
public static class TerminalWire
{
    public const int MaxPayload = 262144;
    public const string SnapshotKind = "snapshot";
    public const string InputKind = "input";
    public const string ResizeKind = "resize";
    public const int MaxCells = TerminalScreen.MaxColumns * TerminalScreen.MaxRows;

    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter(), new TerminalCellArrayConverter() }
    };

    private sealed record SnapshotFrame(string Kind, TerminalSnapshot Snapshot);
    private sealed record InputFrame(string Kind, string Base64);
    private sealed record ResizeFrame(string Kind, int Columns, int Rows);

    public static byte[] EncodeSnapshot(TerminalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new SnapshotFrame(SnapshotKind, snapshot), Json);
        if (payload.Length > MaxPayload) throw new InvalidOperationException("快照超过帧上限，未发送。");
        return payload;
    }

    public static byte[] EncodeInput(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.SerializeToUtf8Bytes(new InputFrame(InputKind, Convert.ToBase64String(bytes)), Json);

    // 尺寸变化在 UI 侧合并，Worker 收到后只做一次 ResizePseudoConsole。
    public static byte[] EncodeResize(int columns, int rows) =>
        JsonSerializer.SerializeToUtf8Bytes(new ResizeFrame(ResizeKind, columns, rows), Json);

    public static TerminalMessage? Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 1 or > MaxPayload) return null;
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Kind", out var kind) || kind.ValueKind != JsonValueKind.String) return null;
            switch (kind.GetString())
            {
                case SnapshotKind:
                    var snapshot = root.TryGetProperty("Snapshot", out var value) ? value.Deserialize<TerminalSnapshot>(Json) : null;
                    return snapshot is null ? null : new TerminalMessage(SnapshotKind, snapshot, null);
                case InputKind:
                    if (!root.TryGetProperty("Base64", out var encoded) || encoded.ValueKind != JsonValueKind.String) return null;
                    var bytes = Convert.FromBase64String(encoded.GetString() ?? "");
                    return new TerminalMessage(InputKind, null, bytes);
                case ResizeKind:
                    if (!root.TryGetProperty("Columns", out var columns) || !root.TryGetProperty("Rows", out var rows)) return null;
                    if (!columns.TryGetInt32(out var columnCount) || !rows.TryGetInt32(out var rowCount)) return null;
                    return new TerminalMessage(ResizeKind, null, null, columnCount, rowCount);
                default:
                    return null;
            }
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // 4 字节小端长度前缀。payload 为空或超过上限时拒绝发送。
    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 1 or > MaxPayload) throw new ArgumentOutOfRangeException(nameof(payload), "帧长度超出协议边界。");
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, cancellation)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaxPayload) return null;
        var payload = new byte[length];
        return await ReadExactlyAsync(stream, payload, cancellation) ? payload : null;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellation)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellation);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }
}

public sealed record TerminalMessage(string Kind, TerminalSnapshot? Snapshot, byte[]? Input, int? Columns = null, int? Rows = null);

public sealed class TerminalCellArrayConverter : JsonConverter<TerminalCell[]>
{
    public override TerminalCell[] Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("格子数组必须是 base64 字符串。");
        var text = reader.GetString() ?? "";
        byte[] payload;
        try { payload = Convert.FromBase64String(text); }
        catch (FormatException) { throw new JsonException("格子数组不是合法 base64。"); }
        var cells = new List<TerminalCell>(Math.Min(TerminalWire.MaxCells, payload.Length));
        var offset = 0;
        while (offset < payload.Length)
        {
            var count = ReadVarint(payload, ref offset);
            if (count < 1 || cells.Count + count > TerminalWire.MaxCells) throw new JsonException("格子行程超出边界。");
            if (offset + 4 > payload.Length) throw new JsonException("格子行程被截断。");
            var packed = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
            offset += 4;
            var cell = Unpack(packed);
            for (var i = 0; i < count; i++) cells.Add(cell);
        }
        return [.. cells];
    }

    public override void Write(Utf8JsonWriter writer, TerminalCell[] value, JsonSerializerOptions options)
    {
        var buffer = new List<byte>(Math.Max(8, value.Length));
        var index = 0;
        while (index < value.Length)
        {
            var packed = Pack(value[index]);
            var run = 1;
            while (index + run < value.Length && Pack(value[index + run]) == packed) run++;
            WriteVarint(buffer, run);
            buffer.Add((byte)packed);
            buffer.Add((byte)(packed >> 8));
            buffer.Add((byte)(packed >> 16));
            buffer.Add((byte)(packed >> 24));
            index += run;
        }
        writer.WriteStringValue(Convert.ToBase64String(buffer.ToArray()));
    }

    private static uint Pack(TerminalCell cell) =>
        (uint)(cell.Codepoint & 0x1FFFFF) |
        ((uint)(cell.Width & 0x3) << 21) |
        (cell.Bold ? 1u << 23 : 0u) |
        ((uint)(cell.Foreground & 0xF) << 24) |
        ((uint)(cell.Background & 0xF) << 28);

    private static TerminalCell Unpack(uint packed) => new(
        (int)(packed & 0x1FFFFF),
        (int)((packed >> 21) & 0x3),
        (byte)((packed >> 24) & 0xF),
        (byte)((packed >> 28) & 0xF),
        (packed & (1u << 23)) != 0);

    private static void WriteVarint(List<byte> buffer, int value)
    {
        var remainder = (uint)value;
        while (remainder >= 0x80)
        {
            buffer.Add((byte)(remainder | 0x80));
            remainder >>= 7;
        }
        buffer.Add((byte)remainder);
    }

    private static int ReadVarint(byte[] payload, ref int offset)
    {
        var value = 0;
        var shift = 0;
        while (true)
        {
            if (offset >= payload.Length || shift > 21) throw new JsonException("格子行程不是合法变长整数。");
            var current = payload[offset++];
            value |= (current & 0x7F) << shift;
            if ((current & 0x80) == 0) return value;
            shift += 7;
        }
    }
}