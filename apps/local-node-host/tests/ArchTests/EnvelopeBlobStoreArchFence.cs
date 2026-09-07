using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Recovery.Blobs;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.Kernel.Buckets.Storage.Durability;
using Harborline.Api.LocalNodeHost.Data.KeyDistribution;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Data.Storage;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// <b>EnvelopeBlobStoreArchFence</b> — the PERS-1 no-side-door fence (ADR 0137 D4 mandatory-envelope-at-rest /
/// C-3; ADR 0127 seam). The LOAD-BEARING guard on at-rest blob confidentiality on the storage role: the resolved
/// <see cref="IBlobStore"/> MUST be the <see cref="EnvelopeBlobStore"/> (never the raw backend); no node-host
/// storage-path caller may register or construct the raw <see cref="FileSystemBlobStore"/> directly (bypassing the
/// envelope); the dev/derivable tenant-key provider is refused fail-closed; and <see cref="EnvelopeBlobStore"/>
/// has no constructor that omits the key provider.
/// </summary>
/// <remarks>
/// This is the blob analogue of <c>DekPairingResolverArchFence</c> — same structure: (1) the storage-role DI
/// composition resolves the envelope store, never the raw backend; (2) the assembly-reflection fence (the known
/// set of <see cref="IBlobStore"/> impls; no rogue raw backend in the host); (3) the source-regex side-door fence
/// + a non-vacuity guard that plants a side door and proves the regex fires; (4) the ctor-no-bypass fence; (5) the
/// fail-closed key-posture gate.
/// </remarks>
public sealed class EnvelopeBlobStoreArchFence
{
    private static byte[] Seed(byte salt)
    {
        var s = new byte[32];
        for (var i = 0; i < s.Length; i++) s[i] = (byte)(i + salt);
        return s;
    }

    private static TenantId TestTenant() => new("arch-fence-tenant");

    // ── FENCE 1 — the STORAGE-ROLE DI composition resolves the ENVELOPE store, NEVER the raw backend ────────────

    [Fact(DisplayName = "PERS-1/F4 fence: the REAL storage-role composition resolves a durability-guarded, envelope-wrapped store, never the raw backend")]
    public void StorageRole_Resolves_Guarded_Envelope_Not_RawBackend()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pers1-archfence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddTestKernelClock();
            // The REAL install-secret tenant-key provider (instance registration — exactly as Program.cs wires it).
            services.AddSingleton<ITenantKeyProvider>(new RootSeedTenantKeyProvider(Seed(1)));
            // PERS-2 F4: exercise the ACTUAL host composition method (the one Program.cs calls), not a hand-rolled
            // re-implementation — so a drift in the real wiring is caught here.
            services.ConfigureStorageRoleBlobStore(dir, "genesis-team");

            using var sp = services.BuildServiceProvider();
            var resolved = sp.GetRequiredService<IBlobStore>();
            var chain = UnwrapChain(resolved);

