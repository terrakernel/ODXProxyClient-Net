using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using Odx.Client.Interop;

namespace Odx.Client;

/// <summary>
/// Managed client over the odxclient Rust core. All network + response-buffer work
/// happens off the caller's thread; the awaiting continuation runs on the thread
/// pool (never a captured UI <see cref="SynchronizationContext"/>), so consumers
/// never need <c>Task.Run</c> and cannot accidentally drag work onto the UI thread.
/// There is deliberately no synchronous/blocking API. See IMPLEMENTATION-PLAN.md §7.
/// </summary>
public sealed class OdxClient : IDisposable
{
    private readonly OdxClientHandle _handle;

    private OdxClient(OdxClientHandle handle) => _handle = handle;

    /// <summary>Create a client bound to an odxproxy base URL + API key.</summary>
    public static OdxClient Create(
        string baseUrl,
        string apiKey,
        uint defaultTimeoutSecs = 15,
        uint connectTimeoutMs = 0,
        uint poolMaxIdlePerHost = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(apiKey);

        byte[] baseUrlBytes = Encoding.UTF8.GetBytes(baseUrl);
        byte[] apiKeyBytes = Encoding.UTF8.GetBytes(apiKey);

        OdxStatus status;
        nint raw = 0;
        unsafe
        {
            fixed (byte* bp = baseUrlBytes)
            fixed (byte* ap = apiKeyBytes)
            {
                var cfg = new OdxClientConfig
                {
                    BaseUrlPtr = bp,
                    BaseUrlLen = (nuint)baseUrlBytes.Length,
                    ApiKeyPtr = ap,
                    ApiKeyLen = (nuint)apiKeyBytes.Length,
                    DefaultTimeoutSecs = defaultTimeoutSecs,
                    ConnectTimeoutMs = connectTimeoutMs,
                    PoolMaxIdlePerHost = poolMaxIdlePerHost,
                };
                status = NativeMethods.odx_client_create(in cfg, out raw);
            }
        }

        if (status != OdxStatus.Ok || raw == 0)
            throw new OdxException(status, GetLastError());

        return new OdxClient(OdxClientHandle.FromRaw(raw));
    }

    // ---- endpoints (raw response; the typed ...Async<T> layer wraps these) ----

    /// <summary>POST <c>/api/odoo/execute</c> with a full JSON request body.</summary>
    public Task<OdxResponse> ExecuteAsync(ReadOnlyMemory<byte> body, uint timeoutSecs = 0, CancellationToken cancellationToken = default)
        => SubmitWithBody(body, timeoutSecs, cancellationToken, Endpoint.Execute);

    /// <summary>POST <c>/api/odoo/version</c> with body <c>{id, url}</c>.</summary>
    public Task<OdxResponse> GetVersionAsync(ReadOnlyMemory<byte> body, uint timeoutSecs = 0, CancellationToken cancellationToken = default)
        => SubmitWithBody(body, timeoutSecs, cancellationToken, Endpoint.Version);

    /// <summary>GET <c>/_/license</c> (flat body, not a JSON-RPC envelope).</summary>
    public Task<OdxResponse> GetLicenseAsync(CancellationToken cancellationToken = default)
        => SubmitGet(cancellationToken, Endpoint.License);

    /// <summary>GET <c>/_/about</c>.</summary>
    public Task<OdxResponse> GetAboutAsync(CancellationToken cancellationToken = default)
        => SubmitGet(cancellationToken, Endpoint.About);

    /// <summary>GET <c>/_/metrics</c> (Prometheus text).</summary>
    public Task<OdxResponse> GetMetricsAsync(CancellationToken cancellationToken = default)
        => SubmitGet(cancellationToken, Endpoint.Metrics);

    // ---- typed endpoints (deserialize off-thread; throw typed OdxExceptions) ----
    // Require a JsonTypeInfo<T> so the whole path stays reflection-free / AOT-safe
    // (constraint #7). Callers supply it from their own JsonSerializerContext.

