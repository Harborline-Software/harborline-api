using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Data.Search.Vector.Sqlite;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Vector;

/// <summary>
/// F2 (Slice-1d, HOST-GATED — the Slice-1b deep-review's F2 gate). The real <c>vec0</c> native path
/// (<see cref="Vec0KnnEngine"/>, <see cref="Vec0Native.TryLoad"/>'s load-extension-then-re-disable window,
/// <see cref="Vec0KnnEngine.EnsureTableAsync"/>) has ZERO runtime callers in Slice 1b — it is substrate. Before
/// the real native is trusted on any shipped host, it MUST pass a NATIVE-GATED parity probe that exercises the
/// vec0 engine through the SAME clip / no-neighbour-leak assertions the brute-force suite uses, AND asserts
/// <c>load_extension</c> is unreachable post-load (the spike-R-6 re-disable security property).
/// </summary>
/// <remarks>
/// <para>
/// <b>This test is NATIVE-GATED / skippable.</b> This Intel CI host has no bundled <c>vec0</c> native (per the
/// spike), so <see cref="Vec0Native.ResolveNativePath"/> returns null and the assertions that REQUIRE the
/// native return early as a documented skip — the test is GREEN-as-skip here. On a host WITH the native
/// (po-mac Mac native run + po-win Windows R-6 probe — the FLAGGED follow-on), it RUNS the real parity +
/// re-disable checks. The clip semantics are identical to the brute-force engine (both build their
/// <c>record_id</c> WHERE via <see cref="VecRecordClip"/>); this proves the NATIVE applies that constraint as a
/// true pre-filter, which the brute-force suite cannot.
/// </para>
/// <para>
/// <b>DO NOT enable <c>HARBORLINE_KG_VEC0_REAL</c> on any shipped host until this passes on that host's native.</b>
/// </para>
/// </remarks>
[Collection(Vec0NativeEnvironmentCollection.Name)]
public sealed class Vec0NativeParityF2Tests
{
    private const int Dim = 64;
    private static readonly TenantId TenantA = TenantId.FromString("tenant-A");

    /// <summary>
    /// True when the real vec0 native is available to load on this host — the precondition for the parity
    /// probe. False on a host without the bundled native (this Intel CI host) ⇒ the native-requiring tests
    /// return early as a documented skip.
    /// </summary>
    private static bool NativeAvailable()
    {
        // The native is only loadable when opted-in AND a path resolves. We DON'T require the opt-in env var to
        // be set for the test to RUN the probe (the test opts in itself below); we only need the native present.
        return Vec0Native.ResolveNativePath() is not null;
    }

    [Fact(DisplayName = "F2 (host-gated): the vec0 native, WHEN present, loads on a keyed SQLCipher connection and re-disables load_extension (spike R-6)")]
    public async Task Vec0_Native_Loads_And_ReDisables_Load_Extension()
    {
        if (!NativeAvailable())
        {
            // SKIP on a host without the native (this Intel CI host). The brute-force engine carries the
            // security semantics here; this probe is the po-mac/po-win follow-on. Documented green-as-skip.
            Assert.Null(Vec0Native.ResolveNativePath());
            return;
        }

        // Opt in for the duration of this probe (the test arms the native path it is validating).
        Environment.SetEnvironmentVariable(Vec0Native.RealVecEnvVar, "1");
        try
        {
            await using var h = await VecTestHarness.CreateAsync();
            await using var ctx = h.Store.CreateContext();
            var connection = (SqliteConnection)ctx.Database.GetDbConnection();
            await connection.OpenAsync();

            // The native loads on the OPEN, KEYED (PRAGMA key) connection — the spike-R-6 property.
            var loaded = Vec0Native.TryLoad(connection);
            Assert.True(loaded, "vec0 native is present but failed to load on the keyed connection");

            // SECURITY (spike R-6): after the trusted load, load_extension(...) must be UNREACHABLE from SQL.
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT load_extension('definitely-not-a-real-extension');";
            // The re-disable means this throws (load_extension is not callable), NOT that it silently loads
            // an arbitrary library. Any exception is the correct fail-closed outcome.
            await Assert.ThrowsAnyAsync<Exception>(() => cmd.ExecuteScalarAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Vec0Native.RealVecEnvVar, null);
        }
    }

    [Fact(DisplayName = "F2 (host-gated): vec0 applies tenant + record clip as TRUE pre-filters (no neighbour leak)")]
    public async Task Vec0_Native_Knn_Clip_Is_A_True_PreFilter()
    {
        if (!NativeAvailable())
        {
            // SKIP — the parity probe requires the native. Green-as-skip on this host (po-mac/po-win follow-on).
            Assert.Null(Vec0Native.ResolveNativePath());
            return;
        }

        Environment.SetEnvironmentVariable(Vec0Native.RealVecEnvVar, "1");
        try
        {
            await using var h = await VecTestHarness.CreateAsync();

            // Index two records with a real acceleration sink driving the vec0 table; one forbidden record
            // embeds the EXACT query text (globally nearest), one allowed record embeds far text.
            await using var ctx = h.Store.CreateContext();
            var connection = (SqliteConnection)ctx.Database.GetDbConnection();
            await connection.OpenAsync();
            Assert.True(Vec0Native.TryLoad(connection));

            var engine = new Vec0KnnEngine(Dim);
            await engine.EnsureTableAsync(connection, CancellationToken.None);

            // Seed the vec0 table directly (the acceleration sink's role) — forbidden row = exact query code.
            var stub = h.StubEmbedder(Dim);
            var qVec = (await stub.EmbedAsync("__q__", "tenant-A", null, "the exact query phrase here")).Vector;
            var allowedVec = (await stub.EmbedAsync("__a__", "tenant-A", null, "completely different text")).Vector;
            await InsertVec0Row(connection, "inv-allowed", "tenant-A", BinaryQuantization.Pack(allowedVec));
            await InsertVec0Row(connection, "inv-forbidden", "tenant-A", BinaryQuantization.Pack(qVec));
            await InsertVec0Row(connection, "inv-other-tenant", "tenant-B", BinaryQuantization.Pack(qVec));

            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync();

            // The record scope authorizes inv-allowed and a globally-nearest row from another tenant. Both the
            // tenant metadata constraint and record clip must be true prefilters: the only result is the farther
            // same-tenant row. The same-tenant inv-forbidden row proves the record clip independently.
            var scope = AuthorizedRecordScope.ForRecordIds(new[] { "inv-allowed", "inv-other-tenant" });
            var hits = await engine.KnnAsync(connection, tx, "tenant-A", scope, qVec, 5, CancellationToken.None);

            var ids = hits.Select(x => x.RecordId).ToArray();
            Assert.Equal(new[] { "inv-allowed" }, ids); // no neighbour leak — the spike R-1 property on the native.
        }
        finally
        {
            Environment.SetEnvironmentVariable(Vec0Native.RealVecEnvVar, null);
        }
    }

    private static async Task InsertVec0Row(
        SqliteConnection connection, string recordId, string tenantId, byte[] code)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Vec0AccelerationSink.UpsertCommandText;
        cmd.Parameters.AddWithValue("$r", recordId);
        cmd.Parameters.AddWithValue("$t", tenantId);
        cmd.Parameters.AddWithValue("$e", code);
        await cmd.ExecuteNonQueryAsync();
    }
}
