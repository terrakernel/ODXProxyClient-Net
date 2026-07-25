using Microsoft.Win32.SafeHandles;

namespace Odx.Client.Interop;

/// <summary>
/// Owns a native <c>OdxClient*</c>. Guarantees the connection pool is released via
/// <c>odx_client_free</c> even if the managed wrapper is not disposed. The RPC entry
/// points only borrow the client for the duration of the (synchronous) submit call —
/// the spawned Rust task captures clones, not the handle — so callers wrap each submit
/// in <see cref="System.Runtime.InteropServices.SafeHandle.DangerousAddRef(ref bool)"/>
/// / <c>DangerousRelease</c> to keep it alive across that window.
/// </summary>
/// <remarks>
/// <see cref="System.Runtime.InteropServices.LibraryImportAttribute"/> does not support
/// SafeHandle marshalling (SYSLIB1051), so the interop passes raw <see cref="nint"/> and
/// this wrapper is built explicitly via <see cref="FromRaw"/>.
/// </remarks>
internal sealed class OdxClientHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public OdxClientHandle()
        : base(ownsHandle: true)
    {
    }

    /// <summary>Wrap a raw pointer returned by <c>odx_client_create</c>.</summary>
    internal static OdxClientHandle FromRaw(nint raw)
    {
        var handle = new OdxClientHandle();
        handle.SetHandle(raw);
        return handle;
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.odx_client_free(handle);
        return true;
    }
}
