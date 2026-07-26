using QuickJsWasi;

namespace QuickJsWasi.Tests;

public abstract class TestBase
{
    protected static readonly byte[]? WasmBytes = LoadWasmBytes();

    protected static bool HasWasm => WasmBytes is { Length: > 0 };

    protected static async Task<QuickJs> CreateVmAsync(Action<QuickJsOptions>? configure = null)
    {
        var options = new QuickJsOptions { WasmBytes = WasmBytes };
        configure?.Invoke(options);
        return await QuickJs.CreateAsync(options);
    }

    private static byte[]? LoadWasmBytes()
    {
        var assembly = typeof(QuickJs).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith("quickjs.wasm", StringComparison.OrdinalIgnoreCase) || x.EndsWith("quickjs_wasm", StringComparison.OrdinalIgnoreCase));
        if (resource is not null)
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is not null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "quickjs.wasm"),
            Path.Combine(AppContext.BaseDirectory, "Resources", "quickjs.wasm"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../QuickJsWasi/Resources/quickjs.wasm")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../quickjs.wasm")),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return File.ReadAllBytes(candidate);
            }
        }

        return null;
    }
}