            // F-Min-3: the outermost store is the durability guard (UnpinAsync is gated) …
            Assert.IsType<DurabilityGuardedBlobStore>(resolved);
            // … the C-3 mandatory-envelope is still in the chain (ciphertext-at-rest preserved) …
            Assert.Contains(chain, s => s is EnvelopeBlobStore);
            // … and NO raw FileSystemBlobStore is the registered/outermost store, nor the volatile edge default.
            Assert.IsNotType<FileSystemBlobStore>(resolved);
            Assert.NotEqual("NodeInMemoryBlobStore", resolved.GetType().Name);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact(DisplayName = "PERS-2/F-Min-3 fence: the storage-role IBlobStore is durability-guarded (symmetry with the record-eviction seam)")]
    public void StorageRole_BlobStore_Is_Durability_Guarded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pers2-fmin3-archfence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddTestKernelClock();
            services.AddSingleton<ITenantKeyProvider>(new RootSeedTenantKeyProvider(Seed(6)));
            services.ConfigureStorageRoleBlobStore(dir, "genesis-team");

            using var sp = services.BuildServiceProvider();
            // The blob-retention shed seam (UnpinAsync) must be guarded on the storage role, mirroring the guarded
            // record-eviction seam — a canonical node cannot silently shed a blob's last copy.
            Assert.IsType<DurabilityGuardedBlobStore>(sp.GetRequiredService<IBlobStore>());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>Walk a decorator chain of <see cref="IBlobStore"/>s by following the first IBlobStore-typed field of each.</summary>
    private static IReadOnlyList<IBlobStore> UnwrapChain(IBlobStore outer)
    {
        var chain = new List<IBlobStore>();
        var current = outer;
        var guard = 0;
        while (current is not null && guard++ < 16)
        {
            chain.Add(current);
            var innerField = current.GetType()
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(f => typeof(IBlobStore).IsAssignableFrom(f.FieldType));
            current = innerField?.GetValue(current) as IBlobStore;
        }
        return chain;
    }

    [Fact(DisplayName = "PERS-1 fence: registering the envelope store BEFORE the in-memory TryAdd suppresses the edge default")]
    public void EnvelopeStore_Registered_First_Suppresses_InMemory_TryAdd()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pers1-archfence-order-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddTestKernelClock();
            services.AddSingleton<ITenantKeyProvider>(new RootSeedTenantKeyProvider(Seed(2)));
            // Storage-role wiring runs FIRST (as Program.cs sequences it before AddNodeForms) …
            services.AddStorageRoleBlobStore(Path.Combine(dir, "blobs"), TestTenant());
            // … then AddNodeForms' edge default TryAdds — and is suppressed because an IBlobStore already exists.
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
                .TryAddSingleton<IBlobStore>(services, _ => throw new InvalidOperationException("edge default must be suppressed"));

            using var sp = services.BuildServiceProvider();
            Assert.IsType<EnvelopeBlobStore>(sp.GetRequiredService<IBlobStore>());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── FENCE 2 — the assembly-reflection fence: the known IBlobStore impls; no rogue raw backend in the host ────

    [Fact(DisplayName = "PERS-1 fence: the node-host assembly declares NO IBlobStore impl other than the volatile edge default")]
    public void HostAssembly_Declares_No_Rogue_BlobStore_Impl()
    {
        var hostAssembly = typeof(LocalNodeOptions).Assembly;
        Assert.Equal("Harborline.Api.LocalNodeHost", hostAssembly.GetName().Name);

        var hostImpls = hostAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IBlobStore).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // The host ships ONLY the volatile in-memory edge default. The durable backend (FileSystemBlobStore) and
        // the envelope wrap (EnvelopeBlobStore) live in foundation / foundation-recovery — the host never declares a
        // raw at-rest backend of its own (a would-be side door).
        Assert.Equal(new[] { "NodeInMemoryBlobStore" }, hostImpls);

        // And the envelope store really IS a decorator, not the raw backend.
        Assert.True(typeof(IBlobStore).IsAssignableFrom(typeof(EnvelopeBlobStore)));
        Assert.NotEqual(typeof(FileSystemBlobStore), typeof(EnvelopeBlobStore));
    }

    // ── FENCE 3 — the SOURCE-regex side-door fence + the NON-VACUITY guard ──────────────────────────────────────

    // Forbidden #1: registering the raw durable backend DIRECTLY as IBlobStore (generic two-arg form).
    private static readonly Regex DirectRawRegistration = new(
        @"(?:Try)?Add(?:Singleton|Scoped|Transient)\s*<\s*(?:[\w.]*\.)?IBlobStore\s*,\s*(?:[\w.]*\.)?FileSystemBlobStore\s*>",
        RegexOptions.Compiled);

    // Forbidden #2: constructing the raw durable backend at all in host production source — the storage-role wiring
    // must delegate to AddStorageRoleBlobStore (which wraps it in the envelope), never `new FileSystemBlobStore(...)`.
    private static readonly Regex RawConstruction = new(
        @"new\s+(?:[\w.]*\.)?FileSystemBlobStore\s*\(",
        RegexOptions.Compiled);

    [Fact(DisplayName = "PERS-1 fence: NO node-host production source registers/constructs the raw FileSystemBlobStore directly")]
    public void No_NodeHost_Source_Uses_Raw_FileSystemBlobStore_Directly()
    {
        var root = NodeHostProjectRoot();
        var offenders = new List<string>();

        foreach (var file in EnumerateProductionSource(root))
        {
            var text = File.ReadAllText(file);
            if (DirectRawRegistration.IsMatch(text) || RawConstruction.IsMatch(text))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The raw FileSystemBlobStore must never be registered/constructed directly in node-host production "
            + "source (it must only exist inside the EnvelopeBlobStore wrap). Offending files: "
            + string.Join(", ", offenders));
    }

    [Fact(DisplayName = "PERS-1 fence NON-VACUITY: the side-door regexes actually fire on a planted side door")]
    public void SideDoor_Regexes_Are_Not_Vacuous()
    {
        // Plant a side door and prove the regex catches it — so a GREEN production scan above is meaningful, not
        // an artefact of a regex that can never match (ADR 0130 inv-8 / anti-pattern A9 non-vacuity discipline).
        const string plantedDirectRegistration = "services.AddSingleton<IBlobStore, FileSystemBlobStore>();";
        const string plantedFullyQualified =
            "services.TryAddSingleton<Harborline.Api.Foundation.Blobs.IBlobStore, Harborline.Api.Foundation.Blobs.FileSystemBlobStore>();";
        const string plantedConstruction = "var raw = new FileSystemBlobStore(rootDir);";

        Assert.Matches(DirectRawRegistration, plantedDirectRegistration);
        Assert.Matches(DirectRawRegistration, plantedFullyQualified);
        Assert.Matches(RawConstruction, plantedConstruction);

        // And the regexes do NOT fire on the LEGITIMATE shapes (the edge in-memory default; the wrapped call).
        Assert.DoesNotMatch(DirectRawRegistration, "services.TryAddSingleton<IBlobStore, NodeInMemoryBlobStore>();");
        Assert.DoesNotMatch(RawConstruction, "services.AddStorageRoleBlobStore(blobRoot, tenant);");
    }

    // ── FENCE 4 — the CTOR-no-bypass fence: no EnvelopeBlobStore ctor omits the key provider ─────────────────────

    [Fact(DisplayName = "PERS-1 fence: EnvelopeBlobStore has NO public constructor that omits the ITenantKeyProvider")]
    public void EnvelopeBlobStore_Has_No_KeyProvider_Bypassing_Ctor()
    {
        var ctors = typeof(EnvelopeBlobStore).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(ctors);
        foreach (var ctor in ctors)
        {
            Assert.Contains(ctor.GetParameters(), p => p.ParameterType == typeof(ITenantKeyProvider));
        }
    }

    // ── FENCE 5 — the FAIL-CLOSED key-posture gate, now an ALLOWLIST (PERS-2 F1) ─────────────────────────────────

    [Fact(DisplayName = "PERS-2/F1 fence: the key-posture gate is an ALLOWLIST — only the real provider passes; stub / unknown / factory / absent fail closed")]
    public void KeyPostureGate_Is_An_Allowlist_Only_Real_Passes()
    {
        // Real provider (instance) → passes.
        var real = new ServiceCollection();
        real.AddTestKernelClock();
        real.AddSingleton<ITenantKeyProvider>(new RootSeedTenantKeyProvider(Seed(3)));
        Assert.Same(real, real.RequireRealBlobEnvelopeKeyProvider());

        // Dev stub (the derivable InMemoryTenantKeyProvider) → fail closed.
        var stub = new ServiceCollection();
        stub.AddTestKernelClock();
        stub.AddSingleton<ITenantKeyProvider, InMemoryTenantKeyProvider>();
        Assert.Throws<InvalidOperationException>(() => stub.RequireRealBlobEnvelopeKeyProvider());

        // PERS-2 F1: an UNKNOWN provider type (not the stub) — a denylist-of-one would have PASSED this; the
        // allowlist REFUSES it fail-closed.
        var unknown = new ServiceCollection();
        unknown.AddTestKernelClock();
        unknown.AddSingleton<ITenantKeyProvider, UnknownTenantKeyProvider>();
        Assert.Throws<InvalidOperationException>(() => unknown.RequireRealBlobEnvelopeKeyProvider());

        // PERS-2 F1: an opaque FACTORY registration (concrete type unverifiable) → fail closed.
        var factory = new ServiceCollection();
        factory.AddTestKernelClock();
        factory.AddSingleton<ITenantKeyProvider>(_ => new RootSeedTenantKeyProvider(Seed(4)));
        Assert.Throws<InvalidOperationException>(() => factory.RequireRealBlobEnvelopeKeyProvider());

        // No provider at all → fail closed.
        var absent = new ServiceCollection();
        absent.AddTestKernelClock();
        Assert.Throws<InvalidOperationException>(() => absent.RequireRealBlobEnvelopeKeyProvider());
    }

    // ── FENCE 6 — PERS-2 F4: the posture gate holds over the REAL host composition graph ─────────────────────────

    [Fact(DisplayName = "PERS-2/F4 fence: the real storage-role composition wins the recovery-coordinator stub TryAdd default")]
    public void RealComposition_RealProvider_Wins_Over_Stub_TryAdd_Default()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pers2-f4-archfence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddTestKernelClock();
            // Real provider registered as an INSTANCE (as Program.cs does, ahead of the recovery coordinator) …
            services.AddSingleton<ITenantKeyProvider>(new RootSeedTenantKeyProvider(Seed(5)));
            // … then the recovery substrate's TryAdd stub default (which must NOT depose the real instance) …
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
                .TryAddSingleton<ITenantKeyProvider, InMemoryTenantKeyProvider>(services);
            // … then the REAL storage-role composition method (posture gate + envelope store).
            services.ConfigureStorageRoleBlobStore(dir, "genesis-team");

            using var sp = services.BuildServiceProvider();
            Assert.IsType<RootSeedTenantKeyProvider>(sp.GetRequiredService<ITenantKeyProvider>());
            var resolved = sp.GetRequiredService<IBlobStore>();
            Assert.IsType<DurabilityGuardedBlobStore>(resolved);                 // guarded outer …
            Assert.Contains(UnwrapChain(resolved), s => s is EnvelopeBlobStore); // … over the C-3 envelope.
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact(DisplayName = "PERS-2/F4 fence: the real storage-role composition FAILS CLOSED when only the dev stub is registered")]
    public void RealComposition_FailsClosed_When_Only_Stub_Registered()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pers2-f4-archfence-stub-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddTestKernelClock();
        // ONLY the derivable dev stub is registered — the real composition path must refuse to wire the store.
        services.AddSingleton<ITenantKeyProvider, InMemoryTenantKeyProvider>();

        Assert.Throws<InvalidOperationException>(() => services.ConfigureStorageRoleBlobStore(dir, "genesis-team"));
    }

    /// <summary>A stand-in ITenantKeyProvider that is NOT the dev stub — proves the allowlist refuses unknown types (F1).</summary>
    private sealed class UnknownTenantKeyProvider : ITenantKeyProvider
    {
        public Task<ReadOnlyMemory<byte>> DeriveKeyAsync(TenantId tenant, string purpose, CancellationToken ct)
            => Task.FromResult<ReadOnlyMemory<byte>>(new byte[32]);

        public Task<ReadOnlyMemory<byte>> DeriveSubjectKeyAsync(
            TenantId tenant, Harborline.Api.Foundation.Recovery.Erasure.SubjectId subject, string purpose,
            Harborline.Api.Foundation.Recovery.Erasure.ISubjectErasureRegistry erasure, CancellationToken ct)
            => Task.FromResult<ReadOnlyMemory<byte>>(new byte[32]);
    }

    // ── source-scan helpers ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The node-host project root, located from THIS file's compile-time path (<see cref="CallerFilePathAttribute"/>)
    /// — robust against the runtime cwd / bin layout. This file lives at
    /// <c>apps/local-node-host/tests/ArchTests/EnvelopeBlobStoreArchFence.cs</c>, so three directory hops up is the
    /// node-host project root.
    /// </summary>
    private static string NodeHostProjectRoot([CallerFilePath] string thisFile = "")
    {
        var archTestsDir = Path.GetDirectoryName(thisFile)!;     // …/tests/ArchTests
        var testsDir = Path.GetDirectoryName(archTestsDir)!;     // …/tests
        var hostRoot = Path.GetDirectoryName(testsDir)!;         // …/local-node-host
        Assert.True(
            File.Exists(Path.Combine(hostRoot, "Program.cs")) && File.Exists(Path.Combine(hostRoot, "LocalNodeOptions.cs")),
            $"Could not locate the node-host project root from '{thisFile}' (resolved '{hostRoot}').");
        return hostRoot;
    }

    /// <summary>Node-host PRODUCTION .cs source — excludes the tests project, bin/obj, and the worktrees mirror.</summary>
    private static IEnumerable<string> EnumerateProductionSource(string hostRoot)
    {
        foreach (var file in Directory.EnumerateFiles(hostRoot, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(hostRoot, file);
            var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s =>
                    s.Equals("tests", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || s.Equals(".worktrees", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            yield return file;
        }
    }
}
