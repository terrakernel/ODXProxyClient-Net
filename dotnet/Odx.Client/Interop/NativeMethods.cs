using System.Runtime.InteropServices;

// The FFI completion callback (mirrors the Rust `OdxCallback`). Cdecl == the single
// x64 Windows C calling convention that Rust `extern "C"` emits.
using unsafe OdxCallbackFn = delegate* unmanaged[Cdecl]<
    nint,                        // user_data
    TerraKernel.OdxClient.Interop.OdxStatus,
    ushort,                      // http_status
    byte*,                       // data_ptr
    nuint,                       // data_len
    nint,                        // owner (OdxBuffer*)
    void>;

namespace TerraKernel.OdxClient.Interop;

/// <summary>
/// Thin P/Invoke surface over <c>odxclient.dll</c>. Source-generated
/// (<see cref="LibraryImportAttribute"/>) so it is reflection-free and AOT-friendly
/// (spec constraint #7). No logic lives here. Opaque handles cross as <see cref="nint"/>;
/// the client handle is a <see cref="OdxClientHandle"/> SafeHandle.
/// </summary>
internal static unsafe partial class NativeMethods
{
    private const string Lib = "odxclient";

    // ---- runtime ----

    [LibraryImport(Lib)]
    internal static partial OdxStatus odx_runtime_init(uint workerThreads);

    // ---- client lifecycle ----

    [LibraryImport(Lib)]
    internal static partial OdxStatus odx_client_create(in OdxClientConfig cfg, out nint client);

    [LibraryImport(Lib)]
    internal static partial void odx_client_free(nint client);

    // ---- calls ----

    [LibraryImport(Lib)]
    internal static partial OdxStatus odx_execute(
        nint client,
        byte* bodyPtr,
        nuint bodyLen,
        uint timeoutSecs,
        OdxCallbackFn callback,
        nint userData,
        out nint outRequest);

    [LibraryImport(Lib)]
    internal static partial OdxStatus odx_get_version(
        nint client,
        byte* bodyPtr,
        nuint bodyLen,
        uint timeoutSecs,
        OdxCallbackFn callback,
        nint userData,
        out nint outRequest);

    [LibraryImport(Lib)]
    internal static partial OdxStatus odx_get_license(
        nint client,
        OdxCallbackFn callback,
        nint userData,
        out nint outRequest);

    [LibraryImport(Lib)]
    internal static partial OdxStatus odx_get_about(
        nint client,
        OdxCallbackFn callback,
        nint userData,
        out nint outRequest);

    [LibraryImport(Lib)]
    internal static partial OdxStatus odx_get_metrics(
        nint client,
        OdxCallbackFn callback,
        nint userData,
        out nint outRequest);

    // ---- cancellation & cleanup ----

    [LibraryImport(Lib)]
    internal static partial void odx_cancel(nint request);

    [LibraryImport(Lib)]
    internal static partial void odx_request_free(nint request);

    [LibraryImport(Lib)]
    internal static partial void odx_buffer_free(nint owner);

    // ---- diagnostics ----

    [LibraryImport(Lib)]
    internal static partial nuint odx_last_error(byte* buf, nuint cap);
}