    /// <summary>
    /// POST <c>/api/odoo/execute</c> and deserialize the envelope's <c>result</c> into
    /// <typeparamref name="T"/>. Throws a typed <see cref="OdxException"/> on a proxy or
    /// Odoo error (including the HTTP-200-with-error case).
    /// </summary>
    public async Task<T?> ExecuteAsync<T>(ReadOnlyMemory<byte> body, JsonTypeInfo<T> resultType, uint timeoutSecs = 0, CancellationToken cancellationToken = default)
    {
        OdxResponse resp = await SubmitWithBody(body, timeoutSecs, cancellationToken, Endpoint.Execute).ConfigureAwait(false);
        return ParseEnvelope(resp, resultType);
    }

    /// <summary>POST <c>/api/odoo/version</c> and deserialize the envelope's <c>result</c>.</summary>
    public async Task<T?> GetVersionAsync<T>(ReadOnlyMemory<byte> body, JsonTypeInfo<T> resultType, uint timeoutSecs = 0, CancellationToken cancellationToken = default)
    {
        OdxResponse resp = await SubmitWithBody(body, timeoutSecs, cancellationToken, Endpoint.Version).ConfigureAwait(false);
        return ParseEnvelope(resp, resultType);
    }

    /// <summary>GET <c>/_/license</c> and deserialize the flat body into <typeparamref name="T"/>.</summary>
    public async Task<T?> GetLicenseAsync<T>(JsonTypeInfo<T> resultType, CancellationToken cancellationToken = default)
    {
        OdxResponse resp = await SubmitGet(cancellationToken, Endpoint.License).ConfigureAwait(false);
        return Envelope.ReadFlat(resp.Body, resp.Status, resp.HttpStatus, resultType);
    }

    /// <summary>GET <c>/_/about</c> and deserialize the flat body into <typeparamref name="T"/>.</summary>
    public async Task<T?> GetAboutAsync<T>(JsonTypeInfo<T> resultType, CancellationToken cancellationToken = default)
    {
        OdxResponse resp = await SubmitGet(cancellationToken, Endpoint.About).ConfigureAwait(false);
        return Envelope.ReadFlat(resp.Body, resp.Status, resp.HttpStatus, resultType);
    }

    private static T? ParseEnvelope<T>(OdxResponse resp, JsonTypeInfo<T> resultType)
    {
        // Transport-level failures have no body. (Cancellation already threw
        // OperationCanceledException from the raw await above.)
        if (resp.Body.Length == 0)
            throw Envelope.MapError(resp.Status, resp.HttpStatus, null, null, null);
        return Envelope.ReadResult(resp.Body, resp.Status, resp.HttpStatus, resultType);
    }

    // ---- structured endpoints (assemble the request envelope + serialize off-UI) ----
    // The request body is built with OdxRequestBuilder; when called on a captured
    // SynchronizationContext (e.g. a UI thread) the build runs on the thread pool so a
    // large batch body never serializes on the UI thread — but a small request pays no
    // hop (IMPLEMENTATION-PLAN.md §7.2, memory `ffi-async-model`).
    // `paramsJson`/`keywordJson` must stay valid until the returned task completes.

    /// <summary>
    /// POST <c>/api/odoo/execute</c>, assembling the request envelope from primitives.
    /// The action is an <see cref="OdxAction"/> — validated at compile time, so a typo can't
    /// reach the proxy as a <c>-32001</c>. <paramref name="paramsJson"/> is a raw JSON array;
    /// <paramref name="keywordJson"/> a raw JSON object (both spliced in verbatim).
    /// <paramref name="fnName"/> is required for <see cref="OdxAction.CallMethod"/>.
    /// Deserializes <c>result</c> into <typeparamref name="T"/>.
    /// </summary>
    public Task<T?> ExecuteAsync<T>(
        OdxAction action,
        string modelId,
        OdooInstance instance,
        JsonTypeInfo<T> resultType,
        ReadOnlyMemory<byte> paramsJson = default,
        ReadOnlyMemory<byte> keywordJson = default,
        string? fnName = null,
        uint timeoutSecs = 0,
        CancellationToken cancellationToken = default)
        => SynchronizationContext.Current is null
            ? ExecuteBuilt(action, modelId, instance, resultType, paramsJson, keywordJson, fnName, timeoutSecs, cancellationToken)
            : Task.Run(() => ExecuteBuilt(action, modelId, instance, resultType, paramsJson, keywordJson, fnName, timeoutSecs, cancellationToken), cancellationToken);

