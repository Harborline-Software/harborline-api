using System.Security.Cryptography;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Applies the node-wide Idempotency-Key contract to every mutating local-node route.
/// Successful responses are replayed byte-for-byte for the same session principal, tenant,
/// method, canonical path, query, key, and payload; the handler is never re-entered on a replay.
/// </summary>
internal static class NodeMutationIdempotency
{
    internal const string HeaderName = IdempotencyContract.HeaderName;
    private const string StoreProperty = "shipyard.local-node.idempotency.store";
    private const string BootstrapScope = "bootstrap";
    private static readonly string[] NeverCachePrefixes =
    {
        "/api/session",
        "/api/local-node/session",
        "/api/local-node/credentials",
        "/api/local-node/admission",
    };

    /// <summary>Installs the middleware once on a shared or standalone test application.</summary>
    internal static void UseOnce(IApplicationBuilder app, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (app.Properties.ContainsKey(StoreProperty))
        {
            return;
        }

        var store = new Store(timeProvider);
        app.Properties[StoreProperty] = store;
        app.ApplicationServices.GetService<IHostApplicationLifetime>()?.ApplicationStopping.Register(store.Dispose);
        app.Use(async (context, next) =>
        {
            if (!IsMutation(context.Request.Method) ||
                IsNeverCacheRoute(context.Request.Path) ||
                !context.Request.Path.StartsWithSegments("/api/local-node", StringComparison.OrdinalIgnoreCase) ||
                IsDurableFormSubmit(context.Request.Path))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            if (!TryReadKey(context.Request, out var key, out var error))
            {
                await WriteBadRequestAsync(context, error!).ConfigureAwait(false);
                return;
            }

            if (key is null)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var payload = await ReadRequestBodyAsync(context.Request).ConfigureAwait(false);
            var payloadFingerprint = Convert.ToHexString(SHA256.HashData(payload));
            var cacheKey = BuildCacheKey(context, key, ResolveBootstrapTenant(context));
            using var gateLease = store.AcquireGate(cacheKey);
            var enteredGate = false;
            try
            {
                await gateLease.Semaphore.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                enteredGate = true;
                switch (store.TryGet(cacheKey, payloadFingerprint, out var replay))
                {
                    case LookupResult.Replay:
                        await replay!.WriteAsync(context).ConfigureAwait(false);
                        return;
                    case LookupResult.Conflict:
                        await WriteConflictAsync(context).ConfigureAwait(false);
                        return;
                }

                var originalBody = context.Response.Body;
                await using var bufferedBody = new MemoryStream();
                context.Response.Body = bufferedBody;
                try
                {
                    await next(context).ConfigureAwait(false);
                    if (context.Response.StatusCode is >= 200 and < 300)
                    {
                        var response = ResponseCapture.From(context, bufferedBody);
                        store.Record(cacheKey, payloadFingerprint, response);
                    }

                    bufferedBody.Position = 0;
                    await bufferedBody.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
                }
                finally
                {
                    context.Response.Body = originalBody;
                }
            }
            finally
            {
                if (enteredGate)
                {
                    gateLease.Semaphore.Release();
                }
            }
        });
    }

