using System.Buffers.Binary;
using System.Text;

namespace QuickJsWasi;

public sealed record Snapshot(
    byte[] Memory,
    int StackPointer,
    int RuntimePtr,
    int ContextPtr,
    IReadOnlyList<SnapshotExtension>? Extensions = null)
{
    public const uint Magic = 0x514A5353;
    public const byte Version = 2;
    public const int HeaderSize = 24;

    public static byte[] Serialize(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var extensions = snapshot.Extensions ?? Array.Empty<SnapshotExtension>();
        var utf8 = Encoding.UTF8;
        var encoded = extensions
            .Select(x => new
            {
                Name = utf8.GetBytes(x.Name),
                Init = utf8.GetBytes(x.InitFunction),
                x.MemoryBase,
                x.TableBase
            })
            .ToArray();

        var extBytes = 4 + encoded.Sum(x => 4 + x.Name.Length + 4 + 4 + 4 + x.Init.Length);
        var result = new byte[HeaderSize + extBytes + snapshot.Memory.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), Magic);
        result[4] = Version;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), checked((uint)snapshot.Memory.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), unchecked((uint)snapshot.StackPointer));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), unchecked((uint)snapshot.RuntimePtr));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20, 4), unchecked((uint)snapshot.ContextPtr));
        var offset = HeaderSize;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset, 4), checked((uint)encoded.Length));
        offset += 4;
        foreach (var item in encoded)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset, 4), checked((uint)item.Name.Length));
            offset += 4;
            item.Name.CopyTo(result, offset);
            offset += item.Name.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset, 4), unchecked((uint)item.MemoryBase));
            offset += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset, 4), unchecked((uint)item.TableBase));
            offset += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset, 4), checked((uint)item.Init.Length));
            offset += 4;
            item.Init.CopyTo(result, offset);
            offset += item.Init.Length;
        }

        snapshot.Memory.CopyTo(result, offset);
        return result;
    }

    public static Snapshot Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            throw new InvalidOperationException("Invalid snapshot: too small.");
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(data[..4]) != Magic)
        {
            throw new InvalidOperationException("Invalid snapshot: bad magic.");
        }

        var version = data[4];
        if (version is not (1 or Version))
        {
            throw new InvalidOperationException($"Unsupported snapshot version: {version}.");
        }

        var memorySize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4)));
        var stackPointer = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12, 4)));
        var runtimePtr = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16, 4)));
        var contextPtr = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20, 4)));

        var offset = HeaderSize;
        var extensions = new List<SnapshotExtension>();
        if (version >= Version)
        {
            var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)));
            offset += 4;
            for (var i = 0; i < count; i++)
            {
                var nameLen = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)));
                offset += 4;
                var name = Encoding.UTF8.GetString(data.Slice(offset, nameLen));
                offset += nameLen;
                var memoryBase = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)));
                offset += 4;
                var tableBase = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)));
                offset += 4;
                var initLen = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)));
                offset += 4;
                var init = Encoding.UTF8.GetString(data.Slice(offset, initLen));
                offset += initLen;
                extensions.Add(new SnapshotExtension(name, memoryBase, tableBase, init));
            }
        }

        if (data.Length < offset + memorySize)
        {
            throw new InvalidOperationException("Invalid snapshot: truncated memory image.");
        }

        return new Snapshot(data.Slice(offset, memorySize).ToArray(), stackPointer, runtimePtr, contextPtr, extensions);
    }
}

public sealed record SnapshotExtension(string Name, int MemoryBase, int TableBase, string InitFunction);
