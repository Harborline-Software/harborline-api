using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Recovery.Blobs;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

namespace Harborline.Api.LocalNodeHost.Tests.Blobs;

/// <summary>
/// PERS-1 ciphertext round-trip GATE (ADR 0137 D4 mandatory-envelope-at-rest / C-3). Proves the storage-role
/// envelope blob store stores CIPHERTEXT (the raw backend never sees plaintext), round-trips the original
/// plaintext, fails closed on a tampered envelope, and — by the random-nonce design — yields DIFFERENT CIDs for
/// two puts of identical plaintext (documents the C-3 no-equality-oracle / no-blob-dedup decision).
/// </summary>
public sealed class EnvelopeBlobStoreRoundTripTests
{
    private static byte[] Seed(byte salt)
    {
        var s = new byte[32];
        for (var i = 0; i < s.Length; i++) s[i] = (byte)(i + salt);
        return s;
    }

    private static EnvelopeBlobStore NewStore(IBlobStore inner, byte seedSalt = 7)
        => new(inner, new RootSeedTenantKeyProvider(Seed(seedSalt)), new TenantId("round-trip-tenant"));

    [Fact(DisplayName = "PERS-1 GATE: PutAsync stores CIPHERTEXT (inner bytes != plaintext); GetAsync returns the original plaintext")]
    public async Task PutAsync_Stores_Ciphertext_GetAsync_Returns_Plaintext()
    {
        var inner = new CapturingBlobStore();
        var store = NewStore(inner);
        var plaintext = Encoding.UTF8.GetBytes("tenant-A confidential blob payload #4001 — never store me in the clear");

        var cid = await store.PutAsync(plaintext);

        // The raw backend holds the ENVELOPE, not the plaintext.
        var stored = Assert.Single(inner.Stored.Values);
        Assert.NotEqual(plaintext, stored);                              // it is NOT the plaintext …
        Assert.False(ContainsSubsequence(stored, plaintext),            // … and the plaintext does not appear inside it.
            "the plaintext must not appear anywhere in the stored envelope bytes");
        Assert.True(stored.Length > plaintext.Length);                  // envelope = header + nonce + tag + ciphertext.
        Assert.Equal(0xE5, stored[0]);                                  // the self-describing envelope magic.

        // GetAsync decrypts back to the exact original plaintext.
        var recovered = await store.GetAsync(cid);
        Assert.NotNull(recovered);
        Assert.Equal(plaintext, recovered!.Value.ToArray());
    }

    [Fact(DisplayName = "PERS-1 GATE: an empty payload round-trips (ciphertext-only, tag still authenticates)")]
    public async Task Empty_Payload_RoundTrips()
    {
        var inner = new CapturingBlobStore();
        var store = NewStore(inner);

        var cid = await store.PutAsync(ReadOnlyMemory<byte>.Empty);
        var stored = Assert.Single(inner.Stored.Values);
        Assert.Equal(35, stored.Length);                                // header(35) + 0 ciphertext bytes.

        var recovered = await store.GetAsync(cid);
        Assert.NotNull(recovered);
        Assert.Equal(0, recovered!.Value.Length);
    }

