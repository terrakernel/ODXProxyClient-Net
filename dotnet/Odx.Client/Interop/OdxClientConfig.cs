using System.Runtime.InteropServices;

namespace TerraKernel.OdxClient.Interop;

/// <summary>
/// Blittable mirror of the Rust <c>OdxClientConfig</c> (repr(C), <c>src/client.rs</c>).
/// The pointer/len pairs are borrowed only for the duration of the create call.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct OdxClientConfig
{
    public byte* BaseUrlPtr;
    public nuint BaseUrlLen;
    public byte* ApiKeyPtr;
    public nuint ApiKeyLen;
    public uint DefaultTimeoutSecs;
    public uint ConnectTimeoutMs;
    public uint PoolMaxIdlePerHost;
}
