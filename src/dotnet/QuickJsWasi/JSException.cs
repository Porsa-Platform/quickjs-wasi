namespace QuickJsWasi;

public sealed class JSException : Exception, IDisposable
{
    public JSException(JSValueHandle handle)
        : base(ReadMessage(handle, out var name, out var stack))
    {
        Handle = handle ?? throw new ArgumentNullException(nameof(handle));
        Name = name;
        JsStack = stack;
    }

    public JSValueHandle Handle { get; }

    public override string Message => base.Message;

    public override string? StackTrace => JsStack ?? base.StackTrace;

    public string Name { get; }

    public string? JsStack { get; }

    private static string ReadMessage(JSValueHandle handle, out string name, out string? stack)
    {
        using var nameHandle = handle.GetProp("name");
        using var messageHandle = handle.GetProp("message");
        using var stackHandle = handle.GetProp("stack");
        name = nameHandle.IsUndefined ? "Error" : nameHandle.ToManagedString();
        stack = stackHandle.IsUndefined ? null : stackHandle.ToManagedString();
        return messageHandle.IsUndefined ? handle.ToManagedString() : messageHandle.ToManagedString();
    }

    public void Dispose() => Handle.Dispose();
}