    [Fact(DisplayName = "PERS-1 GATE: a TAMPERED envelope fails GCM authentication and throws (never returns garbage)")]
    public async Task Tampered_Envelope_Fails_Closed()
    {
        var inner = new CapturingBlobStore();
        var store = NewStore(inner);
        var plaintext = Encoding.UTF8.GetBytes("authenticated payload");

        var cid = await store.PutAsync(plaintext);

        // Flip the last byte of the stored envelope (a ciphertext byte) — GCM auth must reject it.
        inner.CorruptLastByte(cid);

        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await store.GetAsync(cid));
    }

    [Fact(DisplayName = "PERS-2 F2: tampering the self-describing HEADER descriptor (suite byte) fails closed")]
    public async Task Tampered_Header_Descriptor_Fails_Closed()
    {
        var inner = new CapturingBlobStore();
        var store = NewStore(inner);
        var cid = await store.PutAsync(Encoding.UTF8.GetBytes("header-bound payload"));

        // Flip the SUITE byte (offset 2) — a header-descriptor byte now bound as GCM AAD. It must never decrypt to
        // the original: either the parse rejects it (unregistered suite) or GCM auth rejects it (AAD mismatch).
        inner.CorruptByteAt(cid, offset: 2);

        await Assert.ThrowsAnyAsync<Exception>(async () => await store.GetAsync(cid));
    }

    [Fact(DisplayName = "PERS-2 F3: an envelope stamped with an UNSUPPORTED key version fails closed")]
    public async Task Unsupported_KeyVersion_Fails_Closed()
    {
        var inner = new CapturingBlobStore();
        var store = NewStore(inner);
        var cid = await store.PutAsync(Encoding.UTF8.GetBytes("versioned payload"));

        // Overwrite the keyVersion (offset 3, little-endian Int32) with an unsupported value (99). The reader must
        // refuse to derive under a version this build does not support.
        inner.CorruptByteAt(cid, offset: 3, value: 99);

        await Assert.ThrowsAnyAsync<Exception>(async () => await store.GetAsync(cid));
    }

    [Fact(DisplayName = "PERS-1 GATE: two PutAsync of IDENTICAL plaintext yield DIFFERENT CIDs (random nonce; no blob-dedup, C-3)")]
    public async Task Identical_Plaintext_Yields_Different_Cids()
    {
        var inner = new CapturingBlobStore();
        var store = NewStore(inner);
        var plaintext = Encoding.UTF8.GetBytes("the same bytes, twice");

        var cid1 = await store.PutAsync(plaintext);
        var cid2 = await store.PutAsync(plaintext);

        Assert.NotEqual(cid1, cid2);                                    // different ciphertext → different CID.
        Assert.Equal(2, inner.Stored.Count);                           // both envelopes are stored independently.

        // Both still decrypt back to the same plaintext.
        Assert.Equal(plaintext, (await store.GetAsync(cid1))!.Value.ToArray());
        Assert.Equal(plaintext, (await store.GetAsync(cid2))!.Value.ToArray());
    }

    [Fact(DisplayName = "PERS-1: the blob DEK re-derives stably across a 'restart' (a fresh store over the SAME seed reads old blobs)")]
    public async Task Key_ReDerives_Stably_Across_Restart()
    {
        var inner = new CapturingBlobStore();
        var plaintext = Encoding.UTF8.GetBytes("survives a restart");

        // Seal under one provider instance …
        var before = NewStore(inner, seedSalt: 11);
        var cid = await before.PutAsync(plaintext);

        // … then reconstruct a FRESH EnvelopeBlobStore + a FRESH RootSeedTenantKeyProvider over the SAME root seed
        // (a faithful "restart": same install secret, new objects) and read the old blob back.
        var after = NewStore(inner, seedSalt: 11);
        var recovered = await after.GetAsync(cid);
        Assert.NotNull(recovered);
        Assert.Equal(plaintext, recovered!.Value.ToArray());

        // A DIFFERENT install seed cannot decrypt it (the key is install-secret, not derivable from public inputs).
        var wrongInstall = NewStore(inner, seedSalt: 99);
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await wrongInstall.GetAsync(cid));
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
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

    /// <summary>
    /// A minimal content-addressed inner <see cref="IBlobStore"/> that CAPTURES the exact bytes handed to
    /// <see cref="PutAsync"/> (the envelope), so tests can assert the backend only ever sees ciphertext and can
    /// tamper with a stored envelope.
    /// </summary>
    private sealed class CapturingBlobStore : IBlobStore
    {
        public ConcurrentDictionary<string, byte[]> Stored { get; } = new(StringComparer.Ordinal);

        public ValueTask<Cid> PutAsync(ReadOnlyMemory<byte> content, CancellationToken ct = default)
        {
            var cid = Cid.FromBytes(content.Span);
            Stored[cid.Value] = content.ToArray();
            return ValueTask.FromResult(cid);
        }

        public ValueTask<ReadOnlyMemory<byte>?> GetAsync(Cid cid, CancellationToken ct = default)
            => ValueTask.FromResult(Stored.TryGetValue(cid.Value, out var bytes)
                ? (ReadOnlyMemory<byte>?)bytes
                : null);

        public ValueTask<bool> ExistsLocallyAsync(Cid cid, CancellationToken ct = default)
            => ValueTask.FromResult(Stored.ContainsKey(cid.Value));

        public ValueTask PinAsync(Cid cid, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask UnpinAsync(Cid cid, CancellationToken ct = default) => ValueTask.CompletedTask;

        public void CorruptLastByte(Cid cid)
        {
            var bytes = Stored[cid.Value];
            bytes[^1] ^= 0xFF;
        }

        public void CorruptByteAt(Cid cid, int offset, byte? value = null)
        {
            var bytes = Stored[cid.Value];
            bytes[offset] = value ?? (byte)(bytes[offset] ^ 0xFF);
        }
    }
}
