using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Session;
using Harborline.Api.LocalNodeHost.Data.Audit;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>W4's positive LAN route policy. This is separate from the loopback caller policy.</summary>
internal static class LocalNodeLanRoutePolicy
{
    internal const string PairingRedeemPath = "/api/local-node/admission/redeem";

    private static readonly string[] DataRouteRoots =
    [
        "/api/local-node/status", "/api/local-node/sync-status", "/api/local-node/teams",
        "/api/local-node/contacts", "/api/local-node/entities", "/api/local-node/documents",
        "/api/local-node/calendar", "/api/local-node/scheduling/definitions",
        "/api/local-node/scheduling/subjects", "/api/local-node/scheduling/appointments",
        "/api/local-node/scheduling/events", "/api/local-node/scheduling/resources/availability",
        "/api/local-node/org-branding", "/api/local-node/asset-registry",
        "/api/local-node/audit-events", "/api/local-node/accounting",
        "/api/local-node/accounting-periods", "/api/local-node/bank-accounts",
        "/api/local-node/bills", "/api/local-node/chart-of-accounts",
        "/api/local-node/invoices", "/api/local-node/journal-entries",
        "/api/local-node/leases", "/api/local-node/payments", "/api/local-node/payroll",
        "/api/local-node/properties", "/api/local-node/recurring-invoices",
        "/api/local-node/approval-tasks", "/api/local-node/kg-action-tasks", "/api/local-node/kg",
        "/api/local-node/reports", "/api/local-node/charts", "/api/local-node/workflows/definitions",
        "/api/local-node/workflow-confirmations", "/api/local-node/workflow-run-report",
        "/api/local-node/navigation/workspaces", "/api/local-node/packs/installed",
        "/api/local-node/packs/graph",
    ];

    internal static IReadOnlyList<string> AllowlistedRoots => DataRouteRoots;