    /// <summary>
    /// Raw-action-string overload of the structured <c>execute</c> call — the escape hatch
    /// for actions not covered by <see cref="OdxAction"/>. Prefer the <see cref="OdxAction"/>
    /// overload. <paramref name="paramsJson"/> is a raw JSON array; <paramref name="keywordJson"/>
    /// a raw JSON object (both spliced in verbatim). Deserializes <c>result</c> into
    /// <typeparamref name="T"/>.
    /// </summary>
    public Task<T?> ExecuteAsync<T>(
        string action,
        string modelId,
        OdooInstance instance,
        JsonTypeInfo<T> resultType,
        ReadOnlyMemory<byte> paramsJson = default,
        ReadOnlyMemory<byte> keywordJson = default,
        string? fnName = null,
        uint timeoutSecs = 0,
        CancellationToken cancellationToken = default)
        => SynchronizationContext.Current is null
            ? ExecuteBuilt(action, modelId, instance, resultType, paramsJson, keywordJson, fnName, timeoutSecs, cancellationToken)
            : Task.Run(() => ExecuteBuilt(action, modelId, instance, resultType, paramsJson, keywordJson, fnName, timeoutSecs, cancellationToken), cancellationToken);

    /// <summary>POST <c>/api/odoo/version</c> for an Odoo URL; deserialize the result.</summary>
    public Task<T?> GetVersionAsync<T>(string odooUrl, JsonTypeInfo<T> resultType, uint timeoutSecs = 0, CancellationToken cancellationToken = default)
        => SynchronizationContext.Current is null
            ? GetVersionBuilt(odooUrl, resultType, timeoutSecs, cancellationToken)
            : Task.Run(() => GetVersionBuilt(odooUrl, resultType, timeoutSecs, cancellationToken), cancellationToken);

    private Task<T?> ExecuteBuilt<T>(OdxAction action, string modelId, OdooInstance instance, JsonTypeInfo<T> resultType,
        ReadOnlyMemory<byte> paramsJson, ReadOnlyMemory<byte> keywordJson, string? fnName, uint timeoutSecs, CancellationToken ct)
    {
        byte[] body = OdxRequestBuilder.BuildExecute(action, modelId, instance, paramsJson.Span, keywordJson.Span, fnName);
        return ExecuteAsync(body.AsMemory(), resultType, timeoutSecs, ct);
    }

    private Task<T?> ExecuteBuilt<T>(string action, string modelId, OdooInstance instance, JsonTypeInfo<T> resultType,
        ReadOnlyMemory<byte> paramsJson, ReadOnlyMemory<byte> keywordJson, string? fnName, uint timeoutSecs, CancellationToken ct)
    {
        byte[] body = OdxRequestBuilder.BuildExecute(action, modelId, instance, paramsJson.Span, keywordJson.Span, fnName);
        return ExecuteAsync(body.AsMemory(), resultType, timeoutSecs, ct);
    }

    private Task<T?> GetVersionBuilt<T>(string odooUrl, JsonTypeInfo<T> resultType, uint timeoutSecs, CancellationToken ct)
    {
        byte[] body = OdxRequestBuilder.BuildVersion(odooUrl);
        return GetVersionAsync(body.AsMemory(), resultType, timeoutSecs, ct);
    }

    public void Dispose() => _handle.Dispose();

    // ---- submit plumbing ----

    private enum Endpoint { Execute, Version, License, About, Metrics }

    private unsafe Task<OdxResponse> SubmitWithBody(ReadOnlyMemory<byte> body, uint timeoutSecs, CancellationToken ct, Endpoint endpoint)
    {
        var pc = new PendingCall();
        pc.Self = GCHandle.Alloc(pc);
        nint userData = GCHandle.ToIntPtr(pc.Self);

        OdxStatus status;
        nint req;
        bool addRef = false;
        try
        {
            _handle.DangerousAddRef(ref addRef);
            nint client = _handle.DangerousGetHandle();
            fixed (byte* p = body.Span)
            {
                status = endpoint == Endpoint.Version
                    ? NativeMethods.odx_get_version(client, p, (nuint)body.Length, timeoutSecs, &OnComplete, userData, out req)
                    : NativeMethods.odx_execute(client, p, (nuint)body.Length, timeoutSecs, &OnComplete, userData, out req);
            }
        }
        finally
        {
            if (addRef) _handle.DangerousRelease();
        }

        return Complete(pc, status, req, ct);
    }

