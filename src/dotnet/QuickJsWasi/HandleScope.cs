namespace QuickJsWasi;

/// <summary>
/// Represents an active handle scope created by <see cref="QuickJs.WithScope{T}"/>.
/// Handles created inside the scope are automatically disposed when the scope ends,
/// unless explicitly transferred out with <see cref="Escape"/>.
/// </summary>
public interface IHandleScope
{
    /// <summary>
    /// Remove <paramref name="handle"/> from this scope so that it outlives it.
    /// When there is an enclosing scope the handle is transferred to it;
    /// otherwise it becomes the caller's responsibility to dispose.
    /// </summary>
    /// <param name="handle">The handle to escape from the current scope.</param>
    /// <returns>The same handle, for convenient inline use.</returns>
    JSValueHandle Escape(JSValueHandle handle);
}