    internal static bool IsDataRoute(string path)
    {
        var normalized = Normalize(path);
        return DataRouteRoots.Any(root =>
            normalized.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsPairingRedeem(HttpRequest request) =>
        request.Method.Equals(HttpMethods.Post, StringComparison.OrdinalIgnoreCase) &&
        (request.Path.Value ?? string.Empty)
            .Equals(PairingRedeemPath, StringComparison.OrdinalIgnoreCase);

    internal static string Normalize(string path) =>
        path.Length > 1 && path.EndsWith('/', StringComparison.Ordinal) ? path[..^1] : path;
}

/// <summary>Exact W4 startup validation. No LAN configuration is silently repaired or downgraded.</summary>
internal static class LocalNodeLanValidation
{
    internal static X509Certificate2 ValidateAndResolve(
        LocalNodeLanOptions options,
        bool listenerCallerAuthEnforced,
        TimeProvider timeProvider,
        IEnumerable<IPAddress>? localAddresses = null,
        string? aspNetCoreUrls = null,
        string? dataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            throw new InvalidOperationException("LAN validation requires LocalNode:Lan:Enabled=true.");
        }
        if (!IPAddress.TryParse(options.BindAddress, out IPAddress? bindAddress) || bindAddress is null ||
            IPAddress.IsLoopback(bindAddress) || bindAddress.Equals(IPAddress.Any) ||
            bindAddress.Equals(IPAddress.IPv6Any) || bindAddress.Equals(IPAddress.None))
        {
            throw new InvalidOperationException(
                "LocalNode:Lan:BindAddress must be a concrete locally assigned non-loopback IP address.");
        }
        if (options.Port != 7443)
        {
            throw new InvalidOperationException("LocalNode:Lan:Port is fixed at 7443.");
        }
        if (string.IsNullOrWhiteSpace(options.AdvertisedHost) ||
            !IsAdvertisedHostValid(options.AdvertisedHost!, bindAddress))
        {
            throw new InvalidOperationException(
                "LocalNode:Lan:AdvertisedHost must be the exact client DNS name or textual bind address.");
        }

        var assignedAddresses = localAddresses ?? GetAssignedAddresses();
        if (!assignedAddresses.Any(address => address.Equals(bindAddress)))
        {
            throw new InvalidOperationException(
                "LocalNode:Lan:BindAddress is not assigned to a local network interface.");
        }
        if (!listenerCallerAuthEnforced)
        {
            throw new InvalidOperationException(
                "LocalNode:Lan requires ListenerCallerAuthEnforced=true before binding.");
        }

        ValidateEnvironmentUrls(aspNetCoreUrls ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
        var certificate = ResolveCertificate(options, dataDirectory);
        try
        {
        ValidateCertificate(certificate, options.AdvertisedHost!, bindAddress, timeProvider.GetUtcNow());
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
        return certificate;
    }

    internal static void ValidateEnvironmentUrls(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls))
        {
            return;
        }
        foreach (var value in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address))
            {
                throw new InvalidOperationException(
                    "ASPNETCORE_URLS may contain only loopback HTTP URLs when the W4 LAN listener is enabled.");
            }
        }
    }

    private static X509Certificate2 ResolveCertificate(
        LocalNodeLanOptions options,
        string? dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(options.Certificate))
        {
            throw new InvalidOperationException(
                "LocalNode:Lan:Certificate is required when the LAN listener is enabled.");
        }

        if (options.Certificate.StartsWith("store://", StringComparison.OrdinalIgnoreCase))
        {
            var parts = options.Certificate[8..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !Enum.TryParse<StoreLocation>(parts[0], true, out var location) ||
                !Enum.TryParse<StoreName>(parts[1], true, out var name))
            {
                throw new InvalidOperationException("The LAN certificate store identity is invalid.");
            }
            using var store = new X509Store(name, location);
            store.Open(OpenFlags.ReadOnly);
            var certificate = store.Certificates
                .Find(X509FindType.FindByThumbprint, parts[2].Replace(" ", string.Empty), validOnly: false)
                .OfType<X509Certificate2>()
                .SingleOrDefault();
            return certificate ?? throw new InvalidOperationException(
                "The configured LAN certificate was not found in the platform certificate store.");
        }

        var certificatePath = Path.IsPathRooted(options.Certificate)
            ? options.Certificate
            : Path.Combine(dataDirectory ?? LocalNodeOptions.GetDefaultDataDirectory(), options.Certificate);
        if (!File.Exists(certificatePath))
        {
            throw new InvalidOperationException("The configured LAN certificate file does not exist.");
        }
        return X509CertificateLoader.LoadPkcs12FromFile(
            certificatePath,
            options.CertificatePassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    private static void ValidateCertificate(
        X509Certificate2 certificate,
        string advertisedHost,
        IPAddress bindAddress,
        DateTimeOffset at)
    {
        if (!certificate.HasPrivateKey || at < certificate.NotBefore || at > certificate.NotAfter)
        {
            throw new InvalidOperationException("The LAN certificate must be valid and include its private key.");
        }

        var serverAuth = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>())
            .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1");
        if (!serverAuth)
        {
            throw new InvalidOperationException("The LAN certificate must contain the TLS server-auth EKU.");
        }

        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault();
        var dnsNames = san?.EnumerateDnsNames().ToArray() ?? [];
        var ipNames = san?.EnumerateIPAddresses().ToArray() ?? [];
        var advertisedMatches = IPAddress.TryParse(advertisedHost, out var advertisedAddress)
            ? ipNames.Any(address => address.Equals(advertisedAddress))
            : dnsNames.Any(name => name.Equals(advertisedHost, StringComparison.OrdinalIgnoreCase));
        if (!advertisedMatches || !ipNames.Any(address => address.Equals(bindAddress)))
        {
            throw new InvalidOperationException(
                "The LAN certificate SAN must contain both AdvertisedHost and BindAddress.");
        }
    }

    private static bool IsAdvertisedHostValid(string value, IPAddress bindAddress)
    {
        if (value.Equals(bindAddress.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (value.Contains('/') || value.Contains(':') || value.Contains(' ') ||
            value.Length > 253 || !Uri.CheckHostName(value).Equals(UriHostNameType.Dns))
        {
            return false;
        }
        return value.Split('.').All(label => label.Length > 0 && label.Length <= 63 &&
            label[0] != '-' && label[^1] != '-' && label.All(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_'));
    }

    private static IReadOnlyCollection<IPAddress> GetAssignedAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .ToArray();
}

/// <summary>Opaque device-session result bound to the request's device principal.</summary>
internal sealed record LanDevicePrincipal(string DeviceId, string TenantId, string PrincipalId);

/// <summary>
/// Session seam for the transport. Pairing/session issuance remains in its existing substrate; a LAN request
/// is admitted only when that substrate can resolve a device principal from the bearer.
/// </summary>
internal interface ILanDeviceSessionAuthority
{
    ValueTask<LanDevicePrincipal?> AuthenticateAsync(HttpContext context, string bearer, CancellationToken cancellationToken);
}

/// <summary>Fail-closed default until the pairing/session composition supplies the device authority.</summary>
internal sealed class MissingLanDeviceSessionAuthority : ILanDeviceSessionAuthority
{
    public ValueTask<LanDevicePrincipal?> AuthenticateAsync(
        HttpContext context, string bearer, CancellationToken cancellationToken) =>
        ValueTask.FromResult<LanDevicePrincipal?>(null);
}

/// <summary>W4 source-IP and concurrent handshake limiter for non-loopback listener traffic.</summary>
internal sealed class LanConnectionRateLimiter
{
    internal const int MaxAttemptsPerSourcePerMinute = 30;
    internal const int MaxConcurrentHandshakes = 10;

    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SourceState> _sources = new(StringComparer.Ordinal);

    internal LanConnectionRateLimiter(TimeProvider timeProvider) => _timeProvider = timeProvider;

    internal bool TryAcquire(string source, out IDisposable lease)
    {
        var now = _timeProvider.GetUtcNow();
        var state = _sources.GetOrAdd(source, static _ => new SourceState());
        lock (state.Gate)
        {
            while (state.Attempts.First is { Value: var attempt } &&
                   now - attempt.StartedAt >= TimeSpan.FromMinutes(1))
            {
                state.Attempts.RemoveFirst();
            }

            if (state.Attempts.Count >= MaxAttemptsPerSourcePerMinute)
            {
                lease = NullLease.Instance;
                return false;
            }

            var concurrent = Interlocked.Increment(ref state.Concurrent);
            if (concurrent > MaxConcurrentHandshakes)
            {
                Interlocked.Decrement(ref state.Concurrent);
                lease = NullLease.Instance;
                return false;
            }

            var attemptNode = state.Attempts.AddLast(new Attempt(now));
            lease = new ReleaseLease(this, state, attemptNode);
        }
        return true;
    }

    private void Release(SourceState state, LinkedListNode<Attempt> attemptNode, bool authenticated)
    {
        if (authenticated)
        {
            lock (state.Gate)
            {
                state.Attempts.Remove(attemptNode);
            }
        }

        Interlocked.Decrement(ref state.Concurrent);
    }

    private sealed class SourceState
    {
        internal object Gate { get; } = new();
        internal LinkedList<Attempt> Attempts { get; } = new();
        internal int Concurrent;
    }

    private sealed record Attempt(DateTimeOffset StartedAt);

    private sealed class ReleaseLease(
        LanConnectionRateLimiter owner,
        SourceState state,
        LinkedListNode<Attempt> attemptNode) : IDisposable
    {
        private int _released;
        private int _authenticated;

        internal void MarkAuthenticated()
        {
            if (Interlocked.Exchange(ref _authenticated, 1) == 0 &&
                Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(state, attemptNode, authenticated: true);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(state, attemptNode, Volatile.Read(ref _authenticated) != 0);
            }
        }
    }

    internal static void MarkAuthenticated(IDisposable lease)
    {
        if (lease is ReleaseLease typedLease)
        {
            typedLease.MarkAuthenticated();
        }
    }

    private sealed class NullLease : IDisposable
    {
        internal static NullLease Instance { get; } = new();
        public void Dispose() { }
    }
}

internal sealed class LanListenerRequestFeature
{
    internal static LanListenerRequestFeature Instance { get; } = new();
    private LanListenerRequestFeature() { }
}

internal static class LanRouteUnavailable
{
    internal static Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new { error = "lan.route.unavailable" });
    }
}
