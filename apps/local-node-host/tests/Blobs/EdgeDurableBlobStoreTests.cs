using System.IO;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Recovery.Blobs;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Data.Storage;

namespace Harborline.Api.LocalNodeHost.Tests.Blobs;

/// <summary>
/// Card 3750 (gap G2.1, BLOCKER) — the Harborline App's bundled node must not default to the volatile in-memory blob
/// store. The default (Edge) role now wires the durable envelope-sealed blob store through the SAME composition
/// method <c>Program.cs</c> calls (<see cref="NodeStorageRoleComposition.ConfigureEdgeDurableBlobStore"/> — the
/// PERS-2 F4 real-code-path discipline), so:
/// (1) a blob written through the composed graph SURVIVES a restart (a brand-new provider over the same data
///     directory re-reads it by CID),
/// (2) the at-rest bytes on disk are a sealed envelope (self-describing 0xE5 magic; plaintext never appears),
/// (3) the volatile <c>NodeInMemoryBlobStore</c> is NOT reachable in the default composition — its
///     <c>TryAddSingleton</c> in <see cref="NodeFormsComposition.AddNodeForms"/> is suppressed — and remains
///     available only by explicit registration (tests),
/// (4) the fail-closed key-posture gate applies to the edge role exactly as it does to the storage role.
/// </summary>
public sealed class EdgeDurableBlobStoreTests
{
    private static byte[] Seed(byte salt)
    {
        var s = new byte[32];
        for (var i = 0; i < s.Length; i++) s[i] = (byte)(i + salt);
        return s;
    }

