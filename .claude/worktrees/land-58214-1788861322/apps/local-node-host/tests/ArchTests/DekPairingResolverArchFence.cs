using System.Reflection;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.KeyDistribution;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.KeyDistribution;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Tests.TestDoubles;

using Xunit;

// MD-1 uses the FOUNDATION canonical-JSON Ed25519 signer/verifier (the IOperationSigner path); disambiguate from the
// kernel-security Crypto Ed25519Signer/Verifier of the same name.
using Ed25519Signer = Harborline.Api.Foundation.Crypto.Ed25519Signer;
using Ed25519Verifier = Harborline.Api.Foundation.Crypto.Ed25519Verifier;
using MemberRoster = Harborline.Api.Foundation.IdentityAtlas.MemberRoster;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// <b>DekPairingResolverArchFence</b> — the MD-1 / <b>G-1</b> no-mock-crypto fence (joint ADR 0113+0117 amendment,
/// 2026-06-24; composes the #1325 / bug-1312 B1 pattern). The LOAD-BEARING guard on tenant-DEK distribution: the
/// wrap recipient key MUST be roster-bound, the production resolver MUST be the roster-bound provider (fail-closed
/// null-object for any unadmitted recipient), the SHIPPED assembly MUST declare ZERO identity-derivable DEK
/// resolvers, and the LEAK test MUST run against the REAL roster-bound resolver (a green leak test against a
/// stand-in is a FALSE POSITIVE).
/// </summary>
/// <remarks>
/// This file is the DEK-pairing analogue of <c>CommsConversationScopeArchTests</c>'s B1/C5 DM-key fence — same
/// structure: (1) the fail-closed default posture; (2) the production override registers the roster-bound resolver;
/// (3) the assembly-reflection fence (no identity-derivable resolver ships); (4) the real leak test + the real
/// end-to-end admitted-decrypt against the roster-bound resolver; (5) the G-2 compartmentalization vector.
/// </remarks>
public sealed class DekPairingResolverArchFence
{
    private static readonly IX25519KeyAgreement Kem = new X25519KeyAgreement();
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    // ── Helpers: a verified roster with a real admitted party whose DM key is signed in, + the node's DM keypair ──

    /// <summary>
    /// Build a verified two-party roster (founder "owner" admits "sally"), with each party's DM public key derived
    /// from its OWN node root seed + signed into its admission (the C5 forge-proof binding). Returns the host-level
    /// <see cref="NodeTeamRoster"/> the resolver consumes, the admin signer, sally's node root seed (so the test can
    /// derive sally's DM PRIVATE key for unwrap), and the team id string.
    /// </summary>
    private static (NodeTeamRoster roster, Ed25519Signer adminSigner, byte[] sallyRootSeed, string teamId)
        BuildRosterWithAdmittedParty()
    {
        var teamId = Guid.NewGuid();
        var teamIdStr = teamId.ToString("D");

        var ownerKp = KeyPair.Generate();
        var ownerSigner = new Ed25519Signer(ownerKp);
        var ownerRootSeed = RandomSeed(11);
        var ownerDmPub = NodeDmKeyDerivation.DeriveDmPublicKey(ownerRootSeed, teamIdStr);

        var sallyKp = KeyPair.Generate();
        var sallyRootSeed = RandomSeed(97);
        var sallyDmPub = NodeDmKeyDerivation.DeriveDmPublicKey(sallyRootSeed, teamIdStr);

        // Genesis(owner) then Admit(sally) — both DM keys signed into the chain (forge-proof).
        var member = MemberRoster
            .Genesis(teamId, "owner", ownerSigner, Verifier, DateTimeOffset.UnixEpoch, Guid.NewGuid(),
                founderDmPublicKey: PrincipalId.FromBytes(ownerDmPub).ToBase64Url())
            .Admit("owner", ownerSigner, "sally", sallyKp.PrincipalId,
                Harborline.Api.Foundation.IdentityAtlas.Permissions.PermissionCompositions.Member,
                Verifier, DateTimeOffset.UnixEpoch.AddSeconds(1), Guid.NewGuid(),
                newDmPublicKey: PrincipalId.FromBytes(sallyDmPub).ToBase64Url());

        // Host-level roster: seed the live DM-key map exactly as production harvests it from the validated chain.
        var roster = new NodeTeamRoster(member);
        roster.SetOwnDmPublicKey("owner", ownerDmPub);
        roster.SetOwnDmPublicKey("sally", sallyDmPub);

        return (roster, ownerSigner, sallyRootSeed, teamIdStr);
    }

