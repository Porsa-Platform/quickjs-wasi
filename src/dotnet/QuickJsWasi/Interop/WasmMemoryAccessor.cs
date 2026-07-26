using System.Buffers.Binary;
using System.Text;
using Wasmtime;

namespace QuickJsWasi.Interop;

internal static class WasmMemoryAccessor
{
    private static readonly Encoding Utf8 = Encoding.UTF8;

    public static string ReadUtf8(Memory memory, int ptr, int len)
        => Utf8.GetString(memory.GetSpan(ptr, len));

    public static string ReadUtf8(Memory memory, Caller caller, int ptr, int len)
        => ReadUtf8(memory, ptr, len);

    public static string ReadCString(Memory memory, int ptr)
    {
        var span = memory.GetSpan<byte>(0);
        var end = ptr;
        while (span[end] != 0)
        {
            end++;
        }

        return Utf8.GetString(span.Slice(ptr, end - ptr));
    }

    public static string ReadCString(Memory memory, Caller caller, int ptr)
        => ReadCString(memory, ptr);

    public static byte[] ReadBytes(Memory memory, Caller caller, int ptr, int len)
        => ReadBytes(memory, ptr, len);

    public static byte[] ReadBytes(Memory memory, int ptr, int len)
        => memory.GetSpan(ptr, len).ToArray();

    public static void WriteBytes(Memory memory, Caller caller, int ptr, ReadOnlySpan<byte> bytes)
        => WriteBytes(memory, ptr, bytes);

    public static void WriteBytes(Memory memory, int ptr, ReadOnlySpan<byte> bytes)
        => bytes.CopyTo(memory.GetSpan(ptr, bytes.Length));

    public static int ReadInt32(Memory memory, Caller caller, int ptr)
        => ReadInt32(memory, ptr);

    public static int ReadInt32(Memory memory, int ptr)
        => BinaryPrimitives.ReadInt32LittleEndian(memory.GetSpan(ptr, 4));

    public static uint ReadUInt32(Memory memory, int ptr)
        => BinaryPrimitives.ReadUInt32LittleEndian(memory.GetSpan(ptr, 4));

    public static void WriteInt32(Memory memory, int ptr, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(memory.GetSpan(ptr, 4), value);

    public static void WriteUInt32(Memory memory, int ptr, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(memory.GetSpan(ptr, 4), value);

    public static void WriteUInt64(Memory memory, Caller caller, int ptr, ulong value)
        => WriteUInt64(memory, ptr, value);

    public static void WriteUInt64(Memory memory, int ptr, ulong value)
        => BinaryPrimitives.WriteUInt64LittleEndian(memory.GetSpan(ptr, 8), value);
}