    private static async Task<byte[]> ReadRequestBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();
        await using var copy = new MemoryStream();
        await request.Body.CopyToAsync(copy, request.HttpContext.RequestAborted).ConfigureAwait(false);
        request.Body.Position = 0;
        return copy.ToArray();
    }

    private static string BuildCacheKey(HttpContext context, string key, string bootstrapTenant)
    {
        var principal = context.Features.Get<SelectedSessionRequestPrincipal>();
        var principalScope = principal is null
            ? $"{BootstrapScope}:{bootstrapTenant}"
            : $"{principal.TenantId.Value}\n{principal.PrincipalUserId.Value}";
        var path = (context.Request.Path.Value ?? string.Empty).ToLowerInvariant();
        var query = string.Join(
            "&",
            context.Request.Query
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(pair => pair.Value.Select(value =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(value ?? string.Empty)}")));
        return $"{principalScope}\n{context.Request.Method}\n{path}\n{query}\n{key}";
    }

    private static string ResolveBootstrapTenant(HttpContext context)
    {
        // Bootstrap requests have no selected-session principal. Resolve their replay scope from
        // the same active-team tenant the mutation route uses; do not activate the selected-session
        // authorization context merely to compute a cache key.
        return NodeTenant.Resolve(
            context.RequestServices.GetRequiredService<IActiveTeamAccessor>()).Value;
    }

    private static bool IsNeverCacheRoute(PathString path) =>
        NeverCachePrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsDurableFormSubmit(PathString path)
    {
        if (!path.StartsWithSegments("/api/local-node/forms", out var remaining))
        {
            return false;
        }

        var segments = remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
        return segments.Length == 2 && string.Equals(segments[1], "submit", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMutation(string method) =>
        HttpMethods.IsPost(method) ||
        HttpMethods.IsPut(method) ||
        HttpMethods.IsPatch(method) ||
        HttpMethods.IsDelete(method);

    private static bool TryReadKey(HttpRequest request, out string? key, out string? error)
    {
        key = null;
        error = null;
        if (!request.Headers.TryGetValue(HeaderName, out var values))
        {
            return true;
        }

        if (values.Count != 1 || values.Any(value => value?.Contains(',', StringComparison.Ordinal) == true))
        {
            error = $"Only one {HeaderName} header is permitted.";
            return false;
        }

        var value = values.ToString().Trim();
        if (value.Length == 0)
        {
            error = $"{HeaderName} must not be whitespace-only.";
            return false;
        }

        if (value.Length > IdempotencyContract.MaxKeyLength)
        {
            error = $"{HeaderName} must be at most {IdempotencyContract.MaxKeyLength} characters.";
            return false;
        }

        key = value;
        return true;
    }

    private static async Task WriteBadRequestAsync(HttpContext context, string error)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(
                $"{{\"error\":{System.Text.Json.JsonSerializer.Serialize(error)}}}")
            .ConfigureAwait(false);
    }

    private static async Task WriteConflictAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(
                "{\"code\":\"authorization.idempotency_key_reused\"}")
            .ConfigureAwait(false);
    }

    internal enum LookupResult
    {
        Miss,
        Replay,
        Conflict,
    }

    internal sealed class Store : IDisposable
    {
        // This is deliberately an in-memory, non-durable replay store. It protects retries within
        // one node process only; durable mutation ownership remains with the route or data store.
        internal const int DefaultMaxEntries = 1_024;
        internal static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

        private readonly object _sync = new();
        private readonly Dictionary<string, StoredResponse> _responses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GateEntry> _gates = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _lru = new();
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _ttl;
        private readonly int _maxEntries;
        private bool _disposed;

        internal Store(
            TimeProvider? timeProvider = null,
            TimeSpan? ttl = null,
            int maxEntries = DefaultMaxEntries)
        {
            if (maxEntries <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEntries));
            }

            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
            _ttl = ttl ?? DefaultTtl;
            _maxEntries = maxEntries;
        }

        internal int ResponseCount
        {
            get
            {
                lock (_sync)
                {
                    EvictExpired(_timeProvider.GetUtcNow());
                    return _responses.Count;
                }
            }
        }

        internal GateLease AcquireGate(string cacheKey)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (!_gates.TryGetValue(cacheKey, out var entry))
                {
                    entry = new GateEntry();
                    _gates.Add(cacheKey, entry);
                }

                entry.ActiveUsers++;
                return new GateLease(this, cacheKey, entry);
            }
        }

        internal LookupResult TryGet(string cacheKey, string payloadFingerprint, out ResponseCapture? response)
        {
            lock (_sync)
            {
                var now = _timeProvider.GetUtcNow();
                EvictExpired(now);
                if (!_responses.TryGetValue(cacheKey, out var stored))
                {
                    response = null;
                    return LookupResult.Miss;
                }

                // EvictExpired intentionally stops at the first live LRU entry. A requested key
                // can therefore be expired behind a live entry and must be checked independently.
                if (stored.ExpiresAt <= now)
                {
                    RemoveResponse(cacheKey, stored);
                    response = null;
                    return LookupResult.Miss;
                }

                Touch(cacheKey, stored);
                response = stored.Response;
                return string.Equals(stored.PayloadFingerprint, payloadFingerprint, StringComparison.Ordinal)
                    ? LookupResult.Replay
                    : LookupResult.Conflict;
            }
        }

        internal void Record(string cacheKey, string payloadFingerprint, ResponseCapture response)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                var now = _timeProvider.GetUtcNow();
                EvictExpired(now);
                if (_responses.TryGetValue(cacheKey, out var existing))
                {
                    _lru.Remove(existing.LruNode);
                }

                var stored = new StoredResponse(payloadFingerprint, response, now + _ttl)
                {
                    LruNode = _lru.AddLast(cacheKey),
                };
                _responses[cacheKey] = stored;
                while (_responses.Count > _maxEntries)
                {
                    var oldest = _lru.First!;
                    _lru.RemoveFirst();
                    _responses.Remove(oldest.Value);
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                foreach (var entry in _gates.Values)
                {
                    entry.Semaphore.Dispose();
                }

                _gates.Clear();
                _responses.Clear();
                _lru.Clear();
            }
        }

        private void ReleaseGate(string cacheKey, GateEntry entry)
        {
            lock (_sync)
            {
                entry.ActiveUsers--;
                if (entry.ActiveUsers == 0 && _gates.Remove(cacheKey))
                {
                    entry.Semaphore.Dispose();
                }
            }
        }

        private void EvictExpired(DateTimeOffset now)
        {
            while (_lru.First is { } node)
            {
                if (_responses.TryGetValue(node.Value, out var stored) && stored.ExpiresAt > now)
                {
                    break;
                }

                _lru.RemoveFirst();
                _responses.Remove(node.Value);
            }
        }

        private void Touch(string cacheKey, StoredResponse stored)
        {
            _lru.Remove(stored.LruNode);
            stored.LruNode = _lru.AddLast(cacheKey);
        }

        private void RemoveResponse(string cacheKey, StoredResponse stored)
        {
            _lru.Remove(stored.LruNode);
            _responses.Remove(cacheKey);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        internal sealed class GateLease : IDisposable
        {
            private readonly Store _store;
            private readonly string _cacheKey;
            private readonly GateEntry _entry;
            private int _disposed;

            internal GateLease(Store store, string cacheKey, GateEntry entry)
            {
                _store = store;
                _cacheKey = cacheKey;
                _entry = entry;
            }

            internal SemaphoreSlim Semaphore => _entry.Semaphore;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _store.ReleaseGate(_cacheKey, _entry);
                }
            }
        }

        internal sealed class GateEntry
        {
            internal SemaphoreSlim Semaphore { get; } = new(1, 1);
            internal int ActiveUsers { get; set; }
        }

        private sealed class StoredResponse
        {
            internal StoredResponse(string payloadFingerprint, ResponseCapture response, DateTimeOffset expiresAt)
            {
                PayloadFingerprint = payloadFingerprint;
                Response = response;
                ExpiresAt = expiresAt;
            }

            internal string PayloadFingerprint { get; }
            internal ResponseCapture Response { get; }
            internal DateTimeOffset ExpiresAt { get; }
            internal LinkedListNode<string> LruNode { get; set; } = null!;
        }
    }

    internal sealed record ResponseCapture(
        int StatusCode,
        string? ContentType,
        IReadOnlyDictionary<string, string[]> Headers,
        byte[] Body)
    {
        internal static ResponseCapture From(HttpContext context, MemoryStream body)
        {
            var headers = context.Response.Headers
                .Where(pair => !string.Equals(pair.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Select(value => value ?? string.Empty).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            return new ResponseCapture(
                context.Response.StatusCode,
                context.Response.ContentType,
                headers,
                body.ToArray());
        }

        internal async Task WriteAsync(HttpContext context)
        {
            context.Response.StatusCode = StatusCode;
            context.Response.ContentType = ContentType;
            foreach (var pair in Headers)
            {
                context.Response.Headers[pair.Key] = pair.Value;
            }

            context.Response.ContentLength = Body.Length;
            await context.Response.Body.WriteAsync(Body, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
