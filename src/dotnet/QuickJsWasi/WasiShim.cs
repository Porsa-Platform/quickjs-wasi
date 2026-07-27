using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using QuickJsWasi.Interop;
using Wasmtime;

namespace QuickJsWasi;

public class WasiShim
{
    protected const int ErrnoSuccess = 0;
    protected const int ErrnoBadf = 8;
    protected const int ErrnoNosys = 52;
    private static readonly Encoding Utf8 = Encoding.UTF8;

    public virtual int ClockTimeGet(Memory memory, Caller caller, int clockId, long precision, int resultPtr)
    {
        ulong value = clockId == 1
            ? (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency))
            : (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;
        WasmMemoryAccessor.WriteUInt64(memory, caller, resultPtr, value);
        return ErrnoSuccess;
    }

    public virtual int FdWrite(Memory memory, Caller caller, int fd, int iovsPtr, int iovsLen, int nwrittenPtr)
    {
        if (fd is not (1 or 2))
        {
            return ErrnoBadf;
        }

        var total = 0;
        var span = memory.GetSpan<byte>(0);
        for (var i = 0; i < iovsLen; i++)
        {
            var offset = iovsPtr + (i * 8);
            var ptr = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
            var len = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 4, 4));
            var text = Utf8.GetString(span.Slice(ptr, len));
            if (fd == 1)
            {
                Console.Out.Write(text);
            }
            else
            {
                Console.Error.Write(text);
            }

            total += len;
        }

        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(nwrittenPtr, 4), total);
        return ErrnoSuccess;
    }

    public virtual int FdClose(int fd) => ErrnoNosys;

    public virtual void ProcExit(int code)
    {
        // Swallow the exit so a QuickJS internal assertion (e.g.
        // gc_obj_list not empty during JS_FreeRuntime) does not
        // kill the host process.
    }

    public virtual int FdFdstatGet(Memory memory, Caller caller, int fd, int statPtr)
    {
        if (fd is not (1 or 2))
        {
            return ErrnoBadf;
        }

        var span = memory.GetSpan<byte>(0);
        span[statPtr] = 2;
        span[statPtr + 1] = 0;
        span[statPtr + 2] = 0;
        span[statPtr + 3] = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(statPtr + 8, 8), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(statPtr + 16, 8), 0);
        return ErrnoSuccess;
    }

    public virtual int FdSeek(int fd, long offset, int whence, int resultPtr) => ErrnoNosys;

    public virtual int RandomGet(Memory memory, Caller caller, int bufPtr, int bufLen)
    {
        var span = memory.GetSpan(bufPtr, bufLen);
        RandomNumberGenerator.Fill(span);
        return ErrnoSuccess;
    }

    public virtual void Define(Linker linker, Store store, Func<Memory?> memoryAccessor)
    {
        linker.Define("wasi_snapshot_preview1", "clock_time_get",
            Function.FromCallback(store, (Caller caller, int clockId, long precision, int resultPtr) =>
            {
                var memory = caller.GetMemory("memory") ?? memoryAccessor() ?? throw new InvalidOperationException("WASM memory is not available.");
                return ClockTimeGet(memory, caller, clockId, precision, resultPtr);
            }));

        linker.Define("wasi_snapshot_preview1", "fd_write",
            Function.FromCallback(store, (Caller caller, int fd, int iovsPtr, int iovsLen, int nwrittenPtr) =>
            {
                var memory = caller.GetMemory("memory") ?? memoryAccessor() ?? throw new InvalidOperationException("WASM memory is not available.");
                return FdWrite(memory, caller, fd, iovsPtr, iovsLen, nwrittenPtr);
            }));

        linker.Define("wasi_snapshot_preview1", "fd_close",
            Function.FromCallback(store, (int fd) => FdClose(fd)));

        linker.Define("wasi_snapshot_preview1", "fd_fdstat_get",
            Function.FromCallback(store, (Caller caller, int fd, int statPtr) =>
            {
                var memory = caller.GetMemory("memory") ?? memoryAccessor() ?? throw new InvalidOperationException("WASM memory is not available.");
                return FdFdstatGet(memory, caller, fd, statPtr);
            }));

        linker.Define("wasi_snapshot_preview1", "fd_seek",
            Function.FromCallback(store, (int fd, long offset, int whence, int resultPtr) => FdSeek(fd, offset, whence, resultPtr)));

        linker.Define("wasi_snapshot_preview1", "random_get",
            Function.FromCallback(store, (Caller caller, int bufPtr, int bufLen) =>
            {
                var memory = caller.GetMemory("memory") ?? memoryAccessor() ?? throw new InvalidOperationException("WASM memory is not available.");
                return RandomGet(memory, caller, bufPtr, bufLen);
            }));

        linker.Define("wasi_snapshot_preview1", "proc_exit",
            Function.FromCallback(store, (int code) => ProcExit(code)));
    }
}