    private unsafe Task<OdxResponse> SubmitGet(CancellationToken ct, Endpoint endpoint)
    {
        var pc = new PendingCall();
        pc.Self = GCHandle.Alloc(pc);
        nint userData = GCHandle.ToIntPtr(pc.Self);

        OdxStatus status;
        nint req;
        bool addRef = false;
        try
        {
            _handle.DangerousAddRef(ref addRef);
            nint client = _handle.DangerousGetHandle();
            status = endpoint switch
            {
                Endpoint.License => NativeMethods.odx_get_license(client, &OnComplete, userData, out req),
                Endpoint.About => NativeMethods.odx_get_about(client, &OnComplete, userData, out req),
                _ => NativeMethods.odx_get_metrics(client, &OnComplete, userData, out req),
            };
        }
        finally
        {
            if (addRef) _handle.DangerousRelease();
        }

        return Complete(pc, status, req, ct);
    }

    private static Task<OdxResponse> Complete(PendingCall pc, OdxStatus status, nint req, CancellationToken ct)
    {
        if (status != OdxStatus.Ok)
        {
            // Submit failed synchronously: no task was spawned, no callback will fire.
            pc.Self.Free();
            return Task.FromException<OdxResponse>(new OdxException(status, GetLastError()));
        }

        pc.Req = req;
        if (ct.CanBeCanceled)
            pc.CtReg = ct.Register(static s => NativeMethods.odx_cancel(((PendingCall)s!).Req), pc);

        return AwaitResult(pc, req, ct);
    }

    private static async Task<OdxResponse> AwaitResult(PendingCall pc, nint req, CancellationToken ct)
    {
        // ConfigureAwait(false): the continuation (copy + buffer free) runs on the
        // thread pool, never the caller's captured SynchronizationContext.
        NativeResult r = await pc.Tcs.Task.ConfigureAwait(false);

        pc.CtReg.Dispose(); // no further odx_cancel can race the free below
        NativeMethods.odx_request_free(req);

        try
        {
            if (r.Status == OdxStatus.Cancelled)
                throw new OperationCanceledException(ct.IsCancellationRequested ? ct : CancellationToken.None);

            byte[] bytes = Array.Empty<byte>();
            if (r.Data != 0 && r.Len > 0)
            {
                unsafe
                {
                    bytes = new ReadOnlySpan<byte>((void*)r.Data, checked((int)r.Len)).ToArray();
                }
            }
            return new OdxResponse(r.Status, r.Http, bytes);
        }
        finally
        {
            if (r.Owner != 0)
                NativeMethods.odx_buffer_free(r.Owner);
        }
    }

    // The FFI completion callback. Trivial + non-throwing: stash the (still-unmanaged)
    // result and complete the TCS. The heavy work (copy) + freeing the buffer happen
    // in the continuation, off this tokio worker thread (IMPLEMENTATION-PLAN.md §7.2).
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnComplete(nint userData, OdxStatus status, ushort httpStatus, byte* data, nuint len, nint owner)
    {
        try
        {
            var gch = GCHandle.FromIntPtr(userData);
            var pc = gch.Target as PendingCall;
            gch.Free();
            pc?.Tcs.TrySetResult(new NativeResult(status, httpStatus, (nint)data, len, owner));
        }
        catch
        {
            // Never let an exception unwind into native code.
        }
    }

    private static unsafe string GetLastError()
    {
        nuint len = NativeMethods.odx_last_error(null, 0);
        if (len == 0)
            return string.Empty;
        var buf = new byte[(int)len];
        fixed (byte* p = buf)
        {
            NativeMethods.odx_last_error(p, (nuint)buf.Length);
        }
        return Encoding.UTF8.GetString(buf);
    }

    private sealed class PendingCall
    {
        public readonly TaskCompletionSource<NativeResult> Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GCHandle Self;
        public nint Req;
        public CancellationTokenRegistration CtReg;
    }

    private readonly struct NativeResult(OdxStatus status, ushort http, nint data, nuint len, nint owner)
    {
        public readonly OdxStatus Status = status;
        public readonly ushort Http = http;
        public readonly nint Data = data;
        public readonly nuint Len = len;
        public readonly nint Owner = owner;
    }
}
