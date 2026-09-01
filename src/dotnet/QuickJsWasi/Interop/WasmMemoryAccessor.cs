using System.Buffers.Binary;
using System.Text;
using Wasmtime;

namespace QuickJsWasi.Interop;

internal static class WasmMemoryAccessor
{
    private static readonly Encoding Utf8 = Encoding.UTF8;

    public static string ReadUtf8(Memory memory, int ptr, int len)
        => Utf8.GetString(memory.GetSpan(ptr, len));

    /// <summary>
    /// Decode a WTF-8 byte span to a .NET string. WTF-8 is UTF-8 extended with
    /// 3-byte sequences for surrogate code points (0xED 0xA0-0xBF 0x80-0xBF),
    /// which is how QuickJS encodes lone surrogates via JS_ToCStringLen2. Standard
    /// UTF-8 decoding replaces those sequences with U+FFFD; this decoder preserves them.
    /// </summary>
    public static string DecodeWtf8(ReadOnlySpan<byte> bytes)
    {
        // Fast path: no surrogate-range 3-byte sequences → plain UTF-8.
        var hasSurrogateSeq = false;
        for (var i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == 0xED && bytes[i + 1] >= 0xA0 && bytes[i + 1] <= 0xBF)
            {
                hasSurrogateSeq = true;
                break;
            }
        }

        if (!hasSurrogateSeq)
        {
            return Utf8.GetString(bytes);
        }

        var sb = new StringBuilder(bytes.Length);
        var pos = 0;
        while (pos < bytes.Length)
        {
            var b0 = bytes[pos];
            if (b0 < 0x80)
            {
                sb.Append((char)b0);
                pos++;
            }
            else if (b0 < 0xE0)
            {
                sb.Append((char)(((b0 & 0x1F) << 6) | (bytes[pos + 1] & 0x3F)));
                pos += 2;
            }
            else if (b0 < 0xF0)
            {
                // 3-byte: may be in surrogate range (WTF-8 extension) — emit as-is.
                sb.Append((char)(((b0 & 0x0F) << 12) | ((bytes[pos + 1] & 0x3F) << 6) | (bytes[pos + 2] & 0x3F)));
                pos += 3;
            }
            else
            {
                // 4-byte: code point beyond BMP → emit as surrogate pair.
                var cp = ((b0 & 0x07) << 18) | ((bytes[pos + 1] & 0x3F) << 12) | ((bytes[pos + 2] & 0x3F) << 6) | (bytes[pos + 3] & 0x3F);
                sb.Append(char.ConvertFromUtf32(cp));
                pos += 4;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Encode a .NET string to WTF-8 bytes. Well-formed surrogate pairs are encoded
    /// as 4-byte UTF-8; lone surrogates are encoded as their 3-byte WTF-8 sequences
    /// so that QuickJS (which accepts surrogate-range 3-byte sequences) receives the
    /// exact code units. Embedded NULs are preserved as the 1-byte 0x00.
    /// </summary>
    public static byte[] EncodeWtf8(string s)
    {
        // Fast path: if no lone surrogates, standard UTF-8 is identical to WTF-8.
        // (Paired surrogates produce 4-byte sequences in both encodings.)
        var needsSpecial = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                {
                    needsSpecial = true;
                    break;
                }
                i++; // paired low surrogate — fine for UTF-8
            }
            else if (char.IsLowSurrogate(s[i]))
            {
                needsSpecial = true;
                break;
            }
        }

        if (!needsSpecial) return Utf8.GetBytes(s);

        var bytes = new List<byte>(s.Length * 2);
        for (var i = 0; i < s.Length; i++)
        {
            var code = (int)s[i]; // UTF-16 code unit
            if (code < 0x80)
            {
                bytes.Add((byte)code);
            }
            else if (code < 0x800)
            {
                bytes.Add((byte)(0xC0 | (code >> 6)));
                bytes.Add((byte)(0x80 | (code & 0x3F)));
            }
            else if (code >= 0xD800 && code <= 0xDBFF && i + 1 < s.Length && s[i + 1] >= '\uDC00' && s[i + 1] <= '\uDFFF')
            {
                // Well-formed surrogate pair → 4-byte UTF-8.
                var cp = 0x10000 + ((code - 0xD800) << 10) + (s[i + 1] - 0xDC00);
                bytes.Add((byte)(0xF0 | (cp >> 18)));
                bytes.Add((byte)(0x80 | ((cp >> 12) & 0x3F)));
                bytes.Add((byte)(0x80 | ((cp >> 6) & 0x3F)));
                bytes.Add((byte)(0x80 | (cp & 0x3F)));
                i++; // skip low surrogate
            }
            else
            {
                // BMP char or lone surrogate → 3-byte WTF-8.
                bytes.Add((byte)(0xE0 | (code >> 12)));
                bytes.Add((byte)(0x80 | ((code >> 6) & 0x3F)));
                bytes.Add((byte)(0x80 | (code & 0x3F)));
            }
        }

        return bytes.ToArray();
    }

    /// <summary>
    /// Returns true if the key contains an embedded NUL or a lone surrogate —
    /// characters that cannot safely cross NUL-terminated C-string APIs. Such keys
    /// must be routed through a length-aware guest string value instead.
    /// </summary>
    public static bool StringKeyNeedsValuePath(string key)
    {
        for (var i = 0; i < key.Length; i++)
        {
            if (key[i] == '\0') return true;
            if (char.IsHighSurrogate(key[i]))
            {
                if (i + 1 >= key.Length || !char.IsLowSurrogate(key[i + 1]))
                    return true; // lone high surrogate
                i++; // paired — fine
            }
            else if (char.IsLowSurrogate(key[i]))
            {
                return true; // lone low surrogate
            }
        }
        return false;
    }

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