    /// <summary>Compose the DEFAULT (edge-role) graph exactly as Program.cs does: the real install-secret
    /// tenant-key provider instance, then the edge durable blob store, then AddNodeForms (whose in-memory
    /// TryAdd default must lose).</summary>
    private static ServiceProvider BuildDefaultComposition(string dataDirectory, byte seedSalt = 11)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantKeyProvider>(new RootSeedTenantKeyProvider(Seed(seedSalt)));
        services.ConfigureEdgeDurableBlobStore(dataDirectory, "genesis-team");
        services.AddTestAuthorizationGate().AddTestNodeForms();
        return services.BuildServiceProvider();
    }

    [Fact(DisplayName = "3750 GATE: a blob written through the composed default host survives a restart (new provider, same data dir) and re-reads by CID")]
    public async Task Blob_Survives_Restart_And_AtRest_Bytes_Are_A_Sealed_Envelope()
    {
        var dir = Path.Combine(Path.GetTempPath(), "edge-durable-blob-" + Guid.NewGuid().ToString("N"));
        var plaintext = Encoding.UTF8.GetBytes(
            "harborline spatial-capture payload 3750 — must survive the process and never touch disk in the clear");
        try
        {
            Cid cid;
            // "Process" 1: compose, write, tear the whole provider down (the restart).
            await using (var sp = BuildDefaultComposition(dir))
            {
                var store = sp.GetRequiredService<IBlobStore>();
                cid = await store.PutAsync(plaintext);
            }

            // The at-rest artifact is a SEALED ENVELOPE, not cleartext: every stored file starts with the
            // self-describing envelope magic (0xE5) and the plaintext appears nowhere in the stored bytes.
            var blobRoot = Path.Combine(dir, "blobs");
            var storedFiles = Directory
                .EnumerateFiles(blobRoot, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".pins" + Path.DirectorySeparatorChar))
                .ToArray();
            var stored = Assert.Single(storedFiles);
            var atRest = await File.ReadAllBytesAsync(stored);
            Assert.Equal(0xE5, atRest[0]);
            Assert.True(atRest.Length > plaintext.Length, "envelope = header + nonce + tag + ciphertext");
            Assert.False(ContainsSubsequence(atRest, plaintext),
                "the plaintext must not appear anywhere in the at-rest bytes");

            // "Process" 2: a brand-new provider over the SAME data directory (restart) re-reads by CID.
            await using (var sp = BuildDefaultComposition(dir))
            {
                var store = sp.GetRequiredService<IBlobStore>();
                var recovered = await store.GetAsync(cid);
                Assert.NotNull(recovered);
                Assert.Equal(plaintext, recovered!.Value.ToArray());
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact(DisplayName = "3750 GATE: the volatile in-memory store is NOT reachable in the default composition — the resolved IBlobStore is the envelope store")]
    public void Default_Composition_Resolves_Envelope_Store_Never_InMemory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "edge-durable-blob-type-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var sp = BuildDefaultComposition(dir);
            var resolved = sp.GetRequiredService<IBlobStore>();
            Assert.IsType<EnvelopeBlobStore>(resolved);
            Assert.NotEqual("NodeInMemoryBlobStore", resolved.GetType().Name);
            Assert.IsNotType<FileSystemBlobStore>(resolved); // no-side-door: never the raw backend either.
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact(DisplayName = "3750 GATE: the edge role runs the SAME fail-closed key-posture gate — a dev-stub key provider refuses composition")]
    public void Edge_Wiring_Fails_Closed_On_A_NonReal_Key_Provider()
    {
        var dir = Path.Combine(Path.GetTempPath(), "edge-durable-blob-posture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<ITenantKeyProvider>(new InMemoryTenantKeyProvider());
            Assert.Throws<InvalidOperationException>(
                () => services.ConfigureEdgeDurableBlobStore(dir, "genesis-team"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact(DisplayName = "3750: the in-memory store remains available by EXPLICIT registration (test opt-in path preserved)")]
    public void Explicit_Registration_Still_Yields_The_InMemory_Store()
    {
        var services = new ServiceCollection();
        services.AddTestAuthorizationGate().AddTestNodeForms(); // no durable registration first — the TryAdd default applies.
        using var sp = services.BuildServiceProvider();
        Assert.Equal("NodeInMemoryBlobStore", sp.GetRequiredService<IBlobStore>().GetType().Name);
    }

    [Fact(DisplayName = "3750 finding-2 GUARD: the real default composition PASSES RequireDurableBlobStore")]
    public void RequireDurableBlobStore_Passes_On_The_Real_Default_Composition()
    {
        var dir = Path.Combine(Path.GetTempPath(), "edge-durable-blob-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<ITenantKeyProvider>(new RootSeedTenantKeyProvider(Seed(13)));
            services.ConfigureEdgeDurableBlobStore(dir, "genesis-team");
            services.AddTestAuthorizationGate().AddTestNodeForms();
            services.RequireDurableBlobStore(); // must not throw — mirrors the Program.cs call site + ordering.
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact(DisplayName = "3750 finding-2 GUARD: deleting the durable wiring fails the gate (in-memory default refused; missing registration refused)")]
    public void RequireDurableBlobStore_Fails_Closed_Without_Durable_Wiring()
    {
        // The exact re-regression the guard exists for: role branch deleted => AddNodeForms' volatile TryAdd wins.
        var volatileServices = new ServiceCollection();
        volatileServices.AddTestAuthorizationGate().AddTestNodeForms();
        Assert.Throws<InvalidOperationException>(() => volatileServices.RequireDurableBlobStore());

        // And no registration at all is refused too.
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().RequireDurableBlobStore());
    }

    [Fact(DisplayName = "3750 finding-2 GUARD: a raw FileSystemBlobStore type registration (C-3 side-door) fails the gate")]
    public void RequireDurableBlobStore_Refuses_The_Raw_Filesystem_Backend()
    {
        var dir = Path.Combine(Path.GetTempPath(), "edge-durable-blob-sidedoor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IBlobStore>(new FileSystemBlobStore(dir));
            Assert.Throws<InvalidOperationException>(() => services.RequireDurableBlobStore());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
}