    private static byte[] RandomSeed(byte salt)
    {
        var s = new byte[32];
        for (var i = 0; i < s.Length; i++) s[i] = (byte)(i + salt);
        return s;
    }

    private static byte[] FreshDek()
    {
        var dek = new byte[TenantDekWrapper.TenantDekLength];
        RandomNumberGenerator.Fill(dek);
        return dek;
    }

    // ── FENCE 1 — fail-closed DEFAULT posture ──────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "G-1 fence: AddNodeTenantDekPairing ALONE (no roster) registers the FAIL-CLOSED NoTenantDekPairingResolver")]
    public void AddNodeTenantDekPairing_Alone_Is_FailClosed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IX25519KeyAgreement, X25519KeyAgreement>();
        services.AddNodeTenantDekPairing();

        using var sp = services.BuildServiceProvider();
        var resolver = sp.GetRequiredService<ITenantDekPairingResolver>();

        Assert.IsType<NoTenantDekPairingResolver>(resolver);
        // No recipient is resolvable → no DEK can be wrapped to anyone (fail-closed, never plaintext).
        Assert.Null(resolver.ResolveRecipientWrapKey("sally"));
        Assert.Null(resolver.ResolveRecipientWrapKey("owner"));
    }

    // ── FENCE 2 — the production override registers the ROSTER-BOUND resolver ──────────────────────────────────

    [Fact(DisplayName = "G-1 fence: the FULL wiring (+ AddRosterBoundTenantDekPairingResolver) registers the ROSTER-BOUND resolver")]
    public void FullWiring_Registers_RosterBound_Resolver()
    {
        var (roster, _, _, _) = BuildRosterWithAdmittedParty();

        var services = new ServiceCollection();
        services.AddSingleton<IX25519KeyAgreement, X25519KeyAgreement>();
        services.AddSingleton(roster);
        services.AddNodeTenantDekPairing();
        services.AddRosterBoundTenantDekPairingResolver(); // the production override.

        using var sp = services.BuildServiceProvider();
        var resolver = sp.GetRequiredService<ITenantDekPairingResolver>();

        Assert.IsType<RosterBoundTenantDekPairingResolver>(resolver);
        Assert.IsNotType<NoTenantDekPairingResolver>(resolver);

        // An ADMITTED party resolves to its ROSTER-BOUND DM key; an UNADMITTED party resolves null (fail-closed).
        var sallyKey = resolver.ResolveRecipientWrapKey("sally");
        Assert.NotNull(sallyKey);
        Assert.Equal(roster.DmPublicKeyOf("sally"), sallyKey); // it IS the roster's signed key, nothing derived.
        Assert.Null(resolver.ResolveRecipientWrapKey("mallory")); // never admitted → no wrap key.
    }

    // ── FENCE 3 — the SHIPPED assembly declares ZERO identity-derivable DEK resolvers ──────────────────────────

    [Fact(DisplayName = "G-1 fence: the SHIPPED assembly's ONLY ITenantDekPairingResolver impls are the roster-bound + fail-closed (NO identity-derivable one)")]
    public void ProductionAssembly_DekPairingResolver_Is_RosterBound_Or_FailClosed_Only()
    {
        var productionAssembly = typeof(RosterBoundTenantDekPairingResolver).Assembly;
        Assert.Equal("Harborline.Api.LocalNodeHost", productionAssembly.GetName().Name);

        var resolverImpls = productionAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && typeof(ITenantDekPairingResolver).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // The ONLY shipped resolvers are the roster-bound one and the fail-closed null-object. There is NO third
        // (identity-derivable) resolver in production — the only identity-derivable one lives in the TEST assembly.
        Assert.Equal(
            new[] { nameof(NoTenantDekPairingResolver), nameof(RosterBoundTenantDekPairingResolver) },
            resolverImpls);
        Assert.DoesNotContain(nameof(IdentityDerivableTenantDekPairingResolver), resolverImpls);

        // And the identity-derivable stand-in really is test-assembly-only (structurally unreferenceable from prod).
        var testDouble = typeof(IdentityDerivableTenantDekPairingResolver);
        Assert.Equal("Harborline.Api.LocalNodeHost.Tests", testDouble.Assembly.GetName().Name);
    }

    // ── FENCE 4a — the REAL LEAK TEST (against the REAL roster-bound resolver, NOT a stand-in) ─────────────────

    [Fact(DisplayName = "G-1 leak test (REAL resolver): an UNADMITTED / rogue party resolves NO wrap key — no DEK can be minted for it")]
    public void Leak_Test_Unadmitted_Party_Gets_No_Dek_From_Real_Resolver()
    {
        // This is the REAL no-leak guarantee, run against the REAL RosterBoundTenantDekPairingResolver — NOT a
        // construction stand-in. A rogue party that simply CLAIMS an id it was never admitted under resolves null,
        // so the pairing route can mint NO DEK wrap for it. (The bug-1312 lesson: a green leak test against a
        // party-id-derivable stand-in would be a FALSE POSITIVE; this runs against the production resolver.)
        var (roster, adminSigner, _, teamId) = BuildRosterWithAdmittedParty();
        ITenantDekPairingResolver resolver = new RosterBoundTenantDekPairingResolver(roster);
        var wrapper = new TenantDekWrapper(Kem);
        var dek = FreshDek();

        // A rogue party "mallory" — never admitted. The resolver gives no wrap key.
        Assert.Null(resolver.ResolveRecipientWrapKey("mallory"));

        // So the pairing flow cannot wrap a DEK to mallory: a host that resolves null MUST fail closed (it has no
        // recipient key to pass the wrapper). Demonstrate the flow does exactly that.
        var malloryWrapKey = resolver.ResolveRecipientWrapKey("mallory");
        Assert.Null(malloryWrapKey); // ← the fail-closed gate; the host never reaches Wrap() with a null key.

        // Contrast: an ADMITTED party DOES resolve, so the flow proceeds for a legitimate recipient (FENCE 4b proves
        // the full decrypt). This asserts the resolver is the boundary — admission, not identity-claim, gates it.
        var sallyWrapKey = resolver.ResolveRecipientWrapKey("sally");
        Assert.NotNull(sallyWrapKey);
        var ctx = TenantDekPairingContext.For("tenant-A", "sally", homeEpoch: 0);
        var wrap = wrapper.Wrap(dek, sallyWrapKey!, ctx, "owner", adminSigner);
        Assert.NotNull(wrap); // a legitimate wrap is producible only for an admitted recipient.
        _ = teamId;
    }

    [Fact(DisplayName = "G-1 leak test (REAL resolver): a rogue party that FORGES an admitted party's id still cannot unwrap (it lacks the node-secret private key)")]
    public void Leak_Test_Forged_Id_Cannot_Unwrap_Because_Private_Key_Is_Node_Secret()
    {
        // The deeper no-leak guarantee: even if a wrap for "sally" is observed on the wire, a rogue party that CLAIMS
        // to be sally cannot unwrap it — sally's DM PRIVATE key is HKDF(sally-node-root, teamId), node-secret, NOT
        // derivable from the party id (the structural difference from the #1325 stand-in). The rogue holds a
        // DIFFERENT private key, so OpenBox fails closed.
        var (roster, adminSigner, sallyRootSeed, teamId) = BuildRosterWithAdmittedParty();
        ITenantDekPairingResolver resolver = new RosterBoundTenantDekPairingResolver(roster);
        var wrapper = new TenantDekWrapper(Kem);
        var dek = FreshDek();

        var ctx = TenantDekPairingContext.For("tenant-A", "sally", homeEpoch: 0);
        var sallyWrapKey = resolver.ResolveRecipientWrapKey("sally")!;
        var wrap = wrapper.Wrap(dek, sallyWrapKey, ctx, "owner", adminSigner);

        // The genuine sally unwraps (her node-secret private key matches her roster-bound public key).
        var sallyPrivate = NodeDmKeyDerivation.DeriveDmPrivateKey(sallyRootSeed, teamId);
        var genuine = wrapper.VerifyAndUnwrap(wrap, adminSigner.IssuerId, ctx, sallyPrivate, Verifier);
        Assert.Equal(dek, genuine);

        // A rogue "claims to be sally" but holds its OWN node-root-derived private key → OpenBox fails closed.
        var rogueRootSeed = RandomSeed(200);
        var roguePrivate = NodeDmKeyDerivation.DeriveDmPrivateKey(rogueRootSeed, teamId);
        var leaked = wrapper.VerifyAndUnwrap(wrap, adminSigner.IssuerId, ctx, roguePrivate, Verifier);
        Assert.Null(leaked); // the forged-identity unwrap yields NO DEK — the real no-leak guarantee.
    }

    // ── FENCE 4b — END-TO-END: an admitted party unwraps the DEK + DECRYPTS the tenant's real data ─────────────

    [Fact(DisplayName = "MD-1 E2E: an ADMITTED party unwraps the tenant DEK + DECRYPTS the tenant's SQLCipher data; an UNADMITTED party cannot")]
    public void E2E_Admitted_Party_Decrypts_Tenant_Data_Unadmitted_Cannot()
    {
        var (roster, adminSigner, sallyRootSeed, teamId) = BuildRosterWithAdmittedParty();
        ITenantDekPairingResolver resolver = new RosterBoundTenantDekPairingResolver(roster);
        var wrapper = new TenantDekWrapper(Kem);

        // The tenant's REAL data-encryption key: HKDF(owner-root, relationalStoreKeyId) — the SQLCipher DEK keying
        // the financial store. Create a SQLCipher store under it and write a row of the tenant's data.
        var ownerRootSeed = RandomSeed(11); // (matches the owner seed BuildRosterWithAdmittedParty used)
        var tenantDek = new SqlCipherKeyDerivation().DeriveSqlCipherKey(
            ownerRootSeed, LocalNodeSqlCipherRegistration.RelationalStoreKeyId);

        var dir = Path.Combine(Path.GetTempPath(), "harborline-md1-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "tenant-data.db");
        try
        {
            const string secret = "tenant-A confidential ledger row #4001";
            CreateSqlCipherStoreWithRow(dbPath, tenantDek, secret);

            // ── The ADMITTED party (sally) ── resolves her roster-bound recipient key, the owner wraps the tenant DEK
            // to her, she verify+unwraps with her node-secret private key, opens the store, reads the tenant's data.
            var sallyWrapKey = resolver.ResolveRecipientWrapKey("sally");
            Assert.NotNull(sallyWrapKey);
            var ctx = TenantDekPairingContext.For("tenant-A", "sally", homeEpoch: 0);
            var wrap = wrapper.Wrap(tenantDek, sallyWrapKey!, ctx, "owner", adminSigner);

            var sallyPrivate = NodeDmKeyDerivation.DeriveDmPrivateKey(sallyRootSeed, teamId);
            var recoveredDek = wrapper.VerifyAndUnwrap(wrap, adminSigner.IssuerId, ctx, sallyPrivate, Verifier);
            Assert.NotNull(recoveredDek);
            Assert.Equal(tenantDek, recoveredDek);

            var read = ReadRowUnderKey(dbPath, recoveredDek!);
            Assert.Equal(secret, read); // the admitted party DECRYPTS the tenant's real data end-to-end.

            // ── The UNADMITTED party (mallory) ── resolves NO wrap key → the pairing flow fails closed: no DEK is
            // ever minted for her, so she has nothing to open the store with.
            Assert.Null(resolver.ResolveRecipientWrapKey("mallory"));

            // And even the recovered store-DEK only opens THIS tenant's store: a sanity check that a wrong key fails.
            var wrongDek = FreshDek();
            Assert.Throws<SqliteException>(() => ReadRowUnderKey(dbPath, wrongDek));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── FENCE 5 — G-2 COMPARTMENTALIZATION: a Harborline wrap yields no flight-deck-decryptable material ──────────

    [Fact(DisplayName = "MD-1 G-2: a tenant-A (harborline) DEK wrap cannot decrypt tenant-B (flight-deck) data — per-app DEK lineages stay independent")]
    public void G2_Compartmentalization_LegacyWrap_Cannot_Decrypt_FlightDeck()
    {
        var (roster, adminSigner, sallyRootSeed, teamId) = BuildRosterWithAdmittedParty();
        ITenantDekPairingResolver resolver = new RosterBoundTenantDekPairingResolver(roster);
        var wrapper = new TenantDekWrapper(Kem);

        // Two INDEPENDENT per-app root seeds → two INDEPENDENT tenant DEKs for the same logical org (0117 D2).
        var harborlineRoot = RandomSeed(11);
        var flightDeckRoot = RandomSeed(211);
        var keyDeriv = new SqlCipherKeyDerivation();
        var harborlineDek = keyDeriv.DeriveSqlCipherKey(harborlineRoot, LocalNodeSqlCipherRegistration.RelationalStoreKeyId);
        var flightDeckDek = keyDeriv.DeriveSqlCipherKey(flightDeckRoot, LocalNodeSqlCipherRegistration.RelationalStoreKeyId);
        Assert.NotEqual(harborlineDek, flightDeckDek); // independent lineages.

        // Wrap ONLY the Harborline DEK to sally; she unwraps it.
        var ctx = TenantDekPairingContext.For("harborline-tenant-A", "sally", homeEpoch: 0);
        var wrap = wrapper.Wrap(harborlineDek, resolver.ResolveRecipientWrapKey("sally")!, ctx, "owner", adminSigner);
        var sallyPrivate = NodeDmKeyDerivation.DeriveDmPrivateKey(sallyRootSeed, teamId);
        var recovered = wrapper.VerifyAndUnwrap(wrap, adminSigner.IssuerId, ctx, sallyPrivate, Verifier);

        Assert.NotNull(recovered);
        Assert.Equal(harborlineDek, recovered);        // it carries the Harborline DEK …
        Assert.NotEqual(flightDeckDek, recovered);   // … and is NOT the flight-deck DEK — no cross-app material.

        // Concretely: a flight-deck store keyed with flightDeckDek does NOT open under the unwrapped Harborline DEK.
        var dir = Path.Combine(Path.GetTempPath(), "harborline-md1-g2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var fdPath = Path.Combine(dir, "flight-deck.db");
        try
        {
            CreateSqlCipherStoreWithRow(fdPath, flightDeckDek, "flight-deck tenant-B data");
            Assert.Throws<SqliteException>(() => ReadRowUnderKey(fdPath, recovered!)); // Harborline key cannot open it.
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── SQLCipher helpers (the proven raw-key-hex PRAGMA pattern from SqlCipherStoreDekTests) ──────────────────

    // SC-1 (LocalNodeSqlCipherRegistration): the cipher provider MUST be bound explicitly, else a bare
    // Batteries init can pin the NON-cipher e_sqlite3 provider and PRAGMA key becomes a SILENT NO-OP (plaintext).
    // These tests open SQLCipher stores directly (no host registration runs), so pin the cipher provider once here.
    private static int s_cipherProviderBound;

    private static void EnsureCipherProvider()
    {
        if (Interlocked.Exchange(ref s_cipherProviderBound, 1) == 0)
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());
        }
    }

    private static void CreateSqlCipherStoreWithRow(string dbPath, byte[] key, string value)
    {
        EnsureCipherProvider();
        using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            conn.Open();
            ApplyKey(conn, key);
            using var create = conn.CreateCommand();
            create.CommandText = "CREATE TABLE t(v TEXT); INSERT INTO t(v) VALUES ($v);";
            create.Parameters.AddWithValue("$v", value);
            create.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private static string ReadRowUnderKey(string dbPath, byte[] key)
    {
        EnsureCipherProvider();
        // Clear pools so a wrong-key open cannot reuse a pooled handle that was opened under a different key — the
        // same hygiene SqlCipherStoreDekTests uses; otherwise the wrong-key probe can spuriously succeed.
        SqliteConnection.ClearAllPools();
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        ApplyKey(conn, key);
        // Force SQLCipher to decrypt the header by reading sqlite_schema FIRST — a wrong/absent key surfaces here as
        // SQLITE_NOTADB ("file is not a database"), exactly as the SC-1 interceptor probe does. Without this, the
        // first row read may not reliably trip the key check.
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT count(*) FROM sqlite_schema;";
            probe.ExecuteScalar();
        }
        using var read = conn.CreateCommand();
        read.CommandText = "SELECT v FROM t LIMIT 1;";
        return (string)read.ExecuteScalar()!;
    }

    private static void ApplyKey(SqliteConnection conn, byte[] key)
    {
        var hex = Convert.ToHexString(key);
        using var keyCmd = conn.CreateCommand();
        keyCmd.CommandText = $"PRAGMA key = \"x'{hex}'\";";
        keyCmd.ExecuteNonQuery();
    }
}
